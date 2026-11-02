using System.Windows;
using System.Windows.Media;

namespace Kedip.Mascot;

/// <summary>
/// Draws the mascot's two eyes, scaled to the control's height.
/// OWNER: B. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class EyesControl : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(EyesControl),
        new FrameworkPropertyMetadata(MascotMood.Awake, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LookXProperty = DependencyProperty.Register(
        nameof(LookX), typeof(double), typeof(EyesControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LookYProperty = DependencyProperty.Register(
        nameof(LookY), typeof(double), typeof(EyesControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When false, state changes snap instead of animating (blinks still happen, instantly).</summary>
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(EyesControl), new FrameworkPropertyMetadata(true));

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    public MascotMood Mood
    {
        get => (MascotMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public double LookX
    {
        get => (double)GetValue(LookXProperty);
        set => SetValue(LookXProperty, value);
    }

    public double LookY
    {
        get => (double)GetValue(LookYProperty);
        set => SetValue(LookYProperty, value);
    }

    /// <summary>Short happy "hello" animation when the user comes back.</summary>
    public void PlayWake()
    {
    }

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight * 0.42;
        double w = h * 0.68;
        double cy = ActualHeight / 2;
        double cx = ActualWidth / 2;
        dc.DrawEllipse(Brushes.White, null, new Point(cx - w * 1.1, cy), w / 2, h / 2);
        dc.DrawEllipse(Brushes.White, null, new Point(cx + w * 1.1, cy), w / 2, h / 2);
    }
}
