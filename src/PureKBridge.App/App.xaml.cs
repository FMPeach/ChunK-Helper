using System.Windows;
using PureKBridge.App.Services;
using PureKBridge.App.ViewModels;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;

namespace PureKBridge.App;

public partial class App : Application
{
    private readonly AppLog _log = new();
    private SingleInstanceService? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _instance = new SingleInstanceService(_log);
            if (!_instance.IsPrimary)
            {
                await _instance.ActivateExistingAsync();
                Shutdown();
                return;
            }
        }
        catch (Exception error)
        {
            _log.Write("无法唤醒已有窗口", error);
            MessageBox.Show("无法打开已有窗口，请稍后重试。", AboutInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }
        _log.Write("应用启动");
        _log.Write("发现模式：房台登记 + 标准组播公告");
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Write("未处理的界面异常", args.Exception);
            if (MainWindow?.DataContext is MainViewModel model) model.ReportError(args.Exception);
            args.Handled = true;
        };
        var reader = new QrImageReader();
        var camera = new CameraService(reader, _log);
        var renderer = new RendererClient();
        var routes = new LocalRouteResolver();
        var connection = new PureKConnectionService(renderer, routes, _log.Write);
        var model = new MainViewModel(_log, reader, camera, renderer, routes, connection,
            new RendererHealthMonitor(renderer, _log.Write), new SsdpBridge(routes, _log.Write));
        MainWindow = new MainWindow(model);
        _instance.ListenForActivation(() => Dispatcher.InvokeAsync(() =>
            ((MainWindow)MainWindow).RestoreFromExternalLaunch()).Task);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
