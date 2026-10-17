using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace PureKBridge.App.Services;

public sealed record CameraDevice(string Id, string Name);

public sealed class CameraService(QrImageReader decoder, AppLog log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly object _frameGate = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private TaskCompletionSource? _processing;
    private long _lastPreview;
    private long _lastDecode;
    private bool _decoded;

    public event Action<BitmapSource>? PreviewFrame;
    public event Action<string>? QrDecoded;
    public event Action<Exception>? Failed;

    public static async Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);
        return devices.Select(d => new CameraDevice(d.Id, d.Name)).ToArray();
    }

    public async Task StartAsync(CameraDevice device, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            _capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };
            var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask(cancellationToken);
            var group = groups.FirstOrDefault(g => g.SourceInfos.Any(info =>
                info.SourceKind == MediaFrameSourceKind.Color && info.DeviceInformation?.Id == device.Id));
            if (group is not null) settings.SourceGroup = group;
            else settings.VideoDeviceId = device.Id;
            await _capture.InitializeAsync(settings).AsTask(cancellationToken);
            _capture.Failed += OnCaptureFailed;
            var source = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new InvalidOperationException("摄像头没有提供可读取的彩色视频帧。");
            var format = source.CurrentFormat.VideoFormat;
            var scale = Math.Min(1.0, 640.0 / Math.Max(1, format.Width));
            var size = new BitmapSize
            {
                Width = (uint)Math.Max(2, (int)(format.Width * scale) / 2 * 2),
                Height = (uint)Math.Max(2, (int)(format.Height * scale) / 2 * 2)
            };
            var reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, size)
                .AsTask(cancellationToken);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            lock (_frameGate)
            {
                _reader = reader;
                _decoded = false;
                _lastPreview = 0;
                _lastDecode = 0;
            }
            reader.FrameArrived += OnFrameArrived;
            var status = await reader.StartAsync().AsTask(cancellationToken);
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException("摄像头不可用，请关闭其它占用相机的程序后重试。");
            log.Write($"摄像头开始：{device.Name}，输出 {size.Width}×{size.Height}");
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        TaskCompletionSource completion;
        lock (_frameGate)
        {
            if (sender != _reader || _processing is not null) return;
            _processing = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null || Stopwatch.GetElapsedTime(_lastPreview).TotalMilliseconds < 80) return;
            _lastPreview = Stopwatch.GetTimestamp();
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var bytes = new byte[checked(width * height * 4)];
            var buffer = new Windows.Storage.Streams.Buffer((uint)bytes.Length);
            converted.CopyToBuffer(buffer);
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
            image.Freeze();
            PreviewFrame?.Invoke(image);
            if (!_decoded && Stopwatch.GetElapsedTime(_lastDecode).TotalMilliseconds >= 250)
            {
                _lastDecode = Stopwatch.GetTimestamp();
                var text = decoder.DecodeBgra(bytes, width, height);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _decoded = true;
                    QrDecoded?.Invoke(text);
                }
            }
        }
        catch (Exception error)
        {
            log.Write("相机帧读取失败", error);
            Failed?.Invoke(error);
        }
        finally
        {
            lock (_frameGate) _processing = null;
            completion.TrySetResult();
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        if (sender != _capture) return;
        Failed?.Invoke(new InvalidOperationException($"相机读取失败（0x{args.Code:X8}）。"));
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        MediaFrameReader? reader;
        Task? pending;
        lock (_frameGate)
        {
            reader = _reader;
            _reader = null;
            pending = _processing?.Task;
        }
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await reader.StopAsync(); }
            catch (Exception error) { log.Write("停止相机读取器", error); }
            if (pending is not null) await pending;
            reader.Dispose();
        }
        if (_capture is not null)
        {
            _capture.Failed -= OnCaptureFailed;
            _capture.Dispose();
            _capture = null;
            log.Write("摄像头已释放");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
