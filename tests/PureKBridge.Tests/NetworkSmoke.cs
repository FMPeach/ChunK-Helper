using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;
using PureKBridge.Core.Qr;
using ZXing;
using ZXing.Common;

namespace PureKBridge.Tests;

internal static class NetworkSmoke
{
    public static async Task RunAsync(string artifacts, bool serveOnly, string[] args)
    {
        var bindIndex = Array.IndexOf(args, "--bind");
        if (bindIndex < 0 || bindIndex + 1 == args.Length || !IPAddress.TryParse(args[bindIndex + 1], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            !NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.OperationalStatus == OperationalStatus.Up &&
                nic.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(address))))
            throw new ArgumentException("网络测试需显式指定本机 LAN IPv4：--bind <本机地址>。避免自动选到 VPN 或虚拟网卡。");
        var udn = "uuid:" + Guid.NewGuid().ToString();
        await using var fake = new FakeRenderer(address, udn);
        var raw = ProtocolTests.Qr(fake.BaseUrl);
        var imagePath = Path.Combine(artifacts, "network-room.png");
        var pixels = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 800, Height = 800, Margin = 4 } }.Write(raw);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(pixels.Width, pixels.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4)));
        using (var output = File.Create(imagePath)) encoder.Save(output);
        Console.WriteLine($"Fake Renderer: {fake.BaseUrl}\nUDN: {udn}\nQR image: {imagePath}");
        if (serveOnly)
        {
            Console.WriteLine("Test server only. Press Enter to stop (maximum 10 minutes).");
            await Task.WhenAny(Console.In.ReadLineAsync(), Task.Delay(TimeSpan.FromMinutes(10)));
            Console.WriteLine($"Fake HTTP requests: {fake.Requests.Count}");
            return;
        }
        using var client = new RendererClient();
        var routes = new LocalRouteResolver();
        using var connection = new PureKConnectionService(client, routes, (message, error) => Console.WriteLine(message));
        var bridge = new SsdpBridge(routes, (message, error) => Console.WriteLine(message));
        var room = new PureKQrParser().Parse(raw);
        var connected = await connection.ConnectAsync(room, CancellationToken.None);
        bool nativeFound = false;
        string? nativeError = null;
        NativeDiscoveryProbe? observer = null;
        try
        {
            try { observer = new NativeDiscoveryProbe(udn); }
            catch (Exception error) { nativeError = error.GetType().Name + ": " + error.Message; }
            await bridge.StartAsync(connected.Renderer!);
            Console.WriteLine("Real multicast sender started. Querying Windows UPnP by the test UDN...");
            try
            {
                if (observer is not null)
                { await observer.Callback.Found.Task.WaitAsync(TimeSpan.FromSeconds(25)); nativeFound = true; }
            }
            catch (Exception error) { nativeError = error.GetType().Name + ": " + error.Message; }
            Console.WriteLine(nativeFound ? "Windows native UPnP FOUND the exact test UDN." : "Windows native discovery NOT CONFIRMED: " + (nativeError ?? "No device returned"));
            var report = new { renderer = fake.BaseUrl, udn, sourceAddress = connected.LocalAddress,
                multicastSenderStarted = bridge.IsRunning, windowsNativeFound = nativeFound, windowsNativeError = nativeError,
                requests = fake.Requests.Select(request => new { request.Method, request.Path }).ToArray(),
                note = "Fake Renderer only. Not a real Pure K authorization/casting test." };
            await File.WriteAllTextAsync(Path.Combine(artifacts, "network-smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await bridge.StopAsync();
            await connection.DisconnectAsync(room, CancellationToken.None);
            observer?.Dispose();
        }
        Console.WriteLine("Test advertisement stopped and registration revoked.");
    }

}
