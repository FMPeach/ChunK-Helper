using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PureKBridge.App.Services;

internal sealed class SingleInstanceService : IDisposable
{
    private readonly AppLog _log;
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly NamedPipeServerStream? _server;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _disposed;

    public bool IsPrimary { get; }

    public SingleInstanceService(AppLog log, string? name = null)
    {
        _log = log;
        // Stable across versions and install paths, but separate for each Windows user/session.
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        _name = name ?? $"ChunK.Helper.{identity.User!.Value}.{process.SessionId}";
        _mutex = new Mutex(false, @"Local\" + _name);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (!IsPrimary) return;
        try
        {
            _server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public void ListenForActivation(Func<Task> activate)
    {
        if (!IsPrimary || _listener is not null)
            throw new InvalidOperationException("只有首个实例可以接收窗口唤醒请求。");
        _listener = ListenAsync(activate);
    }

    public async Task ActivateExistingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        // The newly launched process may grant foreground activation to the existing window.
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            AllowSetForegroundWindow(processId);
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
        var reply = new byte[1];
        await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
        if (reply[0] != 1) throw new IOException("窗口唤醒请求未完成。");
    }

    private async Task ListenAsync(Func<Task> activate)
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _server!.WaitForConnectionAsync(token).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = new byte[1];
                    await _server.ReadExactlyAsync(request, timeout.Token).ConfigureAwait(false);
                    if (request[0] != 1) throw new IOException("无效的窗口唤醒请求。");
                    await activate().ConfigureAwait(false);
                    await _server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is IOException or OperationCanceledException)
                {
                    _log.Write("窗口唤醒请求未完成", error);
                }
                finally
                {
                    if (!token.IsCancellationRequested) _server.Disconnect();
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error) { _log.Write("窗口唤醒服务已停止", error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _server?.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
