using PureKBridge.Core.Models;

namespace PureKBridge.Core.Network;

public enum RendererHealth { RendererOnline, RendererSuspect, RendererOffline }
public sealed record KnownRenderer(PureKRoomInfo Room, string LocalAddress, RendererDescription Description)
{
    public string Udn => Description.Udn;
}

public interface IRendererHealthMonitor
{
    event Action<RendererHealth>? HealthChanged;
    Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record RendererHealthOptions
{
    public TimeSpan OnlineInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int FailureLimit { get; init; } = 5;
}

public sealed class RendererHealthMonitor(IRendererClient client, Action<string, Exception?>? log = null,
    RendererHealthOptions? options = null) : IRendererHealthMonitor
{
    private readonly RendererHealthOptions _options = options ?? new();
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    public event Action<RendererHealth>? HealthChanged;

    public async Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = MonitorAsync(renderer, _cancellation.Token);
        }
        finally { _gate.Release(); }
    }

    private async Task MonitorAsync(KnownRenderer renderer, CancellationToken cancellationToken)
    {
        void Publish(RendererHealth health)
        { log?.Invoke($"Health state：{health}", null); HealthChanged?.Invoke(health); }
        try
        {
            Publish(RendererHealth.RendererOnline);
            while (true)
            {
                // Connect just validated the description; avoid a redundant immediate download.
                await Task.Delay(_options.OnlineInterval, cancellationToken).ConfigureAwait(false);
                var online = false;
                for (var attempt = 0; attempt < _options.FailureLimit; attempt++)
                {
                    RendererProbeResult? result = null;
                    try { result = await client.ProbeAsync(renderer.Room, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error) { log?.Invoke("Renderer 探活失败", error); }
                    online = result is { IsReachable: true, IsMediaRenderer: true, Description: not null } &&
                        result.Description.Udn.Equals(renderer.Udn, StringComparison.OrdinalIgnoreCase);
                    if (online) break;
                    if (attempt == 0) Publish(RendererHealth.RendererSuspect);
                    if (attempt + 1 < _options.FailureLimit)
                        await Task.Delay(_options.RetryInterval, cancellationToken).ConfigureAwait(false);
                }
                Publish(online ? RendererHealth.RendererOnline : RendererHealth.RendererOffline);
                if (!online) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _cancellation;
        var loop = _loop;
        _cancellation = null;
        _loop = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }
}
