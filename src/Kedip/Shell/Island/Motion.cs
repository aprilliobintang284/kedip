using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Kedip.Shell.Island;

/// <summary>Shared easing curves and small animation helpers for the island.</summary>
internal static class Motion
{
    public static readonly TimeSpan Grow = TimeSpan.FromMilliseconds(340);
    public static readonly TimeSpan Shrink = TimeSpan.FromMilliseconds(260);
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(120);

    /// <summary>Damped spring with ≈ 4 % overshoot; used when the island grows.</summary>
    public static readonly IEasingFunction Spring = new SpringEase(damping: 0.72, stiffness: 8.6);

    /// <summary>Strong deceleration without overshoot; used when the island shrinks and for content.</summary>
    public static readonly IEasingFunction Decelerate = Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Gentle deceleration for small, frequent changes (lyric width, indicator slides).</summary>
    public static readonly IEasingFunction Soft = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>
    /// Animates <paramref name="property"/> to <paramref name="to"/> and leaves <paramref name="to"/> as the base value,
    /// so no clock stays alive once the animation is over.
    /// </summary>
    public static void To(
        IAnimatable target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction? easing, bool animate,
        TimeSpan? delay = null, EventHandler? completed = null)
    {
        var element = (DependencyObject)target;
        double from = (double)element.GetValue(property);
        element.SetValue(property, to);
        if (!animate || duration <= TimeSpan.Zero || Math.Abs(from - to) < 0.001)
        {
            target.BeginAnimation(property, null);
            completed?.Invoke(target, EventArgs.Empty);
            return;
        }

        AnimationTimeline animation;
        if (delay is { } wait && wait > TimeSpan.Zero)
        {
            // Hold the start value during the delay (a BeginTime would briefly show the new base value).
            var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(wait)));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(wait + duration), easing));
            animation = frames;
        }
        else
        {
            animation = new DoubleAnimation(from, to, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        }

        if (completed is not null)
        {
            animation.Completed += completed;
        }
        else
        {
            animation.Freeze();
        }

        target.BeginAnimation(property, animation);
    }

    /// <summary>Fades an element in or out; a faded-out element is collapsed so it neither renders nor hit-tests.</summary>
    public static void FadeTo(UIElement element, bool visible, bool animate, TimeSpan? duration = null, TimeSpan? delay = null)
    {
        double target = visible ? 1 : 0;
        double baseValue = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        if (visible)
        {
            if (element.Visibility == Visibility.Visible && baseValue >= 0.999)
            {
                // Already shown or fading in: don't restart (repeated refreshes would keep postponing a delayed fade).
                return;
            }

            element.Visibility = Visibility.Visible;
        }
        else
        {
            if (element.Visibility != Visibility.Visible)
            {
                return;
            }

            if (baseValue <= 0.001)
            {
                if (element.Opacity <= 0.001)
                {
                    element.Visibility = Visibility.Collapsed;
                }

                return;
            }
        }

        To(element, UIElement.OpacityProperty, target, duration ?? Fade, Decelerate, animate, visible ? delay : null, visible ? null : (_, _) =>
        {
            if ((double)element.GetAnimationBaseValue(UIElement.OpacityProperty) == 0)
            {
                element.Visibility = Visibility.Collapsed;
            }
        });
    }

    /// <summary>Snaps a DIP length to whole device pixels.</summary>
    public static double Snap(double value, double scale) => scale > 0 ? Math.Round(value * scale) / scale : value;

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Critically-underdamped spring normalised to reach exactly 1 at t = 1.</summary>
    private sealed class SpringEase(double damping, double stiffness) : IEasingFunction
    {
        private readonly double _end = Raw(1, damping, stiffness);

        public double Ease(double normalizedTime)
        {
            double t = Math.Clamp(normalizedTime, 0, 1);
            return Raw(t, damping, stiffness) + (1 - _end) * t;
        }

        private static double Raw(double t, double zeta, double omega)
        {
            double wd = omega * Math.Sqrt(1 - zeta * zeta);
            double envelope = Math.Exp(-zeta * omega * t);
            return 1 - envelope * (Math.Cos(wd * t) + zeta * omega / wd * Math.Sin(wd * t));
        }
    }
}

/// <summary>Frozen brush helpers.</summary>
internal static class Paint
{
    private static readonly Dictionary<string, Brush> HexCache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Parses "#RRGGBB"/"#AARRGGBB" (cached); <paramref name="fallback"/> when missing or invalid.</summary>
    public static Brush FromHex(string? hex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        if (HexCache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        Brush brush = fallback;
        try
        {
            if (ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex) is Color color)
            {
                brush = Solid(color);
            }
        }
        catch (FormatException)
        {
        }

        if (HexCache.Count < 64)
        {
            HexCache[hex] = brush;
        }

        return brush;
    }

    public static Brush Resource(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
