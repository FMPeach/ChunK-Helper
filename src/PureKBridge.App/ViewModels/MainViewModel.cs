using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PureKBridge.App.Services;
using PureKBridge.Core.Models;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;
using PureKBridge.Core.Qr;
using PureKBridge.Core.Session;

namespace PureKBridge.App.ViewModels;

public enum FlowPage { Scan = 1, Wechat, Connect, Done }

public sealed class StepViewModel(int number, string label) : ObservableObject
{
    public int Number { get; } = number;
    public string Label { get; } = label;
    private int _current = 1;
    public bool IsCurrent => Number == _current;
    public bool IsDone => Number < _current;
    public string Bullet => IsDone ? "✓" : Number.ToString();
    public void Update(int current) { _current = current; Changed(null); }
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AppLog _log;
    private readonly QrImageReader _images;
    private readonly CameraService _camera;
    private readonly IRendererClient _renderer;
    private readonly ILocalRouteResolver _routes;
    private readonly IPureKConnectionService _connection;
    private readonly IPureKConnectionService _demoConnection;
    private readonly SemaphoreSlim _connectionGate = new(1);
    private readonly IRendererHealthMonitor _monitor;
    private readonly ISsdpBridge _bridge;
    private readonly IPureKQrParser _parser = new PureKQrParser();
    private readonly RoomSession _session = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private int _generation;
    private int _cameraGeneration;
    private bool _closing;
    private bool _busy;
    private bool _cameraRunning;
    private bool _connected;
    private bool _wechatConfirmed;
    private bool _debugSimulation;
    private bool _bridgePublished;
    private IPureKConnectionService? _sessionConnection;
    private PureKRoomInfo? _sessionRoom;
    private bool _tokenVisible;
    private FlowPage _page = FlowPage.Scan;
    private PureKRoomInfo? _room;
    private RendererProbeResult? _probe;
    private string? _localAddress;
    private string _rendererStatus = "Renderer 已识别";
    private string _connectionMessage = "";
    private string _copyLabel = "复制链接";
    private string? _error;
    private BitmapSource? _preview;
    private BitmapSource? _pendingPreview;
    private int _previewQueued;
    private CameraDevice? _selectedCamera;

    public MainViewModel(AppLog log, QrImageReader images, CameraService camera,
        IRendererClient renderer, ILocalRouteResolver routes, IPureKConnectionService connection,
        IRendererHealthMonitor monitor, ISsdpBridge bridge)
    {
        _log = log; _images = images; _camera = camera; _renderer = renderer;
        _routes = routes; _connection = connection; _monitor = monitor; _bridge = bridge;
        _demoConnection = new MockPureKConnectionService(routes);
        CameraCommand = new(ToggleCameraAsync, ReportError, () => !_closing && (!_busy || _cameraRunning));
        ImportImageCommand = new(PickImageAsync, ReportError, () => !_closing && !_busy);
        ResetCommand = new(ResetAsync, ReportError, () => !_closing);
        NextCommand = new(_ => ShowWechat(), () => _room is not null && !_busy);
        ConfirmWechatCommand = new(_ => ConfirmWechat(), () => _room is not null);
        BackCommand = new(_ => Back(), () => !_busy);
        CopyLinkCommand = new(CopyLinkAsync, ReportError, () => _room is not null);
        ToggleTokenCommand = new(_ => { _tokenVisible = !_tokenVisible; Update(); });
        ConnectCommand = new(ConnectAsync, ReportError, () => _room is not null && _wechatConfirmed && !_busy && !_closing);
        DisconnectCommand = new(DisconnectAsync, ReportError, () => _connected && !_busy && !_closing);
        RetryProbeCommand = new(ProbeAgainAsync, ReportError, () => _room is not null && !_busy);
        DebugCommand = new(value => RunDebug(value as string), () => !_closing && !_busy);
        _session.StateChanged += OnStateChanged;
        _camera.PreviewFrame += OnPreview;
        _camera.QrDecoded += OnCameraQr;
        _camera.Failed += OnCameraFailed;
        _connection.ProgressChanged += OnConnectionProgress;
        _demoConnection.ProgressChanged += OnConnectionProgress;
        _monitor.HealthChanged += OnHealthChanged;
        _bridge.Failed += OnBridgeFailed;
    }

    public ObservableCollection<StepViewModel> Steps { get; } = [new(1, "识别房间"), new(2, "微信授权"), new(3, "连接设备"), new(4, "完成")];
    public ObservableCollection<CameraDevice> Cameras { get; } = [];
    public CameraDevice? SelectedCamera { get => _selectedCamera; set => Set(ref _selectedCamera, value); }
    public BitmapSource? Preview { get => _preview; private set => Set(ref _preview, value); }
    public ConnectionState State => _session.State;
    public FlowPage Page => _page;
    public PureKRoomInfo? Room => _room;
    public bool IsScanPage => _page == FlowPage.Scan;
    public bool IsWechatPage => _page == FlowPage.Wechat;
    public bool IsConnectPage => _page == FlowPage.Connect;
    public bool IsDonePage => _page == FlowPage.Done;
    public bool HasRoom => _room is not null;
    public bool ShowScanner => !HasRoom;
    public bool ShowScannerPlaceholder => Preview is null;
    public bool IsCameraRunning => _cameraRunning;
    public bool IsBusy => _busy;
    public bool IsConnecting => State == ConnectionState.Connecting;
    public bool CanChooseCamera => !_cameraRunning && !_busy;
    public string AppTitle => "纯K助手";
    public string CameraButtonLabel => _cameraRunning ? "停止扫描" : "使用摄像头扫描";
    public string ScanTip => _cameraRunning ? "将房间二维码置于画面中，识别后会自动停止" : "将房间点歌屏上的二维码置于框内";
    public string RoomName => _room?.RoomName ?? "尚未识别";
    public string RendererDisplay => _room?.RendererBaseUrl.Authority ?? "尚未识别";
    public string TokenDisplay => _room is null ? "" : _tokenVisible ? _room.Token : _room.MaskedToken;
    public string ToggleTokenLabel => _tokenVisible ? "隐藏" : "显示";
    public string AuthorizationUrl => _room?.OriginalQrUrl.AbsoluteUri ?? "";
    public string LocalAddress => _localAddress ?? "尚未获取";
    public string RendererStatus => _rendererStatus;
    public string ConnectionMessage => _connectionMessage;
    public string CopyLabel => _copyLabel;
    private IPureKConnectionService ActiveConnection => _debugSimulation ? _demoConnection : _connection;
    public bool IsSimulation => ActiveConnection.IsSimulation;
    public string ModeCaption => IsSimulation ? "演示模式 · 不会登记房台或广播设备" : "仅发布当前房间；投屏控制仍直连房台";
    public string ConnectionIntroduction => IsSimulation ? "确认房间和本机地址，然后演示连接。不会向房台提交设备登记。" : "确认房间和本机地址。连接后，可回到投屏应用刷新设备列表。";
    public string BridgeStatus => _bridgePublished ? "已发布" : "尚未启用";
    public string BridgeHint => IsSimulation ? "演示连接" : "标准设备公告";
    public string ConnectedTitle => IsSimulation ? $"已完成 {RoomName} 的演示连接" : $"已连接到 {RoomName}";
    public string ConnectedDescription => IsSimulation ? "四步流程已完成，当前尚未向房台登记此电脑。" : "房台登记与设备公告已启动，可回到投屏应用刷新设备列表。";
    public string OnlineLabel => _connected ? IsSimulation ? "演示在线" : "在线" : "未连接";
    public string DeviceType => _probe?.IsMediaRenderer == true ? "MediaRenderer" : "尚未确认";
    public string? ErrorMessage => _error;
    public bool HasError => !string.IsNullOrWhiteSpace(_error);
    public string ConnectionDetails => $"Room Name: {RoomName}\nRenderer URL: {_room?.RendererBaseUrl}\nLocal IP: {LocalAddress}\nUDN: {_probe?.Udn ?? "尚未读取"}\nToken: {TokenDisplay}\nDescription URL: {_room?.DescriptionUrl}\nConnection State: {State}\nMode: {(IsSimulation ? "Simulation" : "Device registration")}";
#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif
    public AsyncCommand CameraCommand { get; }
    public AsyncCommand ImportImageCommand { get; }
    public AsyncCommand ResetCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ConfirmWechatCommand { get; }
    public RelayCommand BackCommand { get; }
    public AsyncCommand CopyLinkCommand { get; }
    public RelayCommand ToggleTokenCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand RetryProbeCommand { get; }
    public RelayCommand DebugCommand { get; }

    public async Task InitializeAsync()
    {
        try
        {
            var devices = await CameraService.EnumerateAsync(_lifetime.Token);
            if (_closing) return;
            foreach (var device in devices)
            { Cameras.Add(device); _log.Write($"摄像头设备：{device.Name}"); }
            SelectedCamera = Cameras.FirstOrDefault();
            _log.Write($"启动，相机数量 {Cameras.Count}");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _log.Write("摄像头枚举失败；图片扫码仍可使用", error); }
    }

    private (int Generation, CancellationToken Token) BeginOperation()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return (++_generation, _operation.Token);
    }

    private bool Current(int generation) => !_closing && generation == _generation;

    public async Task AcceptQrAsync(string raw)
    {
        var context = BeginOperation();
        try
        {
            if (!await ResetForOperationAsync(context)) return;
            var room = _parser.Parse(raw);
            _log.Write($"QR 类型：官方投屏 URL；解析房间：{room.RoomName}；Token={room.MaskedToken}");
            _room = room;
            _rendererStatus = "正在检查 Renderer...";
            _session.TransitionTo(ConnectionState.RoomDetected);
            await ProbeRoomAsync(room, context.Generation, context.Token);
        }
        finally { if (Current(context.Generation)) { _busy = false; Update(); } }
    }

    private async Task ProbeRoomAsync(PureKRoomInfo room, int generation, CancellationToken cancellationToken)
    {
        async Task<string?> ResolveLocalAsync()
        {
            try { return (await _routes.ResolveAsync(room.RendererBaseUrl, cancellationToken)).ToString(); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException)
            { _log.Write("本机地址暂时无法确认", error); return null; }
        }
        var localTask = ResolveLocalAsync();
        var probeTask = _renderer.ProbeAsync(room, cancellationToken);
        await Task.WhenAll(localTask, probeTask);
        if (!Current(generation)) return;
        _localAddress = await localTask;
        _probe = await probeTask;
        _rendererStatus = _probe.IsReachable ? "Renderer 已响应" : "Renderer 暂时无法访问";
        _log.Write($"Renderer probe：{room.RendererBaseUrl.Authority}；响应={_probe.IsReachable}；Local IP={LocalAddress}");
        Update();
    }

    private async Task ProbeAgainAsync()
    {
        if (_room is not { } room) return;
        var context = BeginOperation();
        _busy = true; _rendererStatus = "正在检查 Renderer..."; Update();
        try { await ProbeRoomAsync(room, context.Generation, context.Token); }
        finally { if (Current(context.Generation)) { _busy = false; Update(); } }
    }

    private async Task PickImageAsync()
    {
        var dialog = new OpenFileDialog
        { Title = "从图片读取房间二维码", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog() == true) await ImportImageAsync(dialog.FileName);
    }

    public async Task ImportImageAsync(string path)
    {
        var context = BeginOperation();
        _busy = true;
        await StopCameraAsync();
        Update();
        try
        {
            var raw = await _images.ReadFileAsync(path, context.Token);
            if (!Current(context.Generation)) return;
            if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("未找到二维码，请换一张更清晰的图片。");
            await AcceptQrAsync(raw);
        }
        finally { if (Current(context.Generation)) { _busy = false; Update(); } }
    }

    private async Task ToggleCameraAsync()
    {
        if (_cameraRunning) { await ResetAsync(); return; }
        var context = BeginOperation();
        try
        {
            if (!await ResetForOperationAsync(context)) return;
            if (SelectedCamera is not { } device)
                throw new InvalidOperationException("摄像头不可用，请连接摄像头，或从图片读取二维码。");
            _cameraGeneration = context.Generation;
            _cameraRunning = true;
            _session.TransitionTo(ConnectionState.Scanning);
            await _camera.StartAsync(device, context.Token);
        }
        catch
        {
            if (Current(context.Generation)) { _cameraRunning = false; Preview = null; }
            throw;
        }
        finally { if (Current(context.Generation)) { _busy = false; Update(); } }
    }

    private void OnPreview(BitmapSource frame)
    {
        if (_closing) return;
        Interlocked.Exchange(ref _pendingPreview, frame);
        if (Interlocked.Exchange(ref _previewQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(() =>
        {
            var latest = Interlocked.Exchange(ref _pendingPreview, null);
            if (!_closing && _cameraRunning && latest is not null) Preview = latest;
            Interlocked.Exchange(ref _previewQueued, 0);
            Changed(nameof(ShowScannerPlaceholder));
        }, DispatcherPriority.Render);
    }

    private void OnCameraQr(string raw)
    {
        var generation = _cameraGeneration;
        _dispatcher.BeginInvoke(async () =>
        {
            if (!Current(generation) || !_cameraRunning) return;
            try { await AcceptQrAsync(raw); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ReportError(error); }
        });
    }

    private void OnCameraFailed(Exception error)
    {
        var generation = _cameraGeneration;
        _dispatcher.BeginInvoke(async () =>
        {
            if (!Current(generation) || !_cameraRunning) return;
            try { await StopCameraAsync(); }
            catch (Exception stopError) { _log.Write("释放异常相机", stopError); }
            if (Current(generation)) ReportError(error);
        });
    }

    private async Task StopCameraAsync()
    {
        _cameraRunning = false;
        await _camera.StopAsync();
        Interlocked.Exchange(ref _pendingPreview, null);
        Preview = null;
    }

    private void ShowWechat()
    {
        _wechatConfirmed = false;
        _error = null; _page = FlowPage.Wechat;
        _session.TransitionTo(ConnectionState.WaitingForWechatAuthorization);
        Update();
    }

    public void ConfirmWechat()
    {
        if (_room is null) return;
        _wechatConfirmed = true; _error = null; _page = FlowPage.Connect;
        _session.TransitionTo(ConnectionState.ReadyToConnect);
        Update();
    }

    private void Back()
    {
        _error = null;
        if (_page == FlowPage.Connect)
        { _wechatConfirmed = false; _page = FlowPage.Wechat; _session.TransitionTo(ConnectionState.WaitingForWechatAuthorization); }
        else if (_page == FlowPage.Wechat)
        { _page = FlowPage.Scan; _session.TransitionTo(ConnectionState.RoomDetected); }
        Update();
    }

    private async Task CopyLinkAsync()
    {
        Clipboard.SetText(AuthorizationUrl);
        _copyLabel = "✓ 已复制";
        Update();
        await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
        _copyLabel = "复制链接"; Update();
    }

    public async Task ConnectAsync()
    {
        if (_room is not { } room || !_wechatConfirmed)
            throw new InvalidOperationException("请先确认已在电脑微信完成授权。");
        var context = BeginOperation();
        _busy = true; _error = null; _page = FlowPage.Connect;
        _session.TransitionTo(ConnectionState.Connecting);
        Update();
        await _connectionGate.WaitAsync(context.Token);
        try
        {
            context.Token.ThrowIfCancellationRequested();
            await StopSessionCoreAsync();
            context.Token.ThrowIfCancellationRequested();
            _sessionConnection = ActiveConnection;
            _sessionRoom = room;
            var result = await _sessionConnection.ConnectAsync(room, context.Token);
            context.Token.ThrowIfCancellationRequested();
            if (!Current(context.Generation)) return;
            _localAddress = result.LocalAddress ?? _localAddress;
            if (!result.IsSimulated)
            {
                var known = result.Renderer ?? throw new DeviceConnectionException("设备登记结果没有包含真实设备信息。");
                _probe = new(true, true, known.Udn, known.Description.FriendlyName, Description: known.Description);
                _connectionMessage = "正在发布设备公告..."; Update();
                await _bridge.StartAsync(known, context.Token);
                context.Token.ThrowIfCancellationRequested();
                if (!_bridge.IsRunning) throw new DeviceConnectionException("设备尚未成功发布，请检查当前网络。");
                _bridgePublished = true;
                await _monitor.StartAsync(known, _lifetime.Token);
                context.Token.ThrowIfCancellationRequested();
            }
            _connected = true;
            if (!result.IsSimulated) _rendererStatus = "Renderer 已响应";
            _page = FlowPage.Done;
            _session.TransitionTo(ConnectionState.Connected);
        }
        catch
        {
            await StopSessionCoreAsync();
            if (Current(context.Generation)) throw;
        }
        finally
        {
            _connectionGate.Release();
            if (Current(context.Generation)) { _busy = false; Update(); }
        }
    }

    public async Task DisconnectAsync()
    {
        if (_room is not { } room) return;
        var context = BeginOperation();
        _busy = true; Update();
        await _connectionGate.WaitAsync(context.Token);
        try
        {
            await StopSessionCoreAsync();
            if (!Current(context.Generation)) return;
            _connected = false; _page = FlowPage.Connect;
            _session.TransitionTo(ConnectionState.Disconnected);
        }
        finally { _connectionGate.Release(); if (Current(context.Generation)) { _busy = false; Update(); } }
    }

    // Called only while holding _connectionGate. Cancel and join before revoking the registration.
    private async Task StopSessionCoreAsync()
    {
        async Task Cleanup(Func<Task> action, string label)
        { try { await action(); } catch (Exception error) { _log.Write(label, error); } }
        await Cleanup(() => _monitor.StopAsync(), "停止探活");
        await Cleanup(() => _bridge.StopAsync(), "停止设备公告");
        _bridgePublished = false; _connected = false;
        var connection = _sessionConnection;
        var room = _sessionRoom;
        _sessionConnection = null; _sessionRoom = null;
        if (connection is not null && room is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Cleanup(() => connection.DisconnectAsync(room, timeout.Token), "撤销当前房台登记");
        }
    }

    public async Task ResetAsync()
    {
        var context = BeginOperation();
        try { await ResetForOperationAsync(context); }
        finally { if (Current(context.Generation)) { _busy = false; Update(); } }
    }

    private async Task<bool> ResetForOperationAsync((int Generation, CancellationToken Token) context)
    {
        _busy = true; Update();
        await StopCameraAsync();
        if (!Current(context.Generation)) return false;
        await _connectionGate.WaitAsync(context.Token);
        try
        {
            if (!Current(context.Generation)) return false;
            await StopSessionCoreAsync();
        }
        finally { _connectionGate.Release(); }
        if (!Current(context.Generation)) return false;
        _connected = false; _room = null; _probe = null; _localAddress = null;
        _wechatConfirmed = false; _tokenVisible = false; _error = null;
        _debugSimulation = false;
        _connectionMessage = ""; _copyLabel = "复制链接";
        _page = FlowPage.Scan;
        _session.TransitionTo(ConnectionState.Idle);
        Update();
        return true;
    }

    private void OnConnectionProgress(ConnectionProgress progress) => _dispatcher.BeginInvoke(() =>
    {
        if (_closing || State != ConnectionState.Connecting) return;
        _connectionMessage = progress.Message;
        Changed(nameof(ConnectionMessage));
    });

    private void OnHealthChanged(RendererHealth health) => _dispatcher.BeginInvoke(() =>
    {
        if (_closing || !_connected) return;
        if (health == RendererHealth.RendererSuspect)
        { _rendererStatus = "网络暂时不稳定，正在重试"; Update(); }
        else if (health == RendererHealth.RendererOffline)
            _ = FailSessionAsync("房台已离线，请检查网络后重新连接。");
        else { _rendererStatus = "Renderer 已响应"; Update(); }
    });

    private void OnBridgeFailed(Exception error) => _dispatcher.BeginInvoke(() =>
    {
        if (_closing || !_connected) return;
        _log.Write("设备公告停止", error);
        _ = FailSessionAsync("当前网络已变化或设备公告失败，请重新连接房台。");
    });

    private async Task FailSessionAsync(string message)
    {
        var generation = _generation + 1;
        try
        {
            await DisconnectAsync();
            if (!Current(generation)) return;
            _error = message;
            _rendererStatus = "Renderer 暂时无法访问";
            Update();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportError(error); }
    }

    public void ReportError(Exception error)
    {
        if (_closing || error is OperationCanceledException) return;
        _log.Write("操作失败", error);
        _error = error switch
        {
            DeviceConnectionException => error.Message,
            FormatException => error.Message,
            UnauthorizedAccessException => "摄像头不可用，或没有读取图片的权限。请检查后重试。",
            IOException => "无法读取文件或连接网络，请检查后重试。",
            InvalidOperationException => error.Message,
            _ => "操作没有完成，请重试或重新识别房间。"
        };
        _busy = false;
        _session.TransitionTo(ConnectionState.Error);
        Update();
    }

    private void OnStateChanged(ConnectionState state) { _log.Write($"连接状态：{state}"); Update(); }

    private void Update()
    {
        Changed(null);
        foreach (var step in Steps) step.Update((int)_page);
        CameraCommand.Refresh(); ImportImageCommand.Refresh(); ResetCommand.Refresh();
        NextCommand.Refresh(); ConfirmWechatCommand.Refresh(); BackCommand.Refresh(); CopyLinkCommand.Refresh();
        ConnectCommand.Refresh(); DisconnectCommand.Refresh(); RetryProbeCommand.Refresh();
        DebugCommand.Refresh();
    }

    private async void RunDebug(string? scenario)
    {
#if DEBUG
        try
        {
            if (scenario is "room" or "success" || _room is null)
            {
                var inner = "http://conn.dolphinstar.cn/Remote?url=http%3A%2F%2F127.0.0.1%3A49152&token=demo-not-a-real-token&name=" + Uri.EscapeDataString("演示房间");
                await AcceptQrAsync("https://wx.chunk.cn/projection/login?place_code=DEMO&projection_url=" +
                    Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(inner))));
                _debugSimulation = true; Update();
            }
            if (scenario == "success") { ShowWechat(); ConfirmWechat(); await ConnectAsync(); }
            else if (scenario == "unreachable") { _rendererStatus = "Renderer 暂时无法访问"; Update(); }
            else if (scenario == "failure") ReportError(new InvalidOperationException("设备连接失败（开发调试模拟）。"));
            else if (scenario == "offline")
            {
                if (_connected) await DisconnectAsync();
                ReportError(new InvalidOperationException("网络已断开（开发调试模拟）。"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportError(error); }
#else
        await Task.CompletedTask;
#endif
    }

    public async ValueTask DisposeAsync()
    {
        if (_closing) return;
        _closing = true;
        ++_generation;
        await _lifetime.CancelAsync();
        _operation?.Cancel();
        async Task Cleanup(Func<Task> operation, string label)
        { try { await operation(); } catch (Exception error) { _log.Write(label, error); } }
        await Cleanup(() => _camera.DisposeAsync().AsTask(), "关闭相机");
        await _connectionGate.WaitAsync();
        try { await StopSessionCoreAsync(); }
        finally { _connectionGate.Release(); }
        _camera.PreviewFrame -= OnPreview; _camera.QrDecoded -= OnCameraQr; _camera.Failed -= OnCameraFailed;
        _connection.ProgressChanged -= OnConnectionProgress; _demoConnection.ProgressChanged -= OnConnectionProgress;
        _monitor.HealthChanged -= OnHealthChanged; _bridge.Failed -= OnBridgeFailed;
        _session.StateChanged -= OnStateChanged;
        (_renderer as IDisposable)?.Dispose();
        (_connection as IDisposable)?.Dispose();
        _operation?.Dispose(); _lifetime.Dispose();
        _log.Write("应用已退出，资源已释放");
    }
}
