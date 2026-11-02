using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kedip.Core;

namespace Kedip.Shell.Island;

/// <summary>A short message: tinted glyph badge + title (+ subtitle). Laid out at its own size and revealed by the island.</summary>
public partial class PeekView : UserControl
{
    public const double MaxPeekWidth = 360;

    private static readonly Brush NeutralGlyph = Paint.Solid(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Brush NeutralBadge = Paint.Solid(0x1F, 0xFF, 0xFF, 0xFF);

    internal PeekView()
    {
        InitializeComponent();
    }

    /// <summary>Fills in <paramref name="request"/> and returns the island body size it needs.</summary>
    internal Size Prepare(PeekRequest request, double baseWidth, double baseHeight)
    {
        bool hasSubtitle = !string.IsNullOrWhiteSpace(request.Subtitle);
        double height = baseHeight + (hasSubtitle ? 18 : 10);
        double badge = Math.Min(28, height - 18);
        double inset = Math.Round((height - badge) / 2);
        const double rightPad = 18;
        const double gap = 10;

        var accent = Paint.FromHex(request.AccentHex, NeutralGlyph);
        GlyphText.Text = string.IsNullOrEmpty(request.Glyph) ? DefaultGlyph(request.Kind) : request.Glyph;
        GlyphText.Foreground = accent;
        GlyphText.FontSize = Math.Round(badge * 0.5);
        BadgeBack.Fill = accent is SolidColorBrush solid && !ReferenceEquals(accent, NeutralGlyph)
            ? Paint.Solid(Color.FromArgb(0x2E, solid.Color.R, solid.Color.G, solid.Color.B))
            : NeutralBadge;
        Badge.Width = badge;
        Badge.Height = badge;

        TitleText.Text = request.Title;
        SubtitleText.Text = request.Subtitle ?? "";
        SubtitleText.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        TextStack.Margin = new Thickness(gap, 0, 0, hasSubtitle ? 1 : 0);

        double maxText = MaxPeekWidth - inset - badge - gap - rightPad;
        TitleText.MaxWidth = maxText;
        SubtitleText.MaxWidth = maxText;
        TextStack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = Math.Ceiling(Math.Min(TextStack.DesiredSize.Width, maxText));

        double width = Math.Clamp(inset + badge + gap + textWidth + rightPad, baseWidth, MaxPeekWidth);
        Layout.Margin = new Thickness(inset, 0, 0, 0);
        Width = width;
        Height = height;
        return new Size(width, height);
    }

    internal void Enter(bool animate)
    {
        Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Zoom.ScaleX = animate ? 0.9 : 1;
        Zoom.ScaleY = animate ? 0.9 : 1;
        Motion.To(Zoom, ScaleTransform.ScaleXProperty, 1, Motion.Grow, Motion.Spring, animate);
        Motion.To(Zoom, ScaleTransform.ScaleYProperty, 1, Motion.Grow, Motion.Spring, animate);
        Motion.FadeTo(this, true, animate, TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(70));
    }

    internal void Leave(bool animate) => Motion.FadeTo(this, false, animate, TimeSpan.FromMilliseconds(110));

    private static string DefaultGlyph(PeekKind kind) => kind switch
    {
        PeekKind.Charging => Glyphs.Bolt,
        PeekKind.Battery => Glyphs.Battery,
        PeekKind.Music => Glyphs.Note,
        PeekKind.Timer => Glyphs.Timer,
        PeekKind.Water => Glyphs.Drop,
        PeekKind.Prayer => Glyphs.Moon,
        PeekKind.Shelf => Glyphs.Shelf,
        _ => Glyphs.Info,
    };
}
