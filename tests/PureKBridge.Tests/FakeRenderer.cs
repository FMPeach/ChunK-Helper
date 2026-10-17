using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PureKBridge.Tests;

internal sealed record TestRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);
internal sealed record TestResponse(int Status, string Body, string ContentType = "text/xml; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Test-only HTTP renderer. No discovery, forwarding, or real device control.</summary>
internal sealed class FakeRenderer : IAsyncDisposable
{
    public const string Udn = "uuid:00000000-0000-4000-8000-000000000013";
    public const string Manager = "urn:schemas-upnp-org:service:ConnectionManager:1";
    public const string ManagerPath = "/device/_urn:schemas-upnp-org:service:ConnectionManager_control";
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly Task _accept;
    private readonly string _udn;
    public Uri BaseUrl { get; }
    public ConcurrentQueue<TestRequest> Requests { get; } = new();
    public Func<TestRequest, Task<TestResponse>>? Handler { get; set; }

    public FakeRenderer(IPAddress? address = null, string? udn = null, IPAddress? listenAddress = null, int port = 0)
    {
        address ??= IPAddress.Loopback;
        _udn = udn ?? Udn;
        _listener = new(listenAddress ?? address, port);
        _listener.Start();
        var boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = new Uri($"http://{address}:{boundPort}/device/");
        _accept = AcceptAsync();
    }

    public static string DescriptionXml(string? udn = null) => """
        <?xml version="1.0" encoding="utf-8"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <friendlyName>ChunK Helper 本地测试设备</friendlyName>
            <manufacturer>ChunK Helper tests</manufacturer><modelName>Fake Renderer</modelName>
            <UDN>UDN_PLACEHOLDER</UDN>
            <serviceList>
              <service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                <controlURL>av/control</controlURL><eventSubURL>av/event</eventSubURL><SCPDURL>av.xml</SCPDURL></service>
              <service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                <controlURL>_urn:schemas-upnp-org:service:ConnectionManager_control</controlURL>
                <eventSubURL>cm/event</eventSubURL><SCPDURL>cm.xml</SCPDURL></service>
              <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId>
                <controlURL>rc/control</controlURL><eventSubURL>rc/event</eventSubURL><SCPDURL>rc.xml</SCPDURL></service>
              <service><serviceType>urn:schemas-upnp-org:service:GeneralControl:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:GeneralControl</serviceId>
                <controlURL>gc/control</controlURL><eventSubURL>gc/event</eventSubURL><SCPDURL>gc.xml</SCPDURL></service>
            </serviceList>
          </device>
        </root>
        """.Replace("UDN_PLACEHOLDER", udn ?? Udn);

    public static string AuthorizationResponse => $"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><u:SetDeviceAuthorizationResponse xmlns:u='{Manager}' /></s:Body></s:Envelope>";

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                _clients.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_cancellation.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await using var stream = client.GetStream();
                using var header = new MemoryStream();
                var single = new byte[1];
                while (header.Length < 16384)
                {
                    if (await stream.ReadAsync(single, timeout.Token).ConfigureAwait(false) == 0) return;
                    header.WriteByte(single[0]);
                    var buffer = header.GetBuffer();
                    var count = (int)header.Length;
                    if (count >= 4 && buffer[count - 4] == 13 && buffer[count - 3] == 10 && buffer[count - 2] == 13 && buffer[count - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var start = lines[0].Split(' ');
                var headers = lines.Skip(1).Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
                if (length > 65536) return;
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                var request = new TestRequest(start[0], start[1], headers, Encoding.UTF8.GetString(body));
                Requests.Enqueue(request);
                var response = Handler is null ? request.Method == "GET" && request.Path.StartsWith("/diagnostic-phase/", StringComparison.Ordinal)
                    ? new TestResponse(200, "ok", "text/plain") : request.Method == "GET" && request.Path == "/device/description.xml"
                    ? new TestResponse(200, DescriptionXml(_udn)) : request.Method == "POST" && request.Path == ManagerPath
                        ? new TestResponse(200, AuthorizationResponse) : request.Method == "GET" && request.Path.EndsWith(".xml")
                            ? new TestResponse(200, "<scpd xmlns='urn:schemas-upnp-org:service-1-0'><specVersion><major>1</major><minor>0</minor></specVersion><actionList/><serviceStateTable><stateVariable sendEvents='no'><name>TestState</name><dataType>string</dataType></stateVariable></serviceStateTable></scpd>") : new TestResponse(404, "not found")
                    : await Handler(request).ConfigureAwait(false);
                var content = Encoding.UTF8.GetBytes(response.Body);
                var extra = response.Headers is null ? "" : string.Concat(response.Headers.Select(pair => pair.Key + ": " + pair.Value + "\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Test\r\nContent-Type: {response.ContentType}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extra}\r\n"), timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(content, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(12));
        _cancellation.Dispose();
    }
}
