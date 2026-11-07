using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kedip.Core;

namespace Kedip.DockBar;

/// <summary>
/// The dock's colours. Every brush is created once and registered in the application resources, so the dock, its
/// panels and menus all share them; switching the look animates the very same brush objects (nothing is rebuilt).
/// </summary>
internal static class DockPalette
{
    private static readonly byte[] InkAlphas =
    [
        0x0A, 0x0D, 0x0F, 0x10, 0x12, 0x14, 0x16, 0x17, 0x1A, 0x1C, 0x1F, 0x22, 0x24, 0x26, 0x28, 0x2E, 0x38, 0x40, 0x4A, 0x4D, 0x99, 0xB3, 0xD9,
        0x05, 0x08, 0x0B, 0x47, 0x59, 0x61, 0x66, 0x33, 0x8C, 0xEB, 0xF2, 0xFF,
    ];

    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(320);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private static readonly Dictionary<byte, Slot> Ink = new();
    private static readonly Dictionary<byte, Brush> InkBrushes = new();
    private static Slot _text = null!, _text2 = null!, _menu = null!, _menuEdge = null!, _hover = null!, _press = null!,
        _line = null!, _card = null!, _cardEdge = null!, _knob = null!, _notch = null!;
    private static Slot[] _surface = null!, _panel = null!, _edge = null!, _tile = null!, _tileActive = null!, _tileEdge = null!;

    /// <summary>
    /// One animated colour. Each brush's colour is bound to its slot rather than set directly: WPF freezes any plain
    /// brush put into the application resources, and a frozen brush could never change with the look.
    /// </summary>
    private sealed class Slot : FrameworkElement
    {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(Slot));

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public void Bind(DependencyObject target, DependencyProperty property) =>
            BindingOperations.SetBinding(target, property, new Binding(nameof(Color)) { Source = this, Mode = BindingMode.OneWay });
    }
    private static bool _ready;

    /// <summary>True for the two light looks.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Creates and registers the brushes (call once, before any dock window loads).</summary>
    public static void Init(DockLook look)
    {
        if (_ready)
        {
            return;
        }

        var res = Application.Current.Resources;
        foreach (byte a in InkAlphas)
        {
            var slot = new Slot();
            var brush = new SolidColorBrush();
            slot.Bind(brush, SolidColorBrush.ColorProperty);
            Ink[a] = slot;
            InkBrushes[a] = brush;
            res["Ink" + a.ToString("X2")] = brush;
        }

        _text = Solid(res, "DockTextBrush");
        _text2 = Solid(res, "DockText2Brush");
        _menu = Solid(res, "DockMenu");
        _menuEdge = Solid(res, "DockMenuEdge");
        _hover = Solid(res, "DockHover");
        _press = Solid(res, "DockPress");
        _line = Solid(res, "DockLine");
        _card = Solid(res, "PanelCard");
        _cardEdge = Solid(res, "PanelCardEdge");
        _knob = Solid(res, "DockKnob");
        _notch = Solid(res, "NotchBrush");
        _surface = Gradient(res, "DockSurface");
        _panel = Gradient(res, "PanelSurface");
        _edge = Gradient(res, "DockEdge", 3);
        _tile = Gradient(res, "AppTileFill");
        _tileActive = Gradient(res, "AppTileActiveFill");
        _tileEdge = Gradient(res, "AppTileEdge");
        _ready = true;
        Set(look, animate: false);
    }

    /// <summary>The shared brush for a white (dark looks) or black (light looks) tint of the given alpha.</summary>
    public static Brush Tint(byte alpha) => InkBrushes.TryGetValue(alpha, out var brush) ? brush : Brushes.Transparent;

    public static void Set(DockLook look, bool animate)
    {
        if (!_ready)
        {
            return;
        }

        bool light = look is DockLook.Light or DockLook.LightGlass;
        bool glass = look is DockLook.DarkGlass or DockLook.LightGlass;
        IsLight = light;
        var ink = light ? Colors.Black : Colors.White;

        foreach (var (alpha, slot) in Ink)
        {
            To(slot, Color.FromArgb(alpha, ink.R, ink.G, ink.B), animate);
        }

        To(_text, light ? Rgb(0xFF, 0x1A, 0x1A, 0x1E) : Rgb(0xFF, 0xFF, 0xFF, 0xFF), animate);
        To(_text2, light ? Rgb(0x99, 0x1A, 0x1A, 0x1E) : Rgb(0x99, 0xFF, 0xFF, 0xFF), animate);
        To(_menu, light ? Rgb(0xF4, 0xF7, 0xF7, 0xF9) : Rgb(0xF2, 0x0D, 0x0D, 0x0F), animate);
        To(_menuEdge, InkColor(light, 0x24), animate);
        To(_hover, InkColor(light, light ? (byte)0x14 : (byte)0x1A), animate);
        To(_press, InkColor(light, light ? (byte)0x0C : (byte)0x0F), animate);
        To(_line, InkColor(light, light ? (byte)0x1A : (byte)0x1C), animate);
        To(_card, light ? Rgb(0xA6, 0xFF, 0xFF, 0xFF) : Rgb(0x14, 0xFF, 0xFF, 0xFF), animate);
        To(_cardEdge, InkColor(light, light ? (byte)0x14 : (byte)0x10), animate);
        To(_notch, (glass, light) switch
        {
            (true, false) => Rgb(0xE0, 0x04, 0x04, 0x05),
            (true, true) => Rgb(0xDC, 0xF6, 0xF6, 0xF8),
            (false, false) => Rgb(0xFF, 0x00, 0x00, 0x00),
            _ => Rgb(0xFF, 0xF6, 0xF6, 0xF8),
        }, animate);
        To(_knob, light ? Rgb(0xFF, 0xE4, 0xE4, 0xE8) : Rgb(0xFF, 0x3A, 0x3A, 0x3D), animate);

        // Surfaces: translucent (the blur behind shows through) for the glass looks, opaque otherwise.
        (Color a, Color b) dock = (glass, light) switch
        {
            (true, false) => (Rgb(0x5C, 0x0F, 0x0F, 0x12), Rgb(0x78, 0x06, 0x06, 0x07)),
            (true, true) => (Rgb(0x8A, 0xF6, 0xF6, 0xF8), Rgb(0xA6, 0xED, 0xED, 0xF1)),
            (false, false) => (Rgb(0xFF, 0x1A, 0x1A, 0x1F), Rgb(0xFF, 0x0F, 0x0F, 0x12)),
            _ => (Rgb(0xFF, 0xF8, 0xF8, 0xFA), Rgb(0xFF, 0xEC, 0xEC, 0xF0)),
        };
        (Color a, Color b) panel = (glass, light) switch
        {
            (true, false) => (Rgb(0x80, 0x0E, 0x0E, 0x11), Rgb(0x99, 0x06, 0x06, 0x07)),
            (true, true) => (Rgb(0xA6, 0xF8, 0xF8, 0xFA), Rgb(0xBC, 0xEE, 0xEE, 0xF2)),
            (false, false) => (Rgb(0xFF, 0x17, 0x17, 0x1B), Rgb(0xFF, 0x0E, 0x0E, 0x11)),
            _ => (Rgb(0xFF, 0xFA, 0xFA, 0xFC), Rgb(0xFF, 0xF0, 0xF0, 0xF4)),
        };
        Stops(_surface, dock.a, dock.b, animate);
        Stops(_panel, panel.a, panel.b, animate);
        Stops(_edge, light ? Rgb(0x30, 0, 0, 0) : Rgb(0x40, 0xFF, 0xFF, 0xFF), light ? Rgb(0x1E, 0, 0, 0) : Rgb(0x1C, 0xFF, 0xFF, 0xFF), animate, light ? Rgb(0x14, 0, 0, 0) : Rgb(0x14, 0xFF, 0xFF, 0xFF));
        Stops(_tile, light ? Rgb(0xB3, 0xFF, 0xFF, 0xFF) : Rgb(0x2E, 0xFF, 0xFF, 0xFF), light ? Rgb(0x66, 0xFF, 0xFF, 0xFF) : Rgb(0x0F, 0xFF, 0xFF, 0xFF), animate);
        Stops(_tileActive, light ? Rgb(0xE6, 0xFF, 0xFF, 0xFF) : Rgb(0x4A, 0xFF, 0xFF, 0xFF), light ? Rgb(0xB3, 0xFF, 0xFF, 0xFF) : Rgb(0x1F, 0xFF, 0xFF, 0xFF), animate);
        Stops(_tileEdge, light ? Rgb(0x26, 0, 0, 0) : Rgb(0x4D, 0xFF, 0xFF, 0xFF), light ? Rgb(0x0C, 0, 0, 0) : Rgb(0x0A, 0xFF, 0xFF, 0xFF), animate);
    }

    private static Color Rgb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    private static Color InkColor(bool light, byte alpha) => light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255);

    private static Slot Solid(ResourceDictionary res, string key)
    {
        var slot = new Slot();
        var brush = new SolidColorBrush();
        slot.Bind(brush, SolidColorBrush.ColorProperty);
        res[key] = brush;
        return slot;
    }

    private static Slot[] Gradient(ResourceDictionary res, string key, int stops = 2)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var slots = new Slot[stops];
        for (int i = 0; i < stops; i++)
        {
            slots[i] = new Slot();
            var stop = new GradientStop { Offset = stops == 3 && i == 1 ? 0.45 : i == 0 ? 0 : 1 };
            slots[i].Bind(stop, GradientStop.ColorProperty);
            brush.GradientStops.Add(stop);
        }

        res[key] = brush;
        return slots;
    }

    /// <summary>Top and bottom colours; the edge gradient also has a middle stop (a quieter band along the sides).</summary>
    private static void Stops(Slot[] stops, Color top, Color bottom, bool animate, Color? middle = null)
    {
        To(stops[0], top, animate);
        To(stops[^1], bottom, animate);
        if (middle is { } m && stops.Length == 3)
        {
            To(stops[1], m, animate);
        }
    }

    private static void To(Slot slot, Color color, bool animate)
    {
        if (!animate)
        {
            slot.BeginAnimation(Slot.ColorProperty, null);
            slot.Color = color;
            return;
        }

        slot.BeginAnimation(Slot.ColorProperty, new ColorAnimation(color, Fade) { EasingFunction = Ease });
    }
}
