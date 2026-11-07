using System.Windows;
using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace Kedip.DockBar;

/// <summary>The Windows accent colour for the dock's "on" states, in the light shade Windows itself uses on dark surfaces.</summary>
internal static class DockAccent
{
    private static readonly Color Fallback = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color LightFallback = Color.FromRgb(0x00, 0x67, 0xC0);

    public static void Apply(ResourceDictionary resources)
    {
        bool light = DockPalette.IsLight;
        var color = Read(light);
        resources["DockAccentBrush"] = Brush(color, 0xFF);
        resources["DockAccentHoverBrush"] = Brush(color, 0xE0);
        resources["DockOnAccentBrush"] = Brush(light ? Colors.White : Colors.Black, light ? (byte)0xFF : (byte)0xE6);
    }

    private static Color Read(bool light)
    {
        try
        {
            var c = new UISettings().GetColorValue(light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception)
        {
            return light ? LightFallback : Fallback;
        }
    }

    private static SolidColorBrush Brush(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
