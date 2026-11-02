using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kedip.Mascot;

namespace Kedip.Shell.Island;

/// <summary>Collapsed island content: centre (lyric / eyes / clock) and the two wings. Stretched over the animated body.</summary>
public partial class CollapsedView : UserControl
{
    /// <summary>Widest the collapsed island may grow for a lyric line (including wings).</summary>
    public const double MaxLyricWidth = 420;

    private static readonly Brush EqualizerFallback = Paint.Solid(0xE6, 0xFF, 0xFF, 0xFF);
    private static readonly TimeSpan LyricIn = TimeSpan.FromMilliseconds(200);

    private CollapsedContent? _shown;
    private double _metricsHeight = -1;
    private double _wing = 30;
    private double _lyricPad = 12;
    private ImageSource? _artSource;
    private bool _hasArtSource;
    private TextBlock _activeLyric;
    private string? _lyricText;
    private double _lyricWidth;

    internal CollapsedView()
    {
        InitializeComponent();
        _activeLyric = LyricA;
        Eyes = new EyesControl { IsHitTestVisible = false };
        EyesHost.Child = Eyes;
        Equalizer.BarBrush = EqualizerFallback;
    }

    public EyesControl Eyes { get; }

    internal bool ShowsEyes => !_centreHidden && _shown?.Centre == CentreContent.Eyes;

    private bool _centreHidden;

    /// <summary>Shows <paramref name="content"/> (fading only what changed) and returns the body width it needs.</summary>
    /// <param name="hideCentre">Keep only the wings (something else, e.g. the beat wave, is drawn in the centre).</param>
    internal double Apply(CollapsedContent content, double baseWidth, double baseHeight, bool animate, bool hideCentre = false)
    {
        if (baseHeight != _metricsHeight)
        {
            ApplyMetrics(baseHeight);
        }

        var previous = _shown;
        _shown = content;
        _centreHidden = hideCentre;
        CentreContent? centre = hideCentre ? null : content.Centre;

        Motion.FadeTo(ClockText, centre == CentreContent.Clock, animate);
        Motion.FadeTo(EyesHost, centre == CentreContent.Eyes, animate);
        if (centre == CentreContent.Lyric)
        {
            ShowLyric(content.Lyric ?? "", baseWidth, animate && previous is not null);
        }
        else
        {
            HideLyrics(animate);
        }

        if (!_hasArtSource || !ReferenceEquals(content.Art, _artSource))
        {
            SetArt(content.Art, animate && content.Left == LeftWing.Art && previous?.Left == LeftWing.Art);
        }

        Motion.FadeTo(ArtFrame, content.Left == LeftWing.Art, animate);
        Motion.FadeTo(BoltGlyph, content.Left == LeftWing.Bolt, animate);
        Equalizer.IsActive = content.Right == RightWing.Equalizer;
        Motion.FadeTo(Equalizer, content.Right == RightWing.Equalizer, animate);
        BatteryText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(content.BatteryPercent, 0, 100)}%");
        Motion.FadeTo(BatteryText, content.Right == RightWing.Battery, animate);

        double wings = content.HasWings ? 2 * _wing : 0;
        if (centre == CentreContent.Lyric)
        {
            return Math.Clamp(_lyricWidth + 2 * _lyricPad + wings, baseWidth + wings, Math.Max(MaxLyricWidth, baseWidth + wings));
        }

        return baseWidth + wings;
    }

    internal void SetClock(string text) => ClockText.Text = text;

    private void ApplyMetrics(double height)
    {
        _metricsHeight = height;
        _wing = height;
        _lyricPad = Math.Round(height * 0.4);

        double art = Math.Clamp(height - 10, 16, 30);
        double artRadius = Math.Round(art * 0.3);
        ArtFrame.Width = art;
        ArtFrame.Height = art;
        ArtFrame.Margin = new Thickness((_wing - art) / 2 + 1, 0, 0, 0);
        ArtPlaceholder.CornerRadius = new CornerRadius(artRadius);
        ArtRim.CornerRadius = new CornerRadius(artRadius);
        ArtPlaceholderGlyph.FontSize = Math.Round(art * 0.5);
        var clip = new RectangleGeometry(new Rect(0, 0, art, art), artRadius, artRadius);
        clip.Freeze();
        ArtImage.Clip = clip;

        BoltGlyph.Height = Math.Round(height * 0.47);
        BoltGlyph.Width = Math.Round(BoltGlyph.Height * 0.62);
        BoltGlyph.Margin = new Thickness((_wing - BoltGlyph.Width) / 2 + 1, 0, 0, 0);

        Equalizer.Width = Math.Round(height * 0.47);
        Equalizer.Height = Math.Round(height * 0.4);
        Equalizer.Margin = new Thickness(0, 0, (_wing - Equalizer.Width) / 2 + 1, 0);

        BatteryText.FontSize = Math.Clamp(Math.Round(height * 0.37 * 2) / 2, 10.5, 13);
        BatteryText.Margin = new Thickness(0, 0, Math.Round(height * 0.24), 0);

        ClockText.FontSize = Math.Clamp(Math.Round(height * 0.43), 12, 16);

        Eyes.Height = height;
        Eyes.Width = Math.Round(height * 3);

        double lyricSize = Math.Clamp(Math.Round(height * 0.42 * 2) / 2, 12, 13);
        LyricA.FontSize = lyricSize;
        LyricB.FontSize = lyricSize;
        _lyricText = null;
    }

    private void ShowLyric(string text, double baseWidth, bool animate)
    {
        bool wasShowing = LyricA.Visibility == Visibility.Visible || LyricB.Visibility == Visibility.Visible;
        if (text == _lyricText && wasShowing && _activeLyric.Opacity > 0)
        {
            Motion.FadeTo(_activeLyric, true, animate);
            return;
        }

        _lyricText = text;
        double maxText = Math.Max(40, Math.Max(MaxLyricWidth, baseWidth + 2 * _wing) - 2 * _wing - 2 * _lyricPad);
        _lyricWidth = Math.Min(Math.Ceiling(MeasureText(_activeLyric, text)) + 1, maxText);

        var outgoing = _activeLyric;
        var incoming = wasShowing ? (outgoing == LyricA ? LyricB : LyricA) : outgoing;
        _activeLyric = incoming;
        incoming.Text = text;
        incoming.Width = _lyricWidth;

        if (incoming != outgoing)
        {
            Motion.FadeTo(outgoing, false, animate);
            Slide(outgoing, -5, animate, Motion.Fade);
        }

        var shift = (TranslateTransform)incoming.RenderTransform;
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.Y = animate ? 5 : 0;
        if (incoming == outgoing)
        {
            incoming.BeginAnimation(OpacityProperty, null);
            incoming.Opacity = animate ? 0 : 1;
        }

        Motion.FadeTo(incoming, true, animate, LyricIn, TimeSpan.FromMilliseconds(animate && wasShowing ? 60 : 0));
        Slide(incoming, 0, animate, LyricIn);
    }

    private void HideLyrics(bool animate)
    {
        Motion.FadeTo(LyricA, false, animate);
        Motion.FadeTo(LyricB, false, animate);
        _lyricText = null;
    }

    private static void Slide(TextBlock block, double to, bool animate, TimeSpan duration) =>
        Motion.To((TranslateTransform)block.RenderTransform, TranslateTransform.YProperty, to, duration, Motion.Decelerate, animate);

    private void SetArt(ImageSource? source, bool animate)
    {
        _artSource = source;
        _hasArtSource = true;
        ArtImage.Source = source;
        ArtPlaceholderGlyph.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        Equalizer.BarBrush = ArtColor.Dominant(source) is { } color ? Paint.Solid(color) : EqualizerFallback;
        if (animate && source is not null)
        {
            ArtImage.BeginAnimation(OpacityProperty, null);
            ArtImage.Opacity = 0.2;
            Motion.To(ArtImage, OpacityProperty, 1, Motion.Fade, Motion.Decelerate, true);
        }
    }

    private static double MeasureText(TextBlock block, string text)
    {
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}
