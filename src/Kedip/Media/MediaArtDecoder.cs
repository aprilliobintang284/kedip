using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace Kedip.Media;

/// <summary>Square, downscaled album art pixels decoded off the UI thread.</summary>
internal sealed class DecodedArt
{
    public required string Hash { get; init; }
    public byte[]? Pixels { get; init; }
    public int Size { get; init; }

    /// <summary>Creates the frozen WPF image. Call on the UI thread (cheap: a single copy of a small buffer).</summary>
    public ImageSource? ToImageSource()
    {
        if (Pixels is null || Size <= 0)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, Pixels, Size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Decodes media-session thumbnails with WinRT imaging on a worker thread: centre-cropped to a square and scaled
/// to about 96 DIPs at the system DPI, so a 1280×720 video frame and a 640 px cover both end up a few dozen KB.
/// </summary>
internal static class MediaArtDecoder
{
    private const int MaxEncodedBytes = 16 * 1024 * 1024;
    private const double TargetDips = 96;

    private static readonly int TargetPixels = ComputeTargetPixels();

    /// <summary>
    /// Returns the decoded art, a result with only <see cref="DecodedArt.Hash"/> set when the bytes match
    /// <paramref name="knownHash"/>, or null when the stream is empty or unreadable.
    /// </summary>
    public static async Task<DecodedArt?> DecodeAsync(IRandomAccessStreamReference reference, string knownHash)
    {
        using var buffer = new MemoryStream();
        using (var source = await reference.OpenReadAsync())
        {
            if (source is null || source.Size == 0 || source.Size > MaxEncodedBytes)
            {
                return null;
            }

            using var input = source.AsStreamForRead();
            await input.CopyToAsync(buffer);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        string hash = Convert.ToHexString(SHA1.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        if (hash == knownHash)
        {
            return new DecodedArt { Hash = hash };
        }

        buffer.Position = 0;
        using var stream = buffer.AsRandomAccessStream();
        var decoder = await WinBitmapDecoder.CreateAsync(stream);
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        if (width == 0 || height == 0)
        {
            return null;
        }

        uint shortSide = Math.Min(width, height);
        uint size = Math.Min((uint)TargetPixels, shortSide);
        double scale = (double)size / shortSide;
        uint scaledWidth = Math.Max(size, (uint)Math.Round(width * scale));
        uint scaledHeight = Math.Max(size, (uint)Math.Round(height * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds
            {
                X = (scaledWidth - size) / 2,
                Y = (scaledHeight - size) / 2,
                Width = size,
                Height = size,
            },
        };

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        if (pixels.Length < size * size * 4)
        {
            return null;
        }

        return new DecodedArt { Hash = hash, Pixels = pixels, Size = (int)size };
    }

    private static int ComputeTargetPixels()
    {
        double dpi = 96;
        try
        {
            uint system = GetDpiForSystem();
            if (system is >= 96 and <= 480)
            {
                dpi = system;
            }
        }
        catch (Exception)
        {
        }

        return (int)Math.Ceiling(TargetDips * dpi / 96);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
