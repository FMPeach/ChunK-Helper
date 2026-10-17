using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PureKBridge.App.Services.NativePublishing;
using PureKBridge.Core.Network;
using PureKBridge.Core.Qr;

namespace PureKBridge.Tests;

/// <summary>Real native publication against a fake upstream. No real room or authorization token.</summary>
internal static class NativeBridgeIntegration
{
    private const string Type = "urn:schemas-upnp-org:service:AVTransport:1";
    public static async Task RunAsync(string artifacts, bool serve, string[] args)
    {
        var bindIndex = Array.IndexOf(args, "--bind");
        if (bindIndex < 0 || bindIndex + 1 == args.Length || !IPAddress.TryParse(args[bindIndex + 1], out var address) ||
            IPAddress.IsLoopback(address) || !NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == OperationalStatus.Up && nic.GetIPProperties().UnicastAddresses.Any(ip => ip.Address.Equals(address))))
            throw new ArgumentException("原生发布网络测试需显式指定本机 LAN IPv4：--bind <本机地址>。");
        await using var upstream = new FakeRenderer(address);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        var sid = "uuid:" + Guid.NewGuid();
        Uri? callback = null;
        var value = "ready";
        var initialTasks = new List<Task>();
        async Task NotifyAsync(uint sequence, string state)
        {
            var body = new XElement(XName.Get("propertyset", "urn:schemas-upnp-org:event-1-0"),
                new XElement(XName.Get("property", "urn:schemas-upnp-org:event-1-0"), new XElement("Value", state)));
            using var notification = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
                { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml") };
            notification.Headers.TryAddWithoutValidation("NT", "upnp:event");
            notification.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
            notification.Headers.TryAddWithoutValidation("SID", sid);
            notification.Headers.TryAddWithoutValidation("SEQ", sequence.ToString());
            using var response = await http.SendAsync(notification).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        var description = FakeRenderer.DescriptionXml("uuid:" + Guid.NewGuid());
        var document = XDocument.Parse(description);
        var list = document.Descendants().Single(x => x.Name.LocalName == "serviceList");
        foreach (var service in list.Elements().Skip(1).ToArray()) service.Remove();
        description = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + document.Root;
        upstream.Handler = request =>
        {
            if (request.Method == "GET") return Task.FromResult(new TestResponse(200,
                request.Path.EndsWith("description.xml") ? description : HostedServiceProbe.Scpd));
            if (request.Method == "SUBSCRIBE")
            {
                if (request.Headers.TryGetValue("CALLBACK", out var target))
                {
                    callback = new Uri(target.Trim('<', '>'));
                    initialTasks.Add(Task.Run(async () => { await Task.Delay(50); await NotifyAsync(0, value); }));
                }
                return Task.FromResult(new TestResponse(200, "", Headers: new Dictionary<string, string> { ["SID"] = sid, ["TIMEOUT"] = "Second-300" }));
            }
            if (request.Method == "UNSUBSCRIBE") return Task.FromResult(new TestResponse(200, ""));
            if (request.Method == "POST")
            {
                var input = XDocument.Parse(request.Body).Descendants().Single(x => x.Name.LocalName == "Input").Value;
                var output = new XElement(XName.Get("EchoResponse", Type), new XElement("Output", "echo:" + input));
                return Task.FromResult(new TestResponse(200, "<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body>" + output + "</s:Body></s:Envelope>"));
            }
            return Task.FromResult(new TestResponse(404, "not found"));
        };
        var room = new PureKQrParser().Parse(ProtocolTests.Qr(upstream.BaseUrl));
        var renderer = new KnownRenderer(room, address.ToString(), RendererClient.ParseDescription(description, room.DescriptionUrl)!);
        var bridge = new WindowsDeviceHostBridge(new ProtocolTests.Routes(address), (text, error) => Console.WriteLine(text + (error is null ? "" : ": " + error.Message)));
        string? nativeUdn = null, location = null;
        var discovered = false; var controlled = false; var initialEvent = false; var changedEvent = false; var removed = false;
        NativeDiscoveryProbe? observer = null;
        try
        {
            await bridge.StartAsync(renderer);
            nativeUdn = bridge.PublishedUdn!;
            observer = new NativeDiscoveryProbe(nativeUdn);
            await observer.Callback.Found.Task.WaitAsync(TimeSpan.FromSeconds(15));
            location = await observer.Callback.DocumentUrl.Task.WaitAsync(TimeSpan.FromSeconds(3));
            discovered = true;
            var published = XDocument.Parse(await http.GetStringAsync(location));
            var control = new Uri(new Uri(location), published.Descendants().Single(x => x.Name.LocalName == "controlURL").Value);
            var eventUrl = new Uri(new Uri(location), published.Descendants().Single(x => x.Name.LocalName == "eventSubURL").Value);
            var original = "http://192.0.2.1:8080/日本語.mp4?x=1&y=<hello>";
            var action = new XElement(XName.Get("Echo", Type), new XElement("Input", original));
            using var request = new HttpRequestMessage(HttpMethod.Post, control)
                { Content = new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body>" + action + "</s:Body></s:Envelope>", Encoding.UTF8, "text/xml") };
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + Type + "#Echo\"");
            using var result = await http.SendAsync(request);
            var resultBody = await result.Content.ReadAsStringAsync();
            result.EnsureSuccessStatusCode();
            controlled = XDocument.Parse(resultBody).Descendants().Single(x => x.Name.LocalName == "Output").Value == "echo:" + original;
            if (!controlled) throw new InvalidOperationException("Relay altered SOAP payload.");
            await using var subscriber = new FakeRenderer();
            subscriber.Handler = _ => Task.FromResult(new TestResponse(200, ""));
            using var subscription = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), eventUrl);
            subscription.Headers.TryAddWithoutValidation("CALLBACK", "<" + new Uri(subscriber.BaseUrl, "notify") + ">");
            subscription.Headers.TryAddWithoutValidation("NT", "upnp:event");
            subscription.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
            using var subscribed = await http.SendAsync(subscription); subscribed.EnsureSuccessStatusCode();
            for (var i = 0; i < 150 && !subscriber.Requests.Any(x => x.Body.Contains("ready")); i++) await Task.Delay(20);
            initialEvent = subscriber.Requests.Any(x => x.Method == "NOTIFY" && x.Body.Contains("ready"));
            value = "changed"; await NotifyAsync(1, value);
            for (var i = 0; i < 150 && !subscriber.Requests.Any(x => x.Body.Contains("changed")); i++) await Task.Delay(20);
            changedEvent = subscriber.Requests.Any(x => x.Method == "NOTIFY" && x.Body.Contains("changed"));
            if (!initialEvent || !changedEvent) throw new InvalidOperationException("Relayed native event was not delivered.");
            using var unsubscribe = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), eventUrl);
            unsubscribe.Headers.TryAddWithoutValidation("SID", subscribed.Headers.GetValues("SID").Single());
            using var unsubscribed = await http.SendAsync(unsubscribe); unsubscribed.EnsureSuccessStatusCode();
            var readyPath = Path.Combine(artifacts, "native-bridge-ready.json");
            await File.WriteAllTextAsync(readyPath, JsonSerializer.Serialize(new { udn = nativeUdn, location, readyAt = DateTimeOffset.Now, expires = DateTimeOffset.Now.AddMinutes(3) }));
            Console.WriteLine("NATIVE_BRIDGE_READY " + nativeUdn);
            if (serve) await Task.Delay(TimeSpan.FromMinutes(3));
            await bridge.StopAsync();
            await observer.Callback.Removed.Task.WaitAsync(TimeSpan.FromSeconds(15)); removed = true;
        }
        finally
        {
            await bridge.StopAsync();
            await Task.WhenAll(initialTasks);
            var report = new { nativeUdn, location, discovered, controlled, initialEvent, changedEvent, removed,
                upstreamUnsubscribed = upstream.Requests.Any(x => x.Method == "UNSUBSCRIBE"), events = observer?.Callback.Events };
            await File.WriteAllTextAsync(Path.Combine(artifacts, "native-bridge-integration.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            observer?.Dispose();
        }
    }
}
