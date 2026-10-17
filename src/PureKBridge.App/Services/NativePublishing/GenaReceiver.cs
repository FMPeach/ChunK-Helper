using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PureKBridge.App.Services.NativePublishing;

/// <summary>Only the current renderer may deliver GENA events to unguessable session paths. Not a media/SSDP server.</summary>
internal sealed class GenaReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, RemoteServiceRelay> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly HashSet<IPAddress> _peers;
    private readonly Action<Exception> _failed;
    private readonly SemaphoreSlim _slots = new(16);
    private readonly Task _accept;
    private long _next;
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    public Uri BaseUrl { get; }

    public GenaReceiver(IPAddress local, IEnumerable<IPAddress> peers, Action<Exception> failed)
    {
        _failed = failed;
        _peers = peers.Select(ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToHashSet();
        _listener = new(local, 0); _listener.Start(16);
        BaseUrl = new Uri($"http://{local}:{((IPEndPoint)_listener.LocalEndpoint).Port}/events/{_nonce}/");
        _accept = AcceptAsync();
    }
    public Uri Add(int index, RemoteServiceRelay service)
    {
        var url = new Uri(BaseUrl, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!_routes.TryAdd(url.AbsolutePath, service)) throw new InvalidOperationException("事件路径重名。");
        return url;
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                var id = Interlocked.Increment(ref _next);
                var task = HandleAsync(client); _clients[id] = task;
                _ = task.ContinueWith(_ => { _clients.TryRemove(id, out var ignored); _slots.Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { if (!_stop.IsCancellationRequested) _failed(new IOException("房台事件接收通道异常。")); }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var peer = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
                if (!_peers.Contains(peer)) return;
                await using var stream = client.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1]; var complete = false;
                while (header.Length < 16384)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0) return;
                    header.WriteByte(one[0]); var bytes = header.GetBuffer(); var n = (int)header.Length;
                    if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10)
                    { complete = true; break; }
                }
                if (!complete) return;
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var start = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2 || !headers.TryAdd(parts[0].Trim(), parts[1].Trim())) return;
                }
                var status = 400;
                if (start.Length == 3 && start[0] == "NOTIFY" && _routes.TryGetValue(start[1], out var service) &&
                    headers.GetValueOrDefault("NT") == "upnp:event" && headers.GetValueOrDefault("NTS") == "upnp:propchange" &&
                    !headers.ContainsKey("Transfer-Encoding") && int.TryParse(headers.GetValueOrDefault("Content-Length"), out var length) &&
                    length is >= 0 and <= 1024 * 1024)
                {
                    var bytes = new byte[length];
                    await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                    try { status = service.AcceptEvent(headers, new UTF8Encoding(false, true).GetString(bytes)) ? 200 : 412; }
                    catch (Exception error) when (error is System.Xml.XmlException or InvalidDataException or FormatException or DecoderFallbackException or OverflowException)
                    { status = 400; }
                }
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Rejected")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
            catch (Exception) { if (!_stop.IsCancellationRequested) _failed(new IOException("房台事件处理失败。")); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false); _listener.Stop();
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _routes.Clear(); _slots.Dispose(); _stop.Dispose();
    }
}
