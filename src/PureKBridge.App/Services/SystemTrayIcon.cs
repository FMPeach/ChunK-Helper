using System.Runtime.InteropServices;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PureKBridge.App.Services;

internal interface ISystemTrayIcon : IDisposable
{
    event Action? ShowRequested;
    event Action? ExitRequested;
    void Show();
}

internal sealed class SystemTrayIcon : ISystemTrayIcon
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Drawing.Icon _icon = CreateIcon();
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.NotifyIcon _notifyIcon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public SystemTrayIcon()
    {
        _menu.Items.Add("显示" + AboutInfo.Name, null,
            (_, _) => _dispatcher.InvokeAsync(() => ShowRequested?.Invoke()));
        _menu.Items.Add("关闭", null,
            (_, _) => _dispatcher.InvokeAsync(() => ExitRequested?.Invoke()));
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = AboutInfo.Name.Length > 63 ? AboutInfo.Name[..63] : AboutInfo.Name,
            Icon = _icon,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                _dispatcher.InvokeAsync(() => ShowRequested?.Invoke());
        };
    }

    public void Show() => _notifyIcon.Visible = true;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Drawing.Pen(Drawing.Color.FromArgb(0, 103, 192), 2.5f);
        graphics.DrawRectangle(pen, 4, 5, 24, 17);
        graphics.DrawLine(pen, 16, 22, 16, 27);
        graphics.DrawLine(pen, 10, 27, 22, 27);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
