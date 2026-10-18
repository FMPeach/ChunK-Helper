using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PureKBridge.App;
using PureKBridge.App.Services;
using PureKBridge.App.ViewModels;
using PureKBridge.Core.Models;
using PureKBridge.Core.Network;
using PureKBridge.Core.PureK;
using PureKBridge.Core.Session;

namespace PureKBridge.Tests;

internal static class TrayWindowTests
{
    public static async Task RunAsync(string artifacts, Action<bool, string> check)
    {
        var folder = Path.Combine(artifacts, "tray", Guid.NewGuid().ToString("N"));
        var settings = new WindowPreferencesStore(new AppLog(), Path.Combine(folder, "settings.json"));
        check(settings.LoadCloseToTray(), "默认勾选关闭后隐藏到托盘");
        settings.SaveCloseToTray(false);
        check(!settings.LoadCloseToTray(), "关闭隐藏偏好可保存并重新读取");
        settings.SaveCloseToTray(true);

        var clock = new TestClock();
        using (var behavior = new WindowTrayBehavior(settings, clock))
        {
            var hides = 0;
            behavior.HideRequested += () => hides++;
            behavior.SetConnected(true);
            check(behavior.RemainingSeconds == 3 && behavior.IsAutoHidePending, "连接成功显示三秒隐藏倒计时");
            clock.Advance(1);
            behavior.AdvanceCountdown();
            check(behavior.RemainingSeconds == 2, "倒计时按经过时间更新到二秒");
            behavior.SetConnected(true);
            clock.Advance(1);
            behavior.AdvanceCountdown();
            check(behavior.RemainingSeconds == 1, "普通在线状态刷新不会重新开始倒计时");
            clock.Advance(1);
            behavior.AdvanceCountdown();
            behavior.AdvanceCountdown();
            check(hides == 1 && !behavior.IsAutoHidePending, "三秒后只请求隐藏一次并停止倒计时");

            behavior.SetConnected(false);
            behavior.SetConnected(true);
            behavior.CancelHideCommand.Execute(null);
            behavior.SetConnected(true);
            clock.Advance(10);
            behavior.AdvanceCountdown();
            check(hides == 1 && !behavior.IsAutoHidePending, "取消隐藏后持续在线也不会再次自动隐藏");
            behavior.SetConnected(false);
            behavior.SetConnected(true);
            behavior.SetConnected(false);
            clock.Advance(10);
            behavior.AdvanceCountdown();
            check(hides == 1, "倒计时内断开连接会撤销自动隐藏");
            behavior.SetConnected(true);
            behavior.Dispose();
            clock.Advance(10);
            behavior.AdvanceCountdown();
            check(hides == 1 && !behavior.IsAutoHidePending, "退出后没有倒计时或迟到的隐藏事件");
        }

        using (var nativeTray = new SystemTrayIcon())
        {
            nativeTray.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        check(true, "原生系统托盘图标可创建并正常释放");

        await using var server = new FakeRenderer();
        var routes = new Routes();
        var log = new AppLog();
        var images = new QrImageReader();
        using var client = new RendererClient();
        using var connection = new PureKConnectionService(client, routes);
        var bridge = new TestBridge();
        var monitor = new TestMonitor();
        var model = new MainViewModel(log, images, new CameraService(images, log), client, routes, connection, monitor, bridge);
        var tray = new TestTray();
        var window = new MainWindow(model, tray, settings, clock, initializeCamera: false);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var checkbox = Descendants<CheckBox>(window).Single(box => Equals(box.Content, "点击关闭后隐藏到托盘"));
            check(checkbox.IsChecked == true, "右下角关闭隐藏开关绑定到已保存偏好");
            await model.AcceptQrAsync(ProtocolTests.Qr(server.BaseUrl));
            model.NextCommand.Execute(null);
            model.ConfirmWechat();
            await model.ConnectAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var cancel = (Hyperlink)window.FindName("CancelHideLink");
            var notice = (TextBlock)window.FindName("AutoHideNotice");
            check(window.TrayBehavior.RemainingSeconds == 3 && notice.IsVisible && notice.Inlines.OfType<Run>().Any(run => run.Text.Contains("3")) &&
                cancel.Command is not null && cancel.Inlines.OfType<Run>().Any(run => run.Text == "取消隐藏"),
                "窗口左下角显示连接倒计时且取消隐藏链接绑定有效");
            foreach (var width in new[] { 640, 760 })
            {
                window.Width = width;
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                    (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(folder, $"countdown-{width}.png"));
                encoder.Save(stream);
            }
            cancel.Command!.Execute(null);
            clock.Advance(4);
            window.TrayBehavior.AdvanceCountdown();
            check(window.IsVisible && tray.Shows == 0, "点击消息中的取消隐藏会保留窗口");

            await model.DisconnectAsync();
            await model.ConnectAsync();
            var stops = monitor.Stops;
            var revokes = RevokeCount(server);
            clock.Advance(3);
            window.TrayBehavior.AdvanceCountdown();
            check(!window.IsVisible && tray.Shows == 1 && model.State == ConnectionState.Connected && bridge.IsRunning &&
                monitor.Stops == stops && RevokeCount(server) == revokes,
                "自动隐藏只收起窗口，不停止探活、公告或撤销登记");
            tray.RequestShow();
            check(window.IsVisible && !window.TrayBehavior.IsAutoHidePending, "托盘显示恢复窗口且不重复自动隐藏");
            window.Close();
            check(!closed && !window.IsVisible && model.State == ConnectionState.Connected && bridge.IsRunning &&
                monitor.Stops == stops && RevokeCount(server) == revokes,
                "点击窗口关闭只进入托盘，保持房台连接");
            tray.RequestShow();
            window.WindowState = WindowState.Minimized;
            tray.RequestShow();
            check(window.IsVisible && window.WindowState == WindowState.Normal, "托盘显示也能恢复最小化窗口");
            window.Close();
            tray.RequestExit();
            var exit = window.ExitAsync();
            check(ReferenceEquals(exit, window.ExitAsync()), "重复退出请求共用一次清理任务");
            await exit.WaitAsync(TimeSpan.FromSeconds(5));
            check(closed && tray.Disposed && !bridge.IsRunning && monitor.Stops == stops + 1 && RevokeCount(server) == revokes + 1,
                "托盘关闭真正退出且只撤销一次登记、停止公告并释放图标");
        }
        finally { await window.ExitAsync(); }

        using var otherClient = new RendererClient();
        using var otherConnection = new PureKConnectionService(otherClient, routes);
        var otherModel = new MainViewModel(log, images, new CameraService(images, log), otherClient, routes,
            otherConnection, new TestMonitor(), new TestBridge());
        var otherTray = new TestTray();
        var otherWindow = new MainWindow(otherModel, otherTray, settings, initializeCamera: false);
        var otherClosed = false;
        otherWindow.Closed += (_, _) => otherClosed = true;
        try
        {
            otherWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var checkbox = Descendants<CheckBox>(otherWindow).Single(box => Equals(box.Content, "点击关闭后隐藏到托盘"));
            checkbox.IsChecked = false;
            check(!otherWindow.TrayBehavior.CloseToTray && !settings.LoadCloseToTray(), "取消勾选会同步保存关闭行为");
            otherWindow.Close();
            await otherWindow.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(otherClosed && otherTray.Disposed && otherTray.Shows == 0, "取消勾选后窗口关闭直接退出而非隐藏");
        }
        finally { await otherWindow.ExitAsync(); }
    }

    private static int RevokeCount(FakeRenderer server) =>
        server.Requests.Count(request => request.Method == "POST" && request.Body.Contains("<Enable>0</Enable>"));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class TestTray : ISystemTrayIcon
    {
        public int Shows;
        public bool Disposed;
        public event Action? ShowRequested;
        public event Action? ExitRequested;
        public void Show() => Shows++;
        public void RequestShow() => ShowRequested?.Invoke();
        public void RequestExit() => ExitRequested?.Invoke();
        public void Dispose() => Disposed = true;
    }

    private sealed class Routes : ILocalRouteResolver
    {
        public Task<IPAddress> ResolveAsync(Uri renderer, CancellationToken cancellationToken) => Task.FromResult(IPAddress.Loopback);
    }

    private sealed class TestBridge : ISsdpBridge
    {
        public bool IsRunning { get; private set; }
        public event Action<Exception>? Failed { add { } remove { } }
        public Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken = default)
        { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
    }

    private sealed class TestMonitor : IRendererHealthMonitor
    {
        public int Stops;
        public event Action<RendererHealth>? HealthChanged { add { } remove { } }
        public Task StartAsync(KnownRenderer renderer, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; return Task.CompletedTask; }
    }
}
