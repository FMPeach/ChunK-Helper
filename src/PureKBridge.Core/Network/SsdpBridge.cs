using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PureKBridge.Core.Network;

public interface ISsdpBridge
{
    bool IsRunning { get; }
    event Action<Exception>? Failed;
    Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public sealed class DummySsdpBridge : ISsdpBridge
{
    public bool IsRunning => false;
    public event Action<Exception>? Failed { add { } remove { } }
    public Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

public interface ISsdpSender : IDisposable
{
    Task SendAsync(byte[] datagram, CancellationToken cancellationToken);
}

public interface ISsdpSenderFactory
{
    ISsdpSender Open(IPAddress localAddress);
}

public sealed class SocketSsdpSenderFactory : ISsdpSenderFactory
{
    public ISsdpSender Open(IPAddress localAddress) => new Sender(localAddress);

    private sealed class Sender : ISsdpSender
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private static readonly IPEndPoint Destination = new(IPAddress.Parse("239.255.255.250"), 1900);

        public Sender(IPAddress localAddress)
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                    item.OperationalStatus == OperationalStatus.Up && item.SupportsMulticast &&
                    item.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(localAddress)));
                if (nic is null || IPAddress.IsLoopback(localAddress))
                    throw new IOException("当前房台路由没有可用的组播网络接口。");
                _socket.Bind(new IPEndPoint(localAddress, 0));
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                _socket.MulticastLoopback = true;
            }
            catch { _socket.Dispose(); throw; }
        }

        public async Task SendAsync(byte[] datagram, CancellationToken cancellationToken) =>
            await _socket.SendToAsync(datagram.AsMemory(), SocketFlags.None, Destination, cancellationToken).ConfigureAwait(false);
        public void Dispose() => _socket.Dispose();
    }
}

public sealed record SsdpBridgeOptions
{
    public TimeSpan AliveInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BurstInterval { get; init; } = TimeSpan.FromMilliseconds(300);
    public int InitialRounds { get; init; } = 3;
}

public sealed class SsdpBridge(ILocalRouteResolver routes, Action<string, Exception?>? log = null,
    ISsdpSenderFactory? senderFactory = null, SsdpBridgeOptions? options = null) : ISsdpBridge
{
    private readonly ISsdpSenderFactory _senders = senderFactory ?? new SocketSsdpSenderFactory();
    private readonly SsdpBridgeOptions _options = options ?? new();
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private volatile bool _running;
    public bool IsRunning => _running;
    public event Action<Exception>? Failed;

    public async Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            if (!IPAddress.TryParse(renderer.LocalAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork)
                throw new IOException("尚未获取有效的本机 IPv4 地址。");
            var identities = SsdpAdvertisement.Identities(renderer.Description);
            var alive = identities.Select(identity => SsdpAdvertisement.Alive(renderer.Description, identity)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var sender = _senders.Open(local);
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = AdvertiseAsync(renderer, local, sender, alive, identities, ready, _cancellation.Token);
            await ready.Task.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task AdvertiseAsync(KnownRenderer renderer, IPAddress local, ISsdpSender sender,
        byte[][] alive, IReadOnlyList<SsdpIdentity> identities, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        using var changed = new SemaphoreSlim(0, 1);
        void OnNetworkChanged(object? source, EventArgs args)
        { try { changed.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        var published = false;
        var reason = "用户停止或会话取消";
        Exception? failure = null;
        async Task CheckRouteAsync()
        {
            var address = await routes.ResolveAsync(renderer.Room.RendererBaseUrl, cancellationToken).ConfigureAwait(false);
            if (!address.Equals(local)) throw new IOException("房台网络路由已切换，请重新连接。");
        }
        async Task AnnounceAsync()
        {
            await CheckRouteAsync().ConfigureAwait(false);
            foreach (var message in alive)
            {
                await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
                published = true;
            }
            log?.Invoke($"SSDP alive：{alive.Length} 条；接口地址={local}；UDN={renderer.Udn}", null);
        }
        try
        {
            log?.Invoke($"SSDP advertise start；LOCATION={renderer.Description.DescriptionUrl}", null);
            for (var round = 0; round < _options.InitialRounds; round++)
            {
                if (round > 0) await Task.Delay(_options.BurstInterval, cancellationToken).ConfigureAwait(false);
                await AnnounceAsync().ConfigureAwait(false);
            }
            _running = true;
            ready.TrySetResult();
            while (true)
            {
                var networkChanged = await changed.WaitAsync(_options.AliveInterval, cancellationToken).ConfigureAwait(false);
                if (networkChanged) await CheckRouteAsync().ConfigureAwait(false);
                else await AnnounceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { ready.TrySetCanceled(cancellationToken); }
        catch (Exception error)
        { failure = error; reason = "发布失败或网络路由变化"; ready.TrySetException(error); }
        finally
        {
            _running = false;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            if (published)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                foreach (var identity in identities)
                {
                    try { await sender.SendAsync(SsdpAdvertisement.ByeBye(identity), timeout.Token).ConfigureAwait(false); }
                    catch (Exception error) { log?.Invoke("SSDP byebye 未发出", error); }
                }
                log?.Invoke($"SSDP byebye：{identities.Count} 条；原因={reason}", null);
            }
            sender.Dispose();
            log?.Invoke("SSDP advertise stop", failure);
        }
        if (failure is not null && ready.Task.IsCompletedSuccessfully) Failed?.Invoke(failure);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _cancellation;
        var loop = _loop;
        _cancellation = null; _loop = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }
}
