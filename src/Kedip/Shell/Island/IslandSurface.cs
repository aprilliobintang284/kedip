using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kedip.Core;

namespace Kedip.Shell.Island;

/// <summary>
/// Paints the black island (via <see cref="NotchShape"/>) and hosts its content layers. The body size is animatable;
/// geometry is rebuilt only when the snapped size changes. Layers are either stretched over the current body
/// (<see cref="StretchProperty"/>) or kept at their own size, centred at the top and revealed by the island clip.
/// </summary>
public sealed class IslandSurface : Panel
{
    public static readonly DependencyProperty BodyWidthProperty = DependencyProperty.Register(
        nameof(BodyWidth), typeof(double), typeof(IslandSurface),
        new FrameworkPropertyMetadata(166.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BodyHeightProperty = DependencyProperty.Register(
        nameof(BodyHeight), typeof(double), typeof(IslandSurface),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EarRadiusProperty = DependencyProperty.Register(
        nameof(EarRadius), typeof(double), typeof(IslandSurface),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnchorProperty = DependencyProperty.Register(
        nameof(Anchor), typeof(NotchPosition), typeof(IslandSurface),
        new FrameworkPropertyMetadata(NotchPosition.Center, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(IslandSurface),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Attached: true = the layer is laid out over the current (animated) body rectangle.</summary>
    public static readonly DependencyProperty StretchProperty = DependencyProperty.RegisterAttached(
        "Stretch", typeof(bool), typeof(IslandSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private Geometry? _geometry;
    private Rect _geometryKey = Rect.Empty;
    private double _geometryEar = -1;
    private Rect _body;

    public double BodyWidth
    {
        get => (double)GetValue(BodyWidthProperty);
        set => SetValue(BodyWidthProperty, value);
    }

    public double BodyHeight
    {
        get => (double)GetValue(BodyHeightProperty);
        set => SetValue(BodyHeightProperty, value);
    }

    public double EarRadius
    {
        get => (double)GetValue(EarRadiusProperty);
        set => SetValue(EarRadiusProperty, value);
    }

    public NotchPosition Anchor
    {
        get => (NotchPosition)GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static bool GetStretch(DependencyObject element) => (bool)element.GetValue(StretchProperty);

    public static void SetStretch(DependencyObject element, bool value) => element.SetValue(StretchProperty, value);

    /// <summary>Animates the body to a new size (spring when growing, deceleration when shrinking); snaps when not animating.</summary>
    public void ResizeTo(double width, double height, bool animate, bool calm = false)
    {
        Resize(BodyWidthProperty, Math.Round(width), animate, calm);
        Resize(BodyHeightProperty, Math.Round(height), animate, calm);
    }

    /// <summary>True when <paramref name="point"/> (panel coordinates) lies on the painted island.</summary>
    public bool HitsIsland(Point point) => _geometry?.FillContains(point) == true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var body = ComputeBody(availableSize);
        foreach (UIElement child in InternalChildren)
        {
            if (GetStretch(child))
            {
                child.Measure(body.Size);
            }
            else
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _body = ComputeBody(finalSize);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (UIElement child in InternalChildren)
        {
            if (GetStretch(child))
            {
                child.Arrange(_body);
            }
            else
            {
                var size = child.DesiredSize;
                double x = Motion.Snap(_body.X + (_body.Width - size.Width) / 2, scale);
                child.Arrange(new Rect(new Point(x, 0), size));
            }
        }

        UpdateGeometry();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        UpdateGeometry();
        if (_geometry is not null)
        {
            dc.DrawGeometry(Fill, null, _geometry);
        }
    }

    private Rect ComputeBody(Size available)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double ear = EarRadius;
        double width = Motion.Snap(Math.Max(0, BodyWidth), scale);
        double height = Motion.Snap(Math.Max(0, BodyHeight), scale);
        double total = double.IsInfinity(available.Width) ? width + 2 * ear : available.Width;
        double x = Anchor switch
        {
            NotchPosition.Left => ear,
            NotchPosition.Right => total - ear - width,
            _ => (total - width) / 2,
        };
        return new Rect(Motion.Snap(x, scale), 0, width, height);
    }

    private void UpdateGeometry()
    {
        var body = _body;
        double ear = EarRadius;
        if (body == _geometryKey && ear == _geometryEar && _geometry is not null)
        {
            return;
        }

        _geometryKey = body;
        _geometryEar = ear;
        if (body.Width <= 0 || body.Height <= 0)
        {
            _geometry = null;
            Clip = Geometry.Empty;
            return;
        }

        var shape = NotchShape.Create(body.Width, body.Height, ear).CloneCurrentValue();
        shape.Transform = new TranslateTransform(body.X - ear, 0);
        shape.Freeze();
        _geometry = shape;
        Clip = shape;
    }

    private void Resize(DependencyProperty property, double to, bool animate, bool calm)
    {
        double target = (double)GetAnimationBaseValue(property);
        if (Math.Abs(target - to) < 0.01)
        {
            return;
        }

        double current = (double)GetValue(property);
        bool growing = to > current;
        var duration = calm ? TimeSpan.FromMilliseconds(220) : growing ? Motion.Grow : Motion.Shrink;
        var easing = calm ? Motion.Soft : growing ? Motion.Spring : Motion.Decelerate;
        Motion.To(this, property, to, duration, easing, animate);
    }
}
