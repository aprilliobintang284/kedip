using System.Windows;
using System.Windows.Media;

namespace Kedip.Shell.Island;

/// <summary>A crisp, hand-drawn battery outline with a fill level; green while charging, red when low.</summary>
internal sealed class BatteryGlyph : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(100, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen Outline = CreatePen();
    private static readonly Brush Nub = Paint.Solid(0x73, 0xFF, 0xFF, 0xFF);
    private static readonly Brush Normal = Paint.Solid(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Brush Charging = Paint.Solid(0xFF, 0x34, 0xC7, 0x59);
    private static readonly Brush Low = Paint.Solid(0xFF, 0xFF, 0x45, 0x3A);

    public int Percent
    {
        get => (int)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(22, 11);

    protected override void OnRender(DrawingContext dc)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double half = 0.5 / scale;
        double bodyWidth = Motion.Snap(ActualWidth - 2.5, scale);
        double height = Motion.Snap(ActualHeight, scale);
        var body = new Rect(half, half, bodyWidth - 2 * half, height - 2 * half);
        dc.DrawRoundedRectangle(null, Outline, body, 3, 3);
        dc.DrawRoundedRectangle(Nub, null, new Rect(bodyWidth + 0.75, height * 0.32, 1.75, height * 0.36), 0.8, 0.8);

        double level = Math.Clamp(Percent, 0, 100) / 100.0;
        var inner = new Rect(2, 2, Math.Max(1.5, (bodyWidth - 4) * level), height - 4);
        var fill = IsCharging ? Charging : Percent <= 20 ? Low : Normal;
        dc.DrawRoundedRectangle(fill, null, inner, 1.5, 1.5);
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(Paint.Solid(0x66, 0xFF, 0xFF, 0xFF), 1);
        pen.Freeze();
        return pen;
    }
}
