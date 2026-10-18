using System.ComponentModel;
using System.Windows;
using PureKBridge.App.Services;
using PureKBridge.App.ViewModels;
using PureKBridge.Core.Session;

namespace PureKBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly ISystemTrayIcon _tray;
    private bool _canClose;
    private bool _exiting;
    private Task? _exitTask;

    public WindowTrayBehavior TrayBehavior { get; }

    private void OpenAbout(object sender, RoutedEventArgs e)
    {
        TrayBehavior.CancelAutoHide();
        new AboutWindow { Owner = this }.ShowDialog();
    }

    public MainWindow(MainViewModel model) : this(model, new SystemTrayIcon(),
        new WindowPreferencesStore(new AppLog())) { }

    internal MainWindow(MainViewModel model, ISystemTrayIcon tray, WindowPreferencesStore preferences,
        TimeProvider? clock = null, bool initializeCamera = true)
    {
        _model = model;
        _tray = tray;
        TrayBehavior = new WindowTrayBehavior(preferences, clock);
        InitializeComponent();
        DataContext = model;
        async void InitializeOnce(object sender, RoutedEventArgs args)
        {
            Loaded -= InitializeOnce;
            if (initializeCamera) await model.InitializeAsync();
        }
        Loaded += InitializeOnce;
        _model.PropertyChanged += OnModelChanged;
        TrayBehavior.HideRequested += HideToTray;
        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += OnTrayExit;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            TrayBehavior.HideRequested -= HideToTray;
            TrayBehavior.Dispose();
            _tray.ShowRequested -= ShowFromTray;
            _tray.ExitRequested -= OnTrayExit;
            _tray.Dispose();
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_exiting && (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.State)))
            TrayBehavior.SetConnected(_model.State == ConnectionState.Connected);
    }

    private void HideToTray()
    {
        if (_exiting) return;
        try { _tray.Show(); }
        catch (Exception error) { _model.ReportError(error); return; }
        TrayBehavior.CancelAutoHide();
        Hide();
    }

    private void ShowFromTray()
    {
        if (_exiting) return;
        TrayBehavior.CancelAutoHide();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnTrayExit() => await ExitAsync();

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true;
        if (_exiting) return;
        if (TrayBehavior.CloseToTray) HideToTray();
        else await ExitAsync();
    }

    internal Task ExitAsync()
    {
        if (_exitTask is not null) return _exitTask;
        _exiting = true;
        TrayBehavior.CancelAutoHide();
        IsEnabled = false;
        return _exitTask = ExitCoreAsync();
    }

    private async Task ExitCoreAsync()
    {
        try { await _model.DisposeAsync(); }
        finally
        {
            // Cleanup can finish synchronously; close on the next dispatcher turn, not inside Closing.
            await Dispatcher.InvokeAsync(() => { _canClose = true; Close(); });
        }
    }
}
