using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using System.Xml.Linq;
using PureKBridge.Core.Network;

namespace PureKBridge.App.Services.NativePublishing;

/// <summary>Transient native Windows publication. SOAP/state only; media URLs pass unchanged to the real renderer.</summary>
public sealed class WindowsDeviceHostBridge(ILocalRouteResolver routes, Action<string, Exception?>? log = null) : ISsdpBridge
{
    private readonly SemaphoreSlim _gate = new(1);
    private volatile Session? _session;
    public bool IsRunning => _session?.Running == true;
    public string? PublishedUdn => _session?.PublishedUdn;
    public event Action<Exception>? Failed;

    public async Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is { } previous) { _session = null; await previous.DisposeAsync().ConfigureAwait(false); }
            Session session = null!;
            session = new Session(renderer, routes, error => _ = StopCurrentAsync(error), log);
            _session = session;
            try { await session.StartAsync(cancellationToken).ConfigureAwait(false); }
            catch { _session = null; await session.DisposeAsync().ConfigureAwait(false); throw; }
            session.OnCancellation = cancellationToken.Register(() => _ = StopCurrentAsync(null));
            async Task StopCurrentAsync(Exception? error)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_session, session)) return;
                    _session = null;
                    try { await session.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { log?.Invoke("Windows 发布清理失败。", cleanup); error ??= cleanup; }
                }
                finally { _gate.Release(); }
                if (error is not null) Failed?.Invoke(error);
            }
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_session is { } session) { _session = null; await session.DisposeAsync().ConfigureAwait(false); } }
        finally { _gate.Release(); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DeviceControl : INativeUpnpDeviceControl
    {
        internal Dictionary<string, HostedServiceState> Services { get; } = new(StringComparer.Ordinal);
        internal string? Udn { get; set; }
        public void Initialize(string xml, string identifier, string init) { }
        public object GetServiceObject(string udn, string serviceId) => Udn == udn && Services.TryGetValue(serviceId, out var service)
            ? service : throw new COMException("服务不存在或设备已断开。", unchecked((int)0x80004005));
    }
    private sealed class Session(KnownRenderer renderer, ILocalRouteResolver routes, Action<Exception> failed,
        Action<string, Exception?>? log) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1024 * 1024 };
        private readonly List<RemoteServiceRelay> _relays = [];
        private readonly List<string> _files = [];
        private readonly DeviceControl _control = new();
        private GenaReceiver? _receiver;
        private Dispatcher? _dispatcher;
        private Thread? _thread;
        private INativeUpnpRegistrar? _registrar;
        private string? _identifier, _directory;
        private volatile bool _disposed;
        private readonly object _failureSync = new();
        private int _failing, _routeChecking;
        public volatile bool Running;
        public string? PublishedUdn { get; private set; }
        public CancellationTokenRegistration OnCancellation { get; set; }

        public async Task StartAsync(CancellationToken cancellation)
        {
            using var cancelStartup = cancellation.Register(() => _stop.Cancel());
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
            var token = startup.Token;
            var local = await routes.ResolveAsync(renderer.Room.RendererBaseUrl, token).ConfigureAwait(false);
            if (local.ToString() != renderer.LocalAddress) throw new IOException("房台网络路由已切换，请重新连接。");
            var services = renderer.Description.Services;
            if (services.Count is 0 or > 16 || services.Select(s => s.ServiceId).Distinct().Count() != services.Count ||
                services.Any(s => s.ControlUrl is null || s.ScpdUrl is null || s.ServiceType.Length > 64 ||
                    s.ServiceId.Length is 0 or > 128 || !s.ServiceId.All(c => c is >= '!' and <= '~') ||
                    !Core.Network.RendererClient.IsSameOrigin(s.ControlUrl, renderer.Description.DescriptionUrl) ||
                    !Core.Network.RendererClient.IsSameOrigin(s.ScpdUrl, renderer.Description.DescriptionUrl) ||
                    (s.EventSubUrl is not null && !Core.Network.RendererClient.IsSameOrigin(s.EventSubUrl, renderer.Description.DescriptionUrl))))
                throw new InvalidDataException("房台的服务地址或服务定义无效，无法安全发布。");
            var peers = await Dns.GetHostAddressesAsync(renderer.Room.RendererBaseUrl.DnsSafeHost, token).ConfigureAwait(false);
            _receiver = new GenaReceiver(local, peers, Fail);
            var schemas = await Task.WhenAll(services.Select(async service =>
                ServiceSchema.Parse(await _http.GetStringAsync(service.ScpdUrl, token).ConfigureAwait(false)))).ConfigureAwait(false);
            for (var i = 0; i < services.Count; i++) _relays.Add(new(services[i], schemas[i], _http, _stop.Token, Fail));
            await Task.WhenAll(_relays.Select((relay, i) => relay.SubscribeAsync(_receiver.Add(i, relay)))).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _directory = Path.Combine(Path.GetTempPath(), "ChunKHelper-" + Guid.NewGuid().ToString("N"));
            var folder = Directory.CreateDirectory(_directory);
            var security = folder.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(security);
            for (var i = 0; i < schemas.Length; i++)
            {
                var path = Path.Combine(_directory, "service" + i + ".xml"); _files.Add(path);
                await File.WriteAllTextAsync(path, schemas[i].Xml, token).ConfigureAwait(false);
            }
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; ready.TrySetResult(dispatcher); Dispatcher.Run(); })
                { IsBackground = true, Name = "ChunK Windows Device Host" };
            _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
            _dispatcher = await ready.Task.ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                foreach (var relay in _relays)
                {
                    var state = ServiceDispatchFactory.Create(relay.Schema, relay.Invoke, relay.Read);
                    state.Post = action => { if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action); };
                    state.EventFailed = _ => Fail(new IOException("Windows 设备状态事件发布失败。"));
                    relay.Changed = state.Changed;
                    _control.Services.Add(relay.Service.ServiceId, state);
                }
                _registrar = NativeUpnp.CreateRegistrar();
                _identifier = _registrar.RegisterRunningDevice(Template(), _control, "", _directory, 900);
                PublishedUdn = _registrar.GetUniqueDeviceName(_identifier, renderer.Udn);
                _control.Udn = PublishedUdn;
            }).Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Running = true;
            NetworkChange.NetworkAddressChanged += NetworkChanged;
            log?.Invoke($"Windows 临时设备已发布；源 UDN={renderer.Udn}；桥接 UDN={PublishedUdn}；不转发媒体流", null);
        }

        private string Template()
        {
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var root = new XElement(ns + "root", new XElement(ns + "specVersion", new XElement(ns + "major", 1), new XElement(ns + "minor", 0)),
                new XElement(ns + "device", new XElement(ns + "deviceType", renderer.Description.DeviceType),
                    new XElement(ns + "friendlyName", renderer.Description.FriendlyName.Length <= 64 ? renderer.Description.FriendlyName : renderer.Description.FriendlyName[..64]),
                    new XElement(ns + "manufacturer", "ChunK Helper"), new XElement(ns + "modelName", "Pure K control bridge"),
                    new XElement(ns + "UDN", renderer.Udn), new XElement(ns + "serviceList", _relays.Select((relay, i) =>
                        new XElement(ns + "service", new XElement(ns + "serviceType", relay.Service.ServiceType),
                            new XElement(ns + "serviceId", relay.Service.ServiceId), new XElement(ns + "SCPDURL", "service" + i + ".xml"),
                            new XElement(ns + "controlURL", ""), new XElement(ns + "eventSubURL", ""))))));
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToString(SaveOptions.DisableFormatting);
        }
        private void NetworkChanged(object? sender, EventArgs args)
        {
            if (Interlocked.Exchange(ref _routeChecking, 1) != 0) return;
            _ = CheckRouteAsync();
        }
        private async Task CheckRouteAsync()
        {
            try
            {
                if (!_disposed && (await routes.ResolveAsync(renderer.Room.RendererBaseUrl, _stop.Token).ConfigureAwait(false)).ToString() != renderer.LocalAddress)
                    Fail(new IOException("房台网络路由已切换，请重新连接。"));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception) { if (!_disposed) Fail(new IOException("房台网络路由不可用。")); }
            finally { Interlocked.Exchange(ref _routeChecking, 0); }
        }
        private void Fail(Exception error)
        {
            lock (_failureSync)
            {
                if (_disposed || Interlocked.Exchange(ref _failing, 1) != 0) return;
                _stop.Cancel();
            }
            failed(error);
        }
        public async ValueTask DisposeAsync()
        {
            lock (_failureSync) { if (_disposed) return; _disposed = true; }
            Running = false;
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            OnCancellation.Unregister();
            await _stop.CancelAsync().ConfigureAwait(false);
            Exception? unregisterError = null;
            if (_dispatcher is { HasShutdownStarted: false })
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    try { if (_identifier is not null) _registrar!.UnregisterDevice(_identifier, true); }
                    catch (Exception error) { unregisterError = error; }
                    foreach (var state in _control.Services.Values) state.Close();
                    _control.Services.Clear();
                    if (_registrar is not null) Marshal.FinalReleaseComObject(_registrar);
                    _registrar = null; _identifier = null;
                    _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }).Task.ConfigureAwait(false);
            }
            await Task.WhenAll(_relays.Select(async relay => await relay.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false);
            if (_receiver is not null) await _receiver.DisposeAsync().ConfigureAwait(false);
            _http.Dispose();
            if (_thread is not null) await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            // Delete only our explicitly created files. Never recursively delete an arbitrary path.
            if (unregisterError is null && _directory is not null)
            {
                try { foreach (var file in _files) File.Delete(file); Directory.Delete(_directory, false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log?.Invoke("临时服务描述目录暂未清理。", error); }
            }
            _stop.Dispose();
            log?.Invoke("Windows 临时设备已停止；事件订阅已撤销", unregisterError);
            if (unregisterError is not null) throw new IOException("Windows 临时设备注销失败。", unregisterError);
        }
    }
}
