using System.Windows;
using System.Windows.Media.Animation;

namespace Kedip.Shell.Settings;

/// <summary>Short, eased one-shot animations for the settings UI. Respects the Windows "animation effects" setting.</summary>
internal static class Motion
{
    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Animates <paramref name="property"/> to <paramref name="to"/>, or jumps there when motion is off.</summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, int milliseconds, bool animate = true)
    {
        if (!animate || !Enabled)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }

        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = EaseOut };
        animation.Freeze();
        target.BeginAnimation(property, animation);
    }

    /// <summary>Animates from <paramref name="from"/> to <paramref name="to"/>, releasing the animation when it ends.</summary>
    public static void FromTo(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var owner = (DependencyObject)target;
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            owner.SetValue(property, to);
            return;
        }

        owner.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        };
        animation.Freeze();
        target.BeginAnimation(property, animation);
    }

    private static IEasingFunction Freeze(CubicEase ease)
    {
        ease.Freeze();
        return ease;
    }
}
