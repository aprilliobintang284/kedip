using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kedip.Core;

namespace Kedip.DockBar;

/// <summary>
/// A glass flyout that rises above the dock. Takes focus while open (for search/keyboard) and closes itself when
/// focus leaves it or on Esc.
/// </summary>
public class GlassPanel : Window
{
    private static readonly IEasingFunction Rise = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private bool _closing;
    private long _shownAt;

    public GlassPanel()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Kedip;component/Dock/DockTheme.xaml", UriKind.Relative) });

        SourceInitialized += (_, _) => Glass.Apply(new WindowInteropHelper(this).Handle, noActivate: false);
        Deactivated += (_, _) =>
        {
            // Windows sometimes bounces the activation back to the previous app right after the panel opens;
            // only a real click elsewhere (later) closes it.
            if (Environment.TickCount64 - _shownAt > 400)
            {
                CloseSmooth();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseSmooth();
            }
        };
    }

    public bool IsClosing => _closing;

    /// <summary>Shows the panel with its bottom at <paramref name="bottom"/> (DIP), left- or right-aligned to <paramref name="x"/>.</summary>
    public void ShowAt(double x, double bottom, bool alignRight, bool animate)
    {
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        double screen = SystemParameters.PrimaryScreenWidth;
        Left = Math.Round(Math.Clamp(alignRight ? x - ActualWidth : x, 8, screen - ActualWidth - 8));
        double top = Math.Round(bottom - ActualHeight);
        _shownAt = Environment.TickCount64;
        DockApps.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        if (Content is UIElement content && animate)
        {
            content.Opacity = 0;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        BeginAnimation(TopProperty, new DoubleAnimation(top + 14, top, animate ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero) { EasingFunction = Rise });
    }

    public void CloseSmooth()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (Content is UIElement content)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (_, _) => Close();
            content.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        MemoryTrimmer.Request(TimeSpan.FromSeconds(3));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
