using System.Windows.Threading;
using PureKBridge.App.Services;

namespace PureKBridge.App.ViewModels;

public sealed class WindowTrayBehavior : ObservableObject, IDisposable
{
    private readonly WindowPreferencesStore _preferences;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closeToTray;
    private bool _connected;
    private bool _disposed;
    private long _countdownStarted;
    private int _remainingSeconds;

    public WindowTrayBehavior(WindowPreferencesStore preferences, TimeProvider? clock = null)
    {
        _preferences = preferences;
        _clock = clock ?? TimeProvider.System;
        _closeToTray = preferences.LoadCloseToTray();
        CancelHideCommand = new(_ => CancelAutoHide());
        _timer.Tick += OnTick;
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (_closeToTray == value) return;
            _closeToTray = value;
            _preferences.SaveCloseToTray(value);
            Changed();
        }
    }

    public int RemainingSeconds => _remainingSeconds;
    public bool IsAutoHidePending => _remainingSeconds > 0;
    public string AutoHideMessage => $"投屏已连接，即将隐藏到系统托盘（{_remainingSeconds}）。";
    public RelayCommand CancelHideCommand { get; }
    public event Action? HideRequested;

    public void SetConnected(bool connected)
    {
        if (_disposed || connected == _connected) return;
        _connected = connected;
        CancelAutoHide();
        if (!connected) return;
        _countdownStarted = _clock.GetTimestamp();
        _remainingSeconds = 3;
        NotifyCountdown();
        _timer.Start();
    }

    public void CancelAutoHide()
    {
        _timer.Stop();
        if (_remainingSeconds == 0) return;
        _remainingSeconds = 0;
        NotifyCountdown();
    }

    private void OnTick(object? sender, EventArgs args) => AdvanceCountdown();

    internal void AdvanceCountdown()
    {
        if (_disposed || !IsAutoHidePending) return;
        var remaining = Math.Max(0, (int)Math.Ceiling(3 - _clock.GetElapsedTime(_countdownStarted).TotalSeconds));
        if (remaining == 0)
        {
            CancelAutoHide();
            HideRequested?.Invoke();
        }
        else if (_remainingSeconds != remaining)
        {
            _remainingSeconds = remaining;
            NotifyCountdown();
        }
    }

    private void NotifyCountdown()
    {
        Changed(nameof(RemainingSeconds));
        Changed(nameof(IsAutoHidePending));
        Changed(nameof(AutoHideMessage));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAutoHide();
        _timer.Tick -= OnTick;
    }
}
