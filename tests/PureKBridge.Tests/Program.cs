using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PureKBridge.App.Services;
using PureKBridge.App.ViewModels;
using PureKBridge.Core.Models;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;
using PureKBridge.Core.Qr;
using PureKBridge.Core.Session;
using ZXing;
using ZXing.Common;
using FormatException = System.FormatException;

namespace PureKBridge.Tests;

internal static class Program
{
    private static int _passed;
    private static readonly PureKQrParser Parser = new();
    private static readonly string Artifacts = FindArtifacts();

    private static string FindArtifacts()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChunK-Helper.sln")))
                return Path.Combine(directory.FullName, "artifacts", "tests");
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "tests");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--diagnose-native-host") || args.Contains("--probe-hosted-service") || args.Contains("--native-bridge-integration") || args.Contains("--serve-native-bridge")) NativeHostedDiagnostics.InitializeSecurity();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Directory.CreateDirectory(Artifacts);
                if (args.Contains("--native-bridge-integration") || args.Contains("--serve-native-bridge"))
                    await NativeBridgeIntegration.RunAsync(Artifacts, args.Contains("--serve-native-bridge"), args);
                else if (args.Contains("--probe-hosted-service"))
                    await HostedServiceProbe.RunAsync(Artifacts);
                else if (args.Contains("--diagnose-native-host"))
                    await NativeHostedDiagnostics.RunAsync(Artifacts);
                else if (args.Contains("--diagnose-local-reception"))
                    await LocalReceptionDiagnostics.RunAsync(Artifacts, args);
                else if (args.Contains("--prepare-vm-test"))
                    await ExternalDiscoveryDiagnostics.PrepareAsync(Artifacts, args);
                else if (args.Contains("--observe-vm-test"))
                    await ExternalDiscoveryDiagnostics.ObserveAsync(Artifacts);
                else if (args.Contains("--diagnose-discovery"))
                    await DiscoveryDiagnostics.RunAsync(Artifacts, args);
                else if (args.Contains("--network-smoke") || args.Contains("--serve-fake"))
                    await NetworkSmoke.RunAsync(Artifacts, args.Contains("--serve-fake"), args);
                else await RunAsync();
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { application.Shutdown(); }
        });
        application.Run();
        return Environment.ExitCode;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
        _passed++;
    }

    private static string Qr(string name = "测试房间 V13", string renderer = "http://127.0.0.1:49152", bool urlSafe = false)
    {
        var inner = "http://conn.dolphinstar.cn/Remote?url=" + Uri.EscapeDataString(renderer) +
                    "&token=opaque%2Btoken%2F123%3D&name=" + Uri.EscapeDataString(name) + "&appid=test-app";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));
        if (urlSafe) encoded = encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "https://wx.chunk.cn/projection/login?bill_id=test-bill&place_code=P13&projection_url=" + Uri.EscapeDataString(encoded);
    }

    private static async Task RunAsync()
    {
        Directory.CreateDirectory(Artifacts);
        var raw = Qr();
        var room = Parser.Parse(raw);
        Check(room.RoomName == "测试房间 V13" && room.PlaceCode == "P13", "房名与场所编码");
        Check(room.Token == "opaque+token/123=" && room.AppId == "test-app" && room.BillId == "test-bill", "不透明 Token 与可选字段");
        Check(Parser.Parse(Qr(urlSafe: true)).Token == room.Token, "URL-safe Base64 与省略 padding");
        Check(Parser.Parse(Qr(renderer: "https://10.23.4.5:6789/base/")).DescriptionUrl.AbsoluteUri == "https://10.23.4.5:6789/base/description.xml", "自定义网段、端口和目录");
        Check(Parser.Parse(Qr(renderer: "http://[::1]:54321")).RendererBaseUrl.Port == 54321, "IPv6 设备地址");
        Check(!room.ToString().Contains(room.Token) && !room.MaskedToken.Contains(room.Token), "默认字符串与显示遮蔽 Token");
        Check(TokenMask.Mask("1234") == "••••••", "短 Token 完整遮蔽");
        foreach (var invalid in new[] { "", "hello", "https://example.com/", raw.Replace("wx.chunk.cn", "evil.invalid"), raw + "&place_code=duplicate", raw.Replace("projection_url=", "projection_url=%%%"), Qr(renderer: "file:///C:/secret") })
        {
            try { Parser.Parse(invalid); throw new Exception("无效 QR 被接受"); }
            catch (FormatException error) { Check(!error.Message.Contains("opaque") && !error.Message.Contains("projection_url="), "无效 QR 拒绝且错误无原文"); }
        }
        var session = new RoomSession();
        foreach (var state in new[] { ConnectionState.Scanning, ConnectionState.RoomDetected, ConnectionState.WaitingForWechatAuthorization, ConnectionState.ReadyToConnect, ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Idle }) session.TransitionTo(state);
        Check(session.State == ConnectionState.Idle, "识别、授权、重连、重置状态闭环");
        try { session.TransitionTo(ConnectionState.Connected); throw new Exception("跳过授权状态"); }
        catch (InvalidOperationException) { Check(true, "非法状态跳转被拒绝"); }

        var qrReader = new QrImageReader();
        var pixels = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 800, Height = 800, Margin = 4 }
        }.Write(raw);
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
        foreach (var extension in new[] { "png", "jpg", "jpeg", "bmp" })
        {
            BitmapEncoder encoder = extension switch { "png" => new PngBitmapEncoder(), "bmp" => new BmpBitmapEncoder(), _ => new JpegBitmapEncoder { QualityLevel = 94 } };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(Artifacts, "room." + extension);
            using (var stream = File.Create(path)) encoder.Save(stream);
            Check(await qrReader.ReadFileAsync(path, CancellationToken.None) == raw, "实际图片解码 " + extension.ToUpperInvariant());
        }
        Check(qrReader.DecodeBgra(pixels.Pixels, pixels.Width, pixels.Height) == raw, "相机 BGRA 帧解码");
        var blank = Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray();
        Check(qrReader.DecodeBgra(blank, 64, 64) is null, "无二维码帧不产生误报");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await qrReader.ReadFileAsync(Path.Combine(Artifacts, "room.png"), canceled.Token); throw new Exception("未取消"); }
            catch (OperationCanceledException) { Check(true, "图片解码响应取消"); }
        }

        var local = await new LocalRouteResolver().ResolveAsync(new Uri("http://127.0.0.1:49152"), CancellationToken.None);
        Check(IPAddress.IsLoopback(local), "从实际路由获取本机地址，不发送数据报");
        using (var server = new TcpListener(IPAddress.Loopback, 0))
        {
            server.Start();
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var responseTask = ReplyOnceAsync(server);
            using var client = new RendererClient();
            var probe = await client.ProbeAsync(Parser.Parse(Qr(renderer: "http://127.0.0.1:" + port)), CancellationToken.None);
            Check(probe.IsReachable && probe.IsMediaRenderer && probe.Udn == "uuid:00000000-0000-4000-8000-000000000013", "实际 HTTP description.xml 探测与 UDN 读取");
            await responseTask;
        }

        var routes = new FakeRoutes();
        var renderer = new FakeRenderer();
        var connection = new MockPureKConnectionService(routes);
        var progress = new List<string>();
        connection.ProgressChanged += p => progress.Add(p.Message);
        var log = new AppLog();
        var monitor = new FakeMonitor();
        var vm = new MainViewModel(log, qrReader, new CameraService(qrReader, log), renderer, routes, connection, monitor, new DummySsdpBridge());
        var layouts = new LayoutTests(Artifacts, vm, Check);
        try
        {
            await vm.ImportImageAsync(Path.Combine(Artifacts, "room.png"));
            Check(vm.State == ConnectionState.RoomDetected && vm.RoomName == room.RoomName, "图片导入接通 ViewModel 识别流程");
            await layouts.CheckPageAsync("识别房间");
            Check(vm.RendererStatus.Contains("暂时无法访问") && vm.HasRoom, "HTTP 不可达不丢失房间");
            Check(vm.TokenDisplay != room.Token && vm.AuthorizationUrl == room.OriginalQrUrl.AbsoluteUri, "授权原链接保留与默认遮蔽");
            vm.ToggleTokenCommand.Execute(null);
            Check(vm.TokenDisplay == room.Token, "Token 显示切换");
            vm.NextCommand.Execute(null);
            Check(vm.Page == FlowPage.Wechat && !vm.ConnectCommand.CanExecute(null), "微信步骤不可跳过手动确认");
            await layouts.CheckPageAsync("微信授权");
            vm.ConfirmWechat();
            Check(vm.State == ConnectionState.ReadyToConnect, "用户确认微信授权");
            await layouts.CheckPageAsync("连接设备");
            await vm.ConnectAsync();
            Check(vm.State == ConnectionState.Connected && vm.IsDonePage && vm.OnlineLabel == "演示在线", "完整四步 Mock 流程且不冒充真实连接");
            await layouts.CheckPageAsync("完成");
            Check(progress.Count == 3 && monitor.Starts == 0 && vm.DeviceType == "尚未确认", "连接三阶段且 Mock 不伪造设备类型、不发起后台探活");
            await vm.DisconnectAsync();
            Check(vm.State == ConnectionState.Disconnected && vm.IsConnectPage, "断开后保留房间供重连");
            await vm.ConnectAsync();
            Check(vm.State == ConnectionState.Connected, "重新连接成功");
            await vm.ResetAsync();
            Check(vm.State == ConnectionState.Idle && !vm.HasRoom && !vm.IsBusy, "重置清理会话");

            renderer.DelayNext = true;
            var stale = vm.AcceptQrAsync(Qr(name: "旧房间"));
            await renderer.Started.Task;
            var newest = vm.AcceptQrAsync(Qr(name: "新房间"));
            await newest;
            renderer.CompleteDelayed();
            await stale;
            Check(vm.RoomName == "新房间" && vm.State == ConnectionState.RoomDetected && !vm.IsBusy, "旧异步检查不得覆盖新识别结果");

            vm.NextCommand.Execute(null); vm.ConfirmWechat();
            var canceledConnect = vm.ConnectAsync();
            await vm.ResetAsync();
            try { await canceledConnect; } catch (OperationCanceledException) { }
            Check(vm.State == ConnectionState.Idle && !vm.HasRoom, "连接中重新识别不会迟到跳到完成页");
        }
        finally { layouts.Finish(); await vm.DisposeAsync(); }

        log.Write("token=super-secret-token&projection_url=hidden-payload&bill_id=private-bill");
        var logText = await File.ReadAllTextAsync(log.Path);
        Check(!logText.Contains("super-secret-token") && !logText.Contains("hidden-payload") && !logText.Contains("private-bill"), "日志敏感 query 遮蔽");
        await ProtocolTests.RunAsync(Check);
        Console.WriteLine($"\n{_passed} checks passed. Test QR: {Path.Combine(Artifacts, "room.png")}");
    }

    private static async Task ReplyOnceAsync(TcpListener server)
    {
        using var connection = await server.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        string? line;
        do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
        const string xml = "<root xmlns='urn:schemas-upnp-org:device-1-0'><device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>Test device</friendlyName><UDN>uuid:00000000-0000-4000-8000-000000000013</UDN></device></root>";
        var body = Encoding.UTF8.GetBytes(xml);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header); await stream.WriteAsync(body);
    }

    private sealed class FakeRoutes : ILocalRouteResolver
    {
        public Task<IPAddress> ResolveAsync(Uri renderer, CancellationToken cancellationToken) => Task.FromResult(IPAddress.Loopback);
    }
    private sealed class FakeRenderer : IRendererClient
    {
        public bool DelayNext;
        public TaskCompletionSource Started { get; } = new();
        private readonly TaskCompletionSource<RendererProbeResult> _delayed = new();
        public Task<RendererProbeResult> ProbeAsync(PureKRoomInfo room, CancellationToken cancellationToken)
        {
            if (!DelayNext) return Task.FromResult(new RendererProbeResult(false, false));
            DelayNext = false; Started.TrySetResult(); return _delayed.Task;
        }
        public void CompleteDelayed() => _delayed.TrySetResult(new(true, true, "uuid:old-room"));
    }
    private sealed class FakeMonitor : IRendererHealthMonitor
    {
        public int Starts;
        public event Action<RendererHealth>? HealthChanged { add { } remove { } }
        public Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
