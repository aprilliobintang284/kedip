using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kedip.Core;

namespace Kedip.Shell.Island;

/// <summary>Picks a vivid, readable-on-black accent colour from album art (computed once per thumbnail).</summary>
internal static class ArtColor
{
    private const int Sample = 10;

    /// <summary>The dominant vivid colour of <paramref name="source"/>, or null for greyscale/unsupported images.</summary>
    public static Color? Dominant(ImageSource? source)
    {
        if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return null;
        }

        try
        {
            BitmapSource small = new TransformedBitmap(
                bitmap, new ScaleTransform((double)Sample / bitmap.PixelWidth, (double)Sample / bitmap.PixelHeight));
            small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int width = small.PixelWidth;
            int height = small.PixelHeight;
            var pixels = new byte[width * height * 4];
            small.CopyPixels(pixels, width * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128)
                {
                    continue;
                }

                var (_, s, v) = ToHsv(pixels[i + 2] / 255.0, pixels[i + 1] / 255.0, pixels[i] / 255.0);
                double weight = s * s * v + 0.002;
                r += pixels[i + 2] * weight;
                g += pixels[i + 1] * weight;
                b += pixels[i] * weight;
                total += weight;
            }

            if (total <= 0)
            {
                return null;
            }

            var (hue, sat, _) = ToHsv(r / total / 255.0, g / total / 255.0, b / total / 255.0);
            if (sat < 0.18)
            {
                return null;
            }

            // Lift to a bright, slightly softened tone that reads well on pure black.
            return FromHsv(hue, Math.Clamp(sat * 1.1, 0.45, 0.75), 0.95);
        }
        catch (Exception ex)
        {
            Log.Warn($"Album art colour skipped: {ex.Message}");
            return null;
        }
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            h = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        }

        return (h < 0 ? h + 360 : h, max <= 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
