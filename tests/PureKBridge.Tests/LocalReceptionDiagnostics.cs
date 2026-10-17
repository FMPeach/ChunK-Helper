using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PureKBridge.Core.Network;

namespace PureKBridge.Tests;

/// <summary>Diagnostic-only socket matrix. Never binds SSDP port 1900 or changes a system setting.</summary>
internal static class LocalReceptionDiagnostics
{
    public static async Task RunAsync(string artifacts, string[] args)
    {
        IPAddress Address(string name)
        {
            var i = Array.IndexOf(args, name);
            if (i < 0 || i + 1 >= args.Length || !IPAddress.TryParse(args[i + 1], out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
                !NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.OperationalStatus == OperationalStatus.Up &&
                    nic.GetIPProperties().UnicastAddresses.Any(entry => entry.Address.Equals(address))))
                throw new ArgumentException(name + " must be a locally assigned non-loopback IPv4 address.");
            return address;
        }
        var source = Address("--bind");
        var other = Address("--other");
        var loopResults = new List<object>();
        foreach (var senderLoop in new[] { false, true })
            foreach (var receiverLoop in new[] { false, true })
            {
                var received = await CheckLoopAsync(source, senderLoop, receiverLoop);
                loopResults.Add(new { senderLoop, receiverLoop, received });
                Console.WriteLine($"Ordinary socket: senderLoop={senderLoop}, receiverLoop={receiverLoop}, received={received}");
            }
        var cases = new List<object>();
        await CaseAsync("multicast-physical", source, source, IPAddress.Parse("239.255.255.250"), false);
        await CaseAsync("unicast-self-physical", source, source, source, false);
        await CaseAsync("unicast-cross-local-interface", other, other, source, false);
        await CaseAsync("multicast-virtual-interface", other, other, IPAddress.Parse("239.255.255.250"), false);
        await CaseAsync("native-preload-then-multicast", source, source, IPAddress.Parse("239.255.255.250"), true);
        var path = Path.Combine(artifacts, "local-reception-diagnostics.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, loopResults, cases,
            note = "All sources are local. MulticastLoopback evidence here applies to our ordinary sockets, not a measurement of SSDPSRV socket options. No native service/settings changes, real device calls, remote port scans, or port 1900 receiver." },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Report: " + path);

        async Task CaseAsync(string name, IPAddress local, IPAddress locationAddress, IPAddress destination, bool preload)
        {
            var udn = "uuid:" + Guid.NewGuid();
            await using var fake = new FakeRenderer(locationAddress, udn, IPAddress.Any);
            var location = new Uri(fake.BaseUrl, "description.xml");
            DiscoveryDiagnostics.LoadResult? loaded = null;
            if (preload) loaded = await Task.Run(() => DiscoveryDiagnostics.LoadDescription(location)).WaitAsync(TimeSpan.FromSeconds(12));
            var description = RendererClient.ParseDescription(FakeRenderer.DescriptionXml(udn), location)!;
            var identities = SsdpAdvertisement.Identities(description);
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.Bind(new IPEndPoint(local, 0));
            sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            sender.MulticastLoopback = true;
            using var observer = new NativeDiscoveryProbe(udn);
            var endpoint = new IPEndPoint(destination, 1900);
            Console.WriteLine($"{name}: source={sender.LocalEndPoint}, target={endpoint}, LOCATION={location}, UDN={udn}");
            var found = false;
            string? error = null;
            try
            {
                for (var round = 0; round < 3; round++)
                {
                    foreach (var identity in identities)
                        await sender.SendToAsync(SsdpAdvertisement.Alive(description, identity), SocketFlags.None, endpoint);
                    if (round < 2) await Task.Delay(300);
                }
                try { await observer.Callback.Found.Task.WaitAsync(TimeSpan.FromSeconds(12)); found = true; }
                catch (TimeoutException) { }
            }
            catch (Exception failure) when (failure is SocketException or IOException)
            { error = failure.GetType().Name + ": " + failure.Message; }
            finally
            {
                foreach (var identity in identities)
                {
                    try { await sender.SendToAsync(SsdpAdvertisement.ByeBye(identity), SocketFlags.None, endpoint); }
                    catch (SocketException failure) { error ??= "byebye: " + failure.Message; }
                }
                await Task.Delay(800);
            }
            var requests = fake.Requests.Select(request => new { request.Method, request.Path,
                host = request.Headers.GetValueOrDefault("Host"), userAgent = request.Headers.GetValueOrDefault("User-Agent") }).ToArray();
            var events = observer.Callback.Events;
            cases.Add(new { name, udn, source = local.ToString(), destination = endpoint.ToString(), location, loaded,
                nativeFound = found, error, events, requests });
            Console.WriteLine($"{name}: nativeFound={found}, HTTP={requests.Length}, error={error}, callbacks={string.Join("; ", events)}");
        }
    }

    private static async Task<bool> CheckLoopAsync(IPAddress source, bool senderLoop, bool receiverLoop)
    {
        var group = IPAddress.Parse("239.255.255.250");
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Any, 0));
        receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, source));
        receiver.MulticastLoopback = receiverLoop;
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(source, 0));
        sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, source.GetAddressBytes());
        sender.MulticastLoopback = senderLoop;
        var expected = Encoding.ASCII.GetBytes("CHUNK-loop-test-" + Guid.NewGuid());
        await sender.SendToAsync(expected, SocketFlags.None, new IPEndPoint(group, ((IPEndPoint)receiver.LocalEndPoint!).Port));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var buffer = new byte[2048];
            var packet = await receiver.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
            return buffer.AsSpan(0, packet.ReceivedBytes).SequenceEqual(expected);
        }
        catch (OperationCanceledException) { return false; }
    }
}
