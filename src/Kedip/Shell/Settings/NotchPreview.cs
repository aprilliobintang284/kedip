using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Kedip.Core;

namespace Kedip.Shell.Settings;

/// <summary>
/// Live 1:1 preview of the collapsed notch on a stylised screen edge. Size and position changes glide;
/// nothing animates while idle.
/// </summary>
public sealed class NotchPreview : FrameworkElement
{
    public static readonly DependencyProperty NotchWidthProperty = DependencyProperty.Register(
        nameof(NotchWidth), typeof(double), typeof(NotchPreview),
        new FrameworkPropertyMetadata(166.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchHeightProperty = DependencyProperty.Register(
        nameof(NotchHeight), typeof(double), typeof(NotchPreview),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchLeftProperty = DependencyProperty.Register(
        nameof(NotchLeft), typeof(double), typeof(NotchPreview),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double SideInset = 16;
    private const double CornerRadius = 6;

    private static readonly Brush Wallpaper = CreateWallpaper();
    private static readonly Brush Glow = CreateGlow();
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen EdgePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
        FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface ClockFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
        FontWeights.SemiBold, FontStretches.Normal);

    private double _targetWidth = 166;
    private double _targetHeight = 30;
    private NotchPosition _position = NotchPosition.Center;
    private IdleStyle _idleStyle = IdleStyle.Eyes;
    private bool _hasLayout;

    public NotchPreview()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    public double NotchWidth
    {
        get => (double)GetValue(NotchWidthProperty);
        set => SetValue(NotchWidthProperty, value);
    }

    public double NotchHeight
    {
        get => (double)GetValue(NotchHeightProperty);
        set => SetValue(NotchHeightProperty, value);
    }

    /// <summary>X of the notch body's left edge.</summary>
    public double NotchLeft
    {
        get => (double)GetValue(NotchLeftProperty);
        set => SetValue(NotchLeftProperty, value);
    }

    public void Show(double width, double height, NotchPosition position, IdleStyle idleStyle, bool animate)
    {
        animate &= _hasLayout && IsVisible;
        if (_idleStyle != idleStyle)
        {
            _idleStyle = idleStyle;
            InvalidateVisual();
        }

        if (_hasLayout && width.Equals(_targetWidth) && height.Equals(_targetHeight) && position == _position)
        {
            return;
        }

        _targetWidth = width;
        _targetHeight = height;
        _position = position;
        Motion.To(this, NotchWidthProperty, width, 320, animate);
        Motion.To(this, NotchHeightProperty, height, 320, animate);
        Motion.To(this, NotchLeftProperty, LeftFor(width), 360, animate);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _hasLayout = true;
        Motion.To(this, NotchWidthProperty, _targetWidth, 0, animate: false);
        Motion.To(this, NotchHeightProperty, _targetHeight, 0, animate: false);
        Motion.To(this, NotchLeftProperty, LeftFor(_targetWidth), 0, animate: false);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(bounds, CornerRadius, CornerRadius));
        dc.DrawRectangle(Wallpaper, null, bounds);
        dc.DrawRectangle(Glow, null, bounds);

        double w = NotchWidth;
        double h = NotchHeight;
        double left = NotchLeft;
        double ear = Math.Round(h * 0.3);
        dc.PushTransform(new TranslateTransform(left - ear, 0));
        dc.DrawGeometry(Brushes.Black, null, NotchShape.Create(w, h, ear));
        dc.Pop();

        double cx = left + w / 2;
        double cy = h * 0.48;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_idleStyle == IdleStyle.Eyes)
        {
            double rx = h * 0.1;
            double ry = h * 0.21;
            double gap = h * 0.3;
            dc.DrawEllipse(Brushes.White, null, new Point(cx - gap, cy), rx, ry);
            dc.DrawEllipse(Brushes.White, null, new Point(cx + gap, cy), rx, ry);
        }
        else
        {
            var clock = Text(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), ClockFace, Math.Max(10, h * 0.43),
                Brushes.White, dpi);
            dc.DrawText(clock, new Point(Math.Round(cx - clock.Width / 2), Math.Round(cy - clock.Height / 2)));
        }

        var label = Text(string.Create(CultureInfo.InvariantCulture, $"{_targetWidth:0} × {_targetHeight:0}"), LabelFace, 11,
            LabelBrush, dpi);
        dc.DrawText(label, new Point(Math.Round(bounds.Width - label.Width - 12), Math.Round(bounds.Height - label.Height - 8)));

        dc.Pop();
        dc.DrawRoundedRectangle(null, EdgePen, new Rect(0.5, 0.5, bounds.Width - 1, bounds.Height - 1), CornerRadius, CornerRadius);
    }

    private double LeftFor(double width)
    {
        double available = ActualWidth;
        return _position switch
        {
            NotchPosition.Left => SideInset,
            NotchPosition.Right => Math.Max(SideInset, available - width - SideInset),
            _ => Math.Round((available - width) / 2),
        };
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, null, TextFormattingMode.Display,
            pixelsPerDip);

    private static Brush CreateWallpaper()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x34, 0x3B, 0x5E), 0),
                new GradientStop(Color.FromRgb(0x22, 0x26, 0x3A), 0.55),
                new GradientStop(Color.FromRgb(0x17, 0x18, 0x20), 1),
            },
        };
        return Frozen(brush);
    }

    private static Brush CreateGlow()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.78, 1.1),
            GradientOrigin = new Point(0.78, 1.1),
            RadiusX = 0.6,
            RadiusY = 1.2,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x55, 0x5E, 0x8B, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x00, 0x5E, 0x8B, 0xFF), 1),
            },
        };
        return Frozen(brush);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
