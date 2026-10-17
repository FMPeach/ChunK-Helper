using PureKBridge.Core.Models;
using PureKBridge.Core.Network;

namespace PureKBridge.Core.PureK;

public sealed record ConnectionProgress(string Message);
public sealed record ConnectionResult(bool IsSimulated, string? LocalAddress, KnownRenderer? Renderer = null);

public interface IPureKConnectionService
{
    bool IsSimulation { get; }
    event Action<ConnectionProgress>? ProgressChanged;
    Task<ConnectionResult> ConnectAsync(PureKRoomInfo room, CancellationToken cancellationToken);
    Task DisconnectAsync(PureKRoomInfo room, CancellationToken cancellationToken);
}

public sealed class MockPureKConnectionService(ILocalRouteResolver routes) : IPureKConnectionService
{
    public bool IsSimulation => true;
    public event Action<ConnectionProgress>? ProgressChanged;

    public async Task<ConnectionResult> ConnectAsync(PureKRoomInfo room, CancellationToken cancellationToken)
    {
        foreach (var message in new[] { "正在连接 Renderer...", "正在登记 Windows 设备...", "正在确认房台状态..." })
        {
            ProgressChanged?.Invoke(new(message));
            await Task.Delay(550, cancellationToken);
        }
        string? local = null;
        try { local = (await routes.ResolveAsync(room.RendererBaseUrl, cancellationToken)).ToString(); }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException) { }
        cancellationToken.ThrowIfCancellationRequested();
        return new(true, local);
    }

    public Task DisconnectAsync(PureKRoomInfo room, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PureKConnectionService(IRendererClient rendererClient, ILocalRouteResolver routes,
    Action<string, Exception?>? log = null, IDeviceAuthorizationClient? authorizationClient = null)
    : IPureKConnectionService, IDisposable
{
    private readonly IDeviceAuthorizationClient _authorization = authorizationClient ?? new DeviceAuthorizationClient();
    private readonly SemaphoreSlim _gate = new(1);
    private KnownRenderer? _registered;
    public bool IsSimulation => false;
    public event Action<ConnectionProgress>? ProgressChanged;

    public async Task<ConnectionResult> ConnectAsync(PureKRoomInfo room, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        KnownRenderer? pending = null;
        try
        {
            if (_registered is not null) await RemoveRegistrationAsync(_registered).ConfigureAwait(false);
            _registered = null;
            ProgressChanged?.Invoke(new("正在连接 Renderer..."));
            var address = await routes.ResolveAsync(room.RendererBaseUrl, cancellationToken).ConfigureAwait(false);
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new DeviceConnectionException("当前发布功能需要可访问房台的 IPv4 网络地址。");
            var probe = await rendererClient.ProbeAsync(room, cancellationToken).ConfigureAwait(false);
            if (!probe.IsReachable) throw new DeviceConnectionException("Renderer 无法访问，请检查是否连接到房台所在网络。");
            if (probe.Description is not { } description || !probe.IsMediaRenderer)
                throw new DeviceConnectionException("房台没有返回可确认身份的 MediaRenderer 描述。");
            pending = new(room, address.ToString(), description);
            log?.Invoke($"Renderer：{room.RendererBaseUrl.Authority}；UDN={description.Udn}；名称={description.FriendlyName}；Local IP={address}", null);
            ProgressChanged?.Invoke(new("正在登记 Windows 设备..."));
            // Retain this exact address for Enable=0 even after a network change.
            await _authorization.SetDeviceAuthorizationAsync(pending, true, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke("SetDeviceAuthorization Enable=1 成功", null);
            ProgressChanged?.Invoke(new("正在读取设备信息..."));
            var confirmed = await rendererClient.ProbeAsync(room, cancellationToken).ConfigureAwait(false);
            if (confirmed is not { IsReachable: true, IsMediaRenderer: true, Description: not null } ||
                !confirmed.Description.Udn.Equals(description.Udn, StringComparison.OrdinalIgnoreCase))
                throw new DeviceConnectionException("登记后未能确认原房台仍在线，请重新连接。");
            if (confirmed.Description.ConnectionManager?.ControlUrl != description.ConnectionManager?.ControlUrl)
                throw new DeviceConnectionException("房台登记接口发生变化，请重新识别房间。");
            pending = pending with { Description = confirmed.Description };
            _registered = pending;
            return new(false, pending.LocalAddress, pending);
        }
        catch
        {
            // Cancellation can arrive after the HTTP request reached the renderer.
            if (pending is not null) await RemoveRegistrationAsync(pending).ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(PureKRoomInfo room, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_registered is not { } registration || registration.Room.OriginalQrUrl != room.OriginalQrUrl) return;
            _registered = null;
            await RemoveRegistrationAsync(registration).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task RemoveRegistrationAsync(KnownRenderer renderer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await _authorization.SetDeviceAuthorizationAsync(renderer, false, timeout.Token).ConfigureAwait(false);
            log?.Invoke("SetDeviceAuthorization Enable=0 成功", null);
        }
        catch (Exception error) { log?.Invoke("撤销房台登记未确认；已停止本机发布", error); }
    }

    public void Dispose() => (_authorization as IDisposable)?.Dispose();
}
