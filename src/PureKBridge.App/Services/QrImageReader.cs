using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace PureKBridge.App.Services;

public sealed class QrImageReader
{
    private readonly object _gate = new();
    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            PossibleFormats = new[] { BarcodeFormat.QR_CODE }
        }
    };

    public string? DecodeBgra(byte[] pixels, int width, int height)
    {
        lock (_gate)
            return _reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32)?.Text;
    }

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > 4096)
            source = new TransformedBitmap(source, new ScaleTransform(4096.0 / longest, 4096.0 / longest));
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return DecodeBgra(pixels, converted.PixelWidth, converted.PixelHeight);
    }, cancellationToken);
}
