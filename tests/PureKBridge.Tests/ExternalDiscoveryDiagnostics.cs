using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PureKBridge.Tests;

/// <summary>Temporary cross-machine experiment. Does not bind port 1900 or alter system settings.</summary>
internal static class ExternalDiscoveryDiagnostics
{
    private sealed record Fixture(string RunId, string HostAddress, string GuestAddress, int HttpPort,
        int DiagnosticPort, string Udn, string DescriptionSha256);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string Bundle(string artifacts) => Path.Combine(artifacts, "vm-discovery-test");

    public static async Task PrepareAsync(string artifacts, string[] args)
    {
        string Argument(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
            return args[index + 1];
        }
        var host = IPAddress.Parse(Argument("--bind"));
        var guest = IPAddress.Parse(Argument("--guest"));
        ValidateLocal(host);
        if (guest.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(guest) || host.Equals(guest))
            throw new ArgumentException("--guest must be a different non-loopback IPv4 address.");
        var directory = Bundle(artifacts);
        Directory.CreateDirectory(directory);
        var udn = "uuid:" + Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(FakeRenderer.DescriptionXml(udn));
        var fixture = new Fixture(Guid.NewGuid().ToString("N"), host.ToString(), guest.ToString(), 49152, 49153,
            udn, Convert.ToHexString(SHA256.HashData(bytes)));
        await File.WriteAllBytesAsync(Path.Combine(directory, "description.xml"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(fixture, Json));
        var repo = Directory.GetParent(artifacts)!.Parent!.FullName;
        File.Copy(Path.Combine(repo, "tests", "tools", "run-vm-discovery-test.ps1"),
            Path.Combine(directory, "run-vm-discovery-test.ps1"), true);
        File.Copy(Path.Combine(repo, "tests", "tools", "VM发现测试说明.txt"), Path.Combine(directory, "README.txt"), true);
        var zip = Path.Combine(artifacts, "ChunK-SSDP-VM-Test.zip");
        // Regenerating the ignored diagnostic archive is intentional; no repository files are removed.
        if (File.Exists(zip)) File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var file in new[] { "description.xml", "fixture.json", "run-vm-discovery-test.ps1", "README.txt" })
                archive.CreateEntryFromFile(Path.Combine(directory, file), file);
        Console.WriteLine("VM bundle: " + zip);
        Console.WriteLine("UDN: " + fixture.Udn);
        Console.WriteLine("XML SHA-256: " + fixture.DescriptionSha256);
    }

    public static async Task ObserveAsync(string artifacts)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Path.Combine(Bundle(artifacts), "fixture.json")))!;
        var host = IPAddress.Parse(fixture.HostAddress);
        var guest = IPAddress.Parse(fixture.GuestAddress);
        ValidateLocal(host);
        await using var fake = new FakeRenderer(host, fixture.Udn, port: fixture.HttpPort);
        using var diagnostic = new UdpClient(new IPEndPoint(IPAddress.Any, fixture.DiagnosticPort));
        diagnostic.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), host);
        using var observer = new NativeDiscoveryProbe(fixture.Udn);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var events = new List<object>();
        var phase = "waiting";
        var callbackCount = 0;
        var httpCount = 0;
        var mirrorSeen = false;
        var guestProbeStarted = false;
        Task<object>? guestHttpProbe = null;
        DateTimeOffset? completedAt = null;
        var receive = diagnostic.ReceiveAsync(timeout.Token).AsTask();
        Console.WriteLine($"OBSERVER READY: host={host}, guest={guest}, UDN={fixture.Udn}");
        Console.WriteLine("Waiting up to 5 minutes. Run the extracted PowerShell test in the VM now.");
        try
        {
            while (!timeout.IsCancellationRequested &&
                   (completedAt is null || DateTimeOffset.Now - completedAt < TimeSpan.FromSeconds(10)))
            {
                if (receive.IsCompleted)
                {
                    var packet = await receive;
                    receive = diagnostic.ReceiveAsync(timeout.Token).AsTask();
                    if (packet.RemoteEndPoint.Address.Equals(guest))
                    {
                        var text = Encoding.ASCII.GetString(packet.Buffer);
                        if (text.StartsWith("CHUNK_TEST|" + fixture.RunId + "|", StringComparison.Ordinal))
                        {
                            var fields = text.Split('|');
                            SetPhase(fields.Length > 2 ? fields[2] : "unknown");
                            Record("guest-marker", text);
                        }
                        else if (!mirrorSeen && text.StartsWith("NOTIFY * HTTP/1.1\r\n", StringComparison.Ordinal) &&
                                 text.Contains(fixture.Udn, StringComparison.Ordinal))
                        {
                            mirrorSeen = true;
                            Record("diagnostic-multicast", "VM multicast copy received on port 49153; this does not prove port 1900 delivery.");
                        }
                    }
                }
                var callbacks = observer.Callback.Events;
                while (callbackCount < callbacks.Length) Record("windows-callback", callbacks[callbackCount++]);
                var requests = fake.Requests.ToArray();
                while (httpCount < requests.Length)
                {
                    var request = requests[httpCount++];
                    var markerPrefix = "/diagnostic-phase/" + fixture.RunId + "/";
                    if (request.Path.StartsWith(markerPrefix, StringComparison.Ordinal))
                        SetPhase(request.Path[markerPrefix.Length..].Split('/')[0]);
                    Record("host-http", request.Method + " " + request.Path + " UA=" + request.Headers.GetValueOrDefault("User-Agent"));
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally
        {
            await timeout.CancelAsync();
            try { await receive; } catch (OperationCanceledException) { }
            var report = new { timestamp = DateTimeOffset.Now, fixture, completed = completedAt is not null,
                nativeFound = observer.Callback.Found.Task.IsCompletedSuccessfully, diagnosticMulticastSeen = mirrorSeen,
                guestHttp = guestHttpProbe is null ? null : await guestHttpProbe, events,
                caveat = "Port 49153 is only an independent network marker/multicast copy. It does not capture Windows SSDPSRV port 1900. No system settings or production logic changed." };
            var path = Path.Combine(artifacts, "vm-discovery-observer.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json));
            Console.WriteLine("Observer report: " + path);
        }

        void Record(string type, string detail)
        {
            var timestamp = DateTimeOffset.Now;
            events.Add(new { timestamp, phase, type, detail });
            Console.WriteLine($"[{timestamp:HH:mm:ss.fff}][{phase}][{type}] {detail}");
        }

        void SetPhase(string value)
        {
            phase = value;
            if (!guestProbeStarted)
            {
                guestProbeStarted = true;
                guestHttpProbe = ProbeGuestAsync(fixture);
            }
            if (phase == "COMPLETE") completedAt = DateTimeOffset.Now;
        }
    }

    private static async Task<object> ProbeGuestAsync(Fixture fixture)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChunKABConnectivityProbe/1.0");
            var bytes = await client.GetByteArrayAsync($"http://{fixture.GuestAddress}:{fixture.HttpPort}/device/description.xml");
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            Console.WriteLine("Guest HTTP connectivity: OK, XML hash matches=" + (sha256 == fixture.DescriptionSha256));
            return new { ok = true, sha256, hashMatches = sha256 == fixture.DescriptionSha256 };
        }
        catch (Exception error)
        {
            Console.WriteLine("Guest HTTP connectivity: " + error.Message);
            return new { ok = false, error = error.GetType().Name + ": " + error.Message };
        }
    }

    private static void ValidateLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            !NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.OperationalStatus == OperationalStatus.Up &&
                nic.GetIPProperties().UnicastAddresses.Any(entry => entry.Address.Equals(address))))
            throw new ArgumentException("Host bind address must be assigned locally.");
    }
}
