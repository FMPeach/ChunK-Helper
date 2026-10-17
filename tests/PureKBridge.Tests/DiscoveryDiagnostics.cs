using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PureKBridge.Core.Network;

namespace PureKBridge.Tests;

internal static class DiscoveryDiagnostics
{
    internal sealed record LoadResult(bool Loaded, string? Udn, string? FriendlyName, string? Error);
    private sealed record CaseResult(string Name, string SourceAddress, string Location, bool CachedBeforeSend,
        bool NativeFound, string? Error, string[] CallbackEvents, object[] HttpRequests);

    public static async Task RunAsync(string artifacts, string[] args)
    {
        IPAddress Address(string flag)
        {
            var index = Array.IndexOf(args, flag);
            if (index < 0 || index + 1 >= args.Length || !IPAddress.TryParse(args[index + 1], out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
                !NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.OperationalStatus == OperationalStatus.Up &&
                    nic.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address))))
                throw new ArgumentException(flag + " must be an assigned local non-loopback IPv4 address.");
            return address;
        }
        var source = Address("--bind");
        var other = Address("--other");
        if (source.Equals(other)) throw new ArgumentException("--other must differ from --bind");
        var loopback = await CheckMulticastLoopAsync(source);
        Console.WriteLine("Ordinary multicast loopback: " + loopback);

        LoadResult direct;
        object[] directRequests;
        await using (var directFake = new FakeRenderer(source, "uuid:" + Guid.NewGuid()))
        {
            var location = new Uri(directFake.BaseUrl, "description.xml");
            try { direct = await Task.Run(() => LoadDescription(location)).WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (Exception error) { direct = new(false, null, null, error.GetType().Name + ": " + error.Message); }
            directRequests = Requests(directFake);
            Console.WriteLine("Direct Windows description load: " + JsonSerializer.Serialize(direct));
        }

        var udn = "uuid:" + Guid.NewGuid();
        await using var fake = new FakeRenderer(source, udn, IPAddress.Any);
        var sameLocation = new Uri(fake.BaseUrl, "description.xml");
        var differentLocation = new UriBuilder(sameLocation) { Host = other.ToString() }.Uri;
        // The HTTP listener, XML, UDN, sender socket/source port and all headers except LOCATION stay unchanged.
        using var sender = new SocketSsdpSenderFactory().Open(source);
        var cases = new List<CaseResult>();
        cases.Add(await CaseAsync("A-different-local-IP", differentLocation));
        cases.Add(await CaseAsync("B-same-local-IP", sameLocation));
        var report = new { timestamp = DateTimeOffset.Now, sourceAddress = source.ToString(), otherLocalAddress = other.ToString(),
            ordinaryMulticastLoopback = loopback, directWindowsDescription = direct, directHttpRequests = directRequests,
            udn, cases, caveat = "Both HTTP addresses belong to this same Windows host. This isolates IP equality, not cross-machine identity or self-filtering. No production logic/system settings changed." };
        var path = Path.Combine(artifacts, "discovery-diagnostics.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Diagnostics: " + path);

        async Task<CaseResult> CaseAsync(string name, Uri location)
        {
            var description = RendererClient.ParseDescription(FakeRenderer.DescriptionXml(udn), location)!;
            var identities = SsdpAdvertisement.Identities(description);
            var before = fake.Requests.Count;
            bool found = false, cached = false;
            string? error = null;
            string[] events = [];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            NativeDiscoveryProbe? observer = null;
            Console.WriteLine($"{name}: source={source}, LOCATION={location}, UDN={udn}");
            try
            {
                observer = new(udn);
                await Task.Delay(250, deadline.Token);
                cached = observer.Callback.Found.Task.IsCompletedSuccessfully;
                for (var round = 0; round < 3; round++)
                {
                    foreach (var identity in identities)
                        await sender.SendAsync(SsdpAdvertisement.Alive(description, identity), deadline.Token);
                    if (round < 2) await Task.Delay(300, deadline.Token);
                }
                try { await observer.Callback.Found.Task.WaitAsync(TimeSpan.FromSeconds(12)); found = true; }
                catch (TimeoutException) { error = "No DeviceAdded during the observation window"; }
                events = observer.Callback.Events;
            }
            catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; }
            finally
            {
                foreach (var identity in identities)
                    await sender.SendAsync(SsdpAdvertisement.ByeBye(identity), CancellationToken.None);
                observer?.Dispose();
            }
            var requests = Requests(fake).Skip(before).ToArray();
            Console.WriteLine($"{name}: nativeFound={found}; HTTP requests={requests.Length}; events={string.Join("; ", events)}; error={error}");
            await Task.Delay(1000);
            return new(name, source.ToString(), location.AbsoluteUri, cached, found, error, events, requests);
        }
    }

    private static object[] Requests(FakeRenderer fake) => fake.Requests.Select(request => (object)new
    { request.Method, request.Path, userAgent = request.Headers.GetValueOrDefault("User-Agent"), host = request.Headers.GetValueOrDefault("Host") }).ToArray();

    internal static LoadResult LoadDescription(Uri location)
    {
        object? document = null, device = null;
        try
        {
            var type = Type.GetTypeFromProgID("UPnP.DescriptionDocument.1") ?? throw new NotSupportedException("UPnP DescriptionDocument missing");
            document = Activator.CreateInstance(type)!;
            ((dynamic)document).Load(location.AbsoluteUri);
            device = ((dynamic)document).RootDevice;
            return new(true, (string)((dynamic)device).UniqueDeviceName, (string)((dynamic)device).FriendlyName, null);
        }
        catch (Exception error) { return new(false, null, null, error.GetType().Name + " 0x" + error.HResult.ToString("X8") + ": " + error.Message); }
        finally
        {
            if (device is not null && Marshal.IsComObject(device)) Marshal.FinalReleaseComObject(device);
            if (document is not null && Marshal.IsComObject(document)) Marshal.FinalReleaseComObject(document);
        }
    }

    private static async Task<bool> CheckMulticastLoopAsync(IPAddress source)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 0));
        listener.MulticastLoopback = true;
        var group = IPAddress.Parse("239.255.255.250");
        listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, source));
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(source, 0));
        sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, source.GetAddressBytes());
        sender.MulticastLoopback = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var expected = Encoding.ASCII.GetBytes("ChunK-Helper-loopback-diagnostic-" + Guid.NewGuid());
        await sender.SendToAsync(expected, SocketFlags.None, new IPEndPoint(group, ((IPEndPoint)listener.LocalEndPoint!).Port), timeout.Token);
        try
        {
            var bytes = new byte[2048];
            var result = await listener.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return bytes.AsSpan(0, result.ReceivedBytes).SequenceEqual(expected);
        }
        catch (OperationCanceledException) { return false; }
    }
}
