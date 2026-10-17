using System.ComponentModel;
using System.Windows;
using PureKBridge.App.ViewModels;

namespace PureKBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _canClose;

    private void OpenAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = _model = model;
        Loaded += async (_, _) => await model.InitializeAsync();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true;
        IsEnabled = false;
        try { await _model.DisposeAsync(); }
        finally
        {
            // Cleanup can finish synchronously; close on the next dispatcher turn, not inside Closing.
            await Dispatcher.InvokeAsync(() => { _canClose = true; Close(); });
        }
    }
}
