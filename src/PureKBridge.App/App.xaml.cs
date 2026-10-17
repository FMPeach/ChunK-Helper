using System.Windows;
using PureKBridge.App.Services;
using PureKBridge.App.ViewModels;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;

namespace PureKBridge.App;

public partial class App : Application
{
    private readonly AppLog _log = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        MainWindow.Show();
    }
}
