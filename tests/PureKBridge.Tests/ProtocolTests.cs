using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PureKBridge.App.Services;
using PureKBridge.App.ViewModels;
using PureKBridge.Core.Models;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;
using PureKBridge.Core.Qr;
using PureKBridge.Core.Session;

namespace PureKBridge.Tests;

internal static class ProtocolTests
{
    public static string Qr(Uri renderer, string token = "test<&token+123")
    {
        var inner = "http://conn.dolphinstar.cn/Remote?url=" + Uri.EscapeDataString(renderer.AbsoluteUri) +
                    "&token=" + Uri.EscapeDataString(token) + "&name=" + Uri.EscapeDataString("本地协议测试");
        return "https://wx.chunk.cn/projection/login?place_code=TEST&projection_url=" +
               Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(inner)));
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var parser = new PureKQrParser();
        await using var server = new FakeRenderer();
        using var client = new RendererClient();
        var room = parser.Parse(Qr(server.BaseUrl));
        var probe = await client.ProbeAsync(room, CancellationToken.None);
        var description = probe.Description!;
        check(probe.IsMediaRenderer && description.Services.Count == 4 && description.IsRootDevice, "真实 description 服务表与根设备解析");
        check(description.ConnectionManager?.ControlUrl?.AbsolutePath == FakeRenderer.ManagerPath &&
            description.Services[0].EventSubUrl?.AbsolutePath == "/device/av/event" && description.Services[0].ScpdUrl is not null, "controlURL / eventSubURL / SCPDURL 相对地址解析");
        var foreign = RendererClient.ParseDescription(FakeRenderer.DescriptionXml().Replace(FakeRenderer.ManagerPath.TrimStart('/').Replace("device/", ""), "https://evil.invalid/control"), room.DescriptionUrl);
        check(foreign?.ConnectionManager?.ControlUrl is null, "不得向描述中的外域地址发送 Token");
        try
        {
            RendererClient.ParseDescription("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///C:/private'>]><root>&x;</root>", room.DescriptionUrl);
            throw new Exception("XML external entity accepted");
        }
        catch (XmlException) { check(true, "XML 外部实体禁止"); }

        var routes = new Routes(IPAddress.Loopback);
        using var connection = new PureKConnectionService(client, routes);
        var connected = await connection.ConnectAsync(room, CancellationToken.None);
        check(!connected.IsSimulated && connected.Renderer?.Udn == FakeRenderer.Udn, "真实连接服务通过本地 HTTP 完成登记");
        var registration = server.Requests.Last(request => request.Method == "POST");
        var body = XDocument.Parse(registration.Body);
        check(registration.Headers["SOAPAction"] == $"\"{FakeRenderer.Manager}#SetDeviceAuthorization\"" &&
            body.Descendants().Single(e => e.Name.LocalName == "Token").Value == room.Token &&
            body.Descendants().Single(e => e.Name.LocalName == "DeviceIP").Value == "127.0.0.1", "SOAPAction、XML 转义和路由来源地址正确");
        routes.Address = IPAddress.Parse("10.1.2.3");
        await connection.DisconnectAsync(room, CancellationToken.None);
        var revoke = XDocument.Parse(server.Requests.Last(request => request.Method == "POST").Body);
        check(revoke.Descendants().Single(e => e.Name.LocalName == "Enable").Value == "0" &&
            revoke.Descendants().Single(e => e.Name.LocalName == "DeviceIP").Value == "127.0.0.1", "撤销使用原登记地址，不使用变化后的地址");

        server.Handler = request => Task.FromResult(request.Method == "GET" ? new TestResponse(200, FakeRenderer.DescriptionXml()) :
            new TestResponse(200, "<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><s:Fault><faultstring>test-secret</faultstring></s:Fault></s:Body></s:Envelope>"));
        try { await connection.ConnectAsync(room, CancellationToken.None); throw new Exception("SOAP Fault accepted"); }
        catch (DeviceConnectionException error) { check(!error.ToString().Contains("test-secret"), "HTTP 200 SOAP Fault 不得误判成功或泄漏响应正文"); }
        server.Handler = null;

        var descriptionReads = 0;
        server.Handler = request => Task.FromResult(request.Method == "GET"
            ? new TestResponse(200, FakeRenderer.DescriptionXml(++descriptionReads > 1 ? "uuid:00000000-0000-4000-8000-000000000099" : null))
            : new TestResponse(200, FakeRenderer.AuthorizationResponse));
        try { await connection.ConnectAsync(room, CancellationToken.None); throw new Exception("changed identity accepted"); }
        catch (DeviceConnectionException)
        {
            check(server.Requests.Last(request => request.Method == "POST").Body.Contains("<Enable>0</Enable>"),
                "登记后设备身份变化时不发布，并回滚原登记");
        }
        server.Handler = null;

        var delayedAuthorization = new BlockingAuthorization();
        using (var canceledService = new PureKConnectionService(client, routes, authorizationClient: delayedAuthorization))
        using (var canceled = new CancellationTokenSource())
        {
            var pending = canceledService.ConnectAsync(room, canceled.Token);
            await delayedAuthorization.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            canceled.Cancel();
            try { await pending; throw new Exception("canceled authorization accepted"); }
            catch (OperationCanceledException) { }
            check(delayedAuthorization.Calls.SequenceEqual([true, false]), "登记请求中取消时仍尝试撤销，避免迟到注册残留");
        }

        var publicRoom = parser.Parse(Qr(new Uri("http://10.20.30.40:54321/device/")));
        var publicDescription = RendererClient.ParseDescription(FakeRenderer.DescriptionXml(), publicRoom.DescriptionUrl)!;
        var known = new KnownRenderer(publicRoom, "10.20.30.2", publicDescription);
        var identities = SsdpAdvertisement.Identities(publicDescription);
        check(identities.Count == 7 && identities.Count(identity => identity.Usn == FakeRenderer.Udn) == 1,
            "真实 UDN 及 4 服务生成 7 个 NT/USN，不编造服务");
        var alive = Encoding.ASCII.GetString(SsdpAdvertisement.Alive(publicDescription, identities[0]));
        check(alive.EndsWith("\r\n\r\n") && alive.Contains("max-age=60\r\n") && alive.Contains(publicRoom.DescriptionUrl.AbsoluteUri) &&
            !alive.Contains(publicRoom.Token), "alive 使用真实 LOCATION、CRLF、短缓存且不含 Token");
        var bye = Encoding.ASCII.GetString(SsdpAdvertisement.ByeBye(identities[0]));
        check(bye.Contains("ssdp:byebye") && !bye.Contains("LOCATION") && !bye.Contains("CACHE-CONTROL"), "byebye 撤销格式");
        var embedded = publicDescription with { IsRootDevice = false, Services = publicDescription.Services.Take(1).ToArray() };
        check(SsdpAdvertisement.Identities(embedded).Count == 3 &&
            SsdpAdvertisement.Identities(embedded).All(id => id.NotificationType != "upnp:rootdevice"), "嵌入设备不冒充根设备，不添加缺失服务");
        try { SsdpAdvertisement.Alive(description, identities[0]); throw new Exception("loopback LOCATION accepted"); }
        catch (ArgumentException) { check(true, "正式公告拒绝回环 LOCATION"); }

        var senders = new Senders();
        var bridgeRoutes = new Routes(IPAddress.Parse(known.LocalAddress));
        var bridge = new SsdpBridge(bridgeRoutes, senderFactory: senders,
            options: new() { AliveInterval = TimeSpan.FromMinutes(1), BurstInterval = TimeSpan.FromMilliseconds(1) });
        await bridge.StartAsync(known);
        check(bridge.IsRunning && senders.Messages.Count(message => message.Contains("ssdp:alive")) == 21, "启动 3 轮低频公告，连接不依赖定向 NOTIFY");
        await bridge.StopAsync();
        check(!bridge.IsRunning && senders.Messages.Count(message => message.Contains("ssdp:byebye")) == 7 && senders.Disposed, "正常停止发送全部 byebye 并释放 socket");
        var stoppedCount = senders.Messages.Count;
        await Task.Delay(30);
        check(senders.Messages.Count == stoppedCount, "停止后不再发 alive");

        var routeSenders = new Senders();
        var changingRoutes = new Routes(IPAddress.Parse(known.LocalAddress));
        var changingBridge = new SsdpBridge(changingRoutes, senderFactory: routeSenders,
            options: new() { AliveInterval = TimeSpan.FromMilliseconds(15), BurstInterval = TimeSpan.FromMilliseconds(1) });
        var routeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changingBridge.Failed += _ => routeFailed.TrySetResult();
        await changingBridge.StartAsync(known);
        changingRoutes.Address = IPAddress.Parse("10.20.30.3");
        await routeFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await changingBridge.StopAsync();
        check(!changingBridge.IsRunning && routeSenders.Messages.Count(message => message.Contains("ssdp:byebye")) == 7,
            "路由地址切换停止旧接口发布并撤销全部身份");

        var healthClient = new HealthClient(description, [false, false, true]);
        var health = new RendererHealthMonitor(healthClient, options: new()
        { OnlineInterval = TimeSpan.FromMilliseconds(10), RetryInterval = TimeSpan.FromMilliseconds(5) });
        var healthEvents = new List<RendererHealth>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.HealthChanged += state =>
        {
            lock (healthEvents) { healthEvents.Add(state); if (healthEvents.Count > 1 && state == RendererHealth.RendererOnline) recovered.TrySetResult(); }
        };
        await health.StartAsync(new(room, "127.0.0.1", description), CancellationToken.None);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await health.StopAsync();
        check(healthEvents.Contains(RendererHealth.RendererSuspect) && !healthEvents.Contains(RendererHealth.RendererOffline), "短暂抖动恢复 ONLINE，不提前下线");

        var failingClient = new HealthClient(description, [false, false, false, false, false]);
        var failingHealth = new RendererHealthMonitor(failingClient, options: new()
        { OnlineInterval = TimeSpan.FromMilliseconds(10), RetryInterval = TimeSpan.FromMilliseconds(5) });
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failingHealth.HealthChanged += state => { if (state == RendererHealth.RendererOffline) offline.TrySetResult(); };
        await failingHealth.StartAsync(new(room, "127.0.0.1", description), CancellationToken.None);
        await offline.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(30);
        check(failingClient.Calls == 5, "5 次失败后 OFFLINE 并停止重试，不无限 1 秒轮询");
        await failingHealth.StopAsync();

        var changedIdentity = new HealthClient(description with { Udn = "uuid:00000000-0000-4000-8000-000000000099" }, [true]);
        var identityHealth = new RendererHealthMonitor(changedIdentity, options: new()
        { OnlineInterval = TimeSpan.FromMilliseconds(10), RetryInterval = TimeSpan.FromMilliseconds(1) });
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        identityHealth.HealthChanged += state => { if (state == RendererHealth.RendererOffline) replaced.TrySetResult(); };
        await identityHealth.StartAsync(new(room, "127.0.0.1", description), CancellationToken.None);
        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await identityHealth.StopAsync();
        check(changedIdentity.Calls == 5, "HTTP 200 但 UDN 改变不能保持原房台在线");

        await ViewModelTestsAsync(server, client, room, check);
    }

    private static async Task ViewModelTestsAsync(FakeRenderer server, RendererClient client, PureKRoomInfo room, Action<bool, string> check)
    {
        var routes = new Routes(IPAddress.Loopback);
        using var connection = new PureKConnectionService(client, routes);
        var log = new AppLog(); var images = new QrImageReader();
        var bridge = new TestBridge(); var health = new TestMonitor();
        var vm = new MainViewModel(log, images, new CameraService(images, log), client, routes, connection, health, bridge);
#if DEBUG
        check(vm.IsDebugBuild, "Debug 保留开发入口");
#else
        check(!vm.IsDebugBuild, "Release 隐藏开发入口");
#endif
        await vm.AcceptQrAsync(room.OriginalQrUrl.AbsoluteUri);
        vm.NextCommand.Execute(null); vm.ConfirmWechat();
        await vm.ConnectAsync();
        check(vm.IsDonePage && vm.OnlineLabel == "在线" && vm.BridgeStatus == "已发布" && health.Starts == 1, "正式 UI 只在登记与发布成功后显示在线");
        bridge.Fail();
        await WaitUntilAsync(() => vm.State == ConnectionState.Disconnected && !vm.IsBusy);
        check(vm.HasError && vm.BridgeStatus == "尚未启用" && !bridge.IsRunning && health.Stops > 0, "公告失败事件清理会话并回到可重连状态");
        bridge.StartFailure = true;
        try { await vm.ConnectAsync(); throw new Exception("bridge failure accepted"); }
        catch (IOException) { }
        check(!bridge.IsRunning && vm.BridgeStatus != "已发布" &&
            server.Requests.Last(r => r.Method == "POST").Body.Contains("<Enable>0</Enable>"), "登记成功但发布失败时回滚登记");
        bridge.StartFailure = false;
        await vm.ConnectAsync();
        health.Offline();
        await WaitUntilAsync(() => vm.State == ConnectionState.Disconnected && !vm.IsBusy);
        check(!bridge.IsRunning && vm.HasError && server.Requests.Last(r => r.Method == "POST").Body.Contains("<Enable>0</Enable>"),
            "确认离线联动停止公告并撤销登记");
        await vm.ConnectAsync();
        await vm.DisposeAsync();
        check(!bridge.IsRunning && server.Requests.Last(r => r.Method == "POST").Body.Contains("<Enable>0</Enable>"), "关闭 ViewModel 取消探活、停止公告并撤销登记");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    internal sealed class Routes(IPAddress address) : ILocalRouteResolver
    {
        public IPAddress Address = address;
        public Task<IPAddress> ResolveAsync(Uri renderer, CancellationToken cancellationToken) => Task.FromResult(Address);
    }
    private sealed class Senders : ISsdpSenderFactory, ISsdpSender
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool Disposed;
        public ISsdpSender Open(IPAddress localAddress) => this;
        public Task SendAsync(byte[] datagram, CancellationToken cancellationToken) { Messages.Enqueue(Encoding.ASCII.GetString(datagram)); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
    private sealed class HealthClient(RendererDescription description, bool[] sequence) : IRendererClient
    {
        public int Calls;
        public Task<RendererProbeResult> ProbeAsync(PureKRoomInfo room, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref Calls) - 1;
            var healthy = sequence[Math.Min(index, sequence.Length - 1)];
            return Task.FromResult(healthy ? new RendererProbeResult(true, true, Description: description) : new(false, false));
        }
    }
    private sealed class BlockingAuthorization : IDeviceAuthorizationClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> Calls { get; } = [];
        public async Task SetDeviceAuthorizationAsync(KnownRenderer renderer, bool enable, CancellationToken cancellationToken)
        {
            Calls.Add(enable);
            if (enable) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
        }
    }
    private sealed class TestBridge : ISsdpBridge
    {
        public bool StartFailure;
        public bool IsRunning { get; private set; }
        public event Action<Exception>? Failed;
        public Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken = default)
        { if (StartFailure) throw new IOException("fake bridge failed"); IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Fail() => Failed?.Invoke(new IOException("fake network change"));
    }
    private sealed class TestMonitor : IRendererHealthMonitor
    {
        public int Starts, Stops;
        public event Action<RendererHealth>? HealthChanged;
        public Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; return Task.CompletedTask; }
        public void Offline() => HealthChanged?.Invoke(RendererHealth.RendererOffline);
    }
}
