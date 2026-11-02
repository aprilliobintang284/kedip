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
    /// <summary>Corner radius of every flyout (DIP); the root border of each panel uses the same value.</summary>
    public const double Radius = 20;

    private static readonly IEasingFunction Rise = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private bool _closing;
    private long _shownAt;
    private double _bottom = double.NaN;

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
        DockAccent.Apply(Resources);

        SourceInitialized += (_, _) =>
        {
            Glass.Apply(new WindowInteropHelper(this).Handle, noActivate: false);
            ApplyShape();
        };
        Deactivated += (_, _) =>
        {
            // Windows sometimes bounces the activation back to the previous app right after the panel opens;
            // only a real click elsewhere (later) closes it.
            if (Environment.TickCount64 - _shownAt > 400)
            {
                CloseSmooth();
            }
        };
        SizeChanged += (_, e) =>
        {
            ApplyShape();
            // Pages of different heights: grow upwards, keeping the bottom edge above the dock.
            if (e.HeightChanged && !double.IsNaN(_bottom) && !_closing)
            {
                BeginAnimation(TopProperty, null);
                Top = Math.Round(_bottom - ActualHeight);
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

    private void ApplyShape()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var size = new Rect(0, 0, ActualWidth * toDevice.M11, ActualHeight * toDevice.M22);
        Glass.Shape(hwnd, [size], Radius * toDevice.M22);
    }

    /// <summary>
    /// Shows the panel with its bottom at <paramref name="bottom"/> (DIP): its left edge, right edge or centre at
    /// <paramref name="x"/>, kept on screen.
    /// </summary>
    public void ShowAt(double x, double bottom, HorizontalAlignment align, bool animate)
    {
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        double screen = SystemParameters.PrimaryScreenWidth;
        double left = align switch
        {
            HorizontalAlignment.Right => x - ActualWidth,
            HorizontalAlignment.Center => x - ActualWidth / 2,
            _ => x,
        };
        Left = Math.Round(Math.Clamp(left, 8, screen - ActualWidth - 8));
        double top = Math.Round(bottom - ActualHeight);
        _shownAt = Environment.TickCount64;
        DockApps.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        if (Content is UIElement content && animate)
        {
            content.Opacity = 0;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        // Anchored from now on: content that arrives late (brightness, lists) makes the panel grow upwards.
        _bottom = bottom;
        BeginAnimation(TopProperty, new DoubleAnimation(top + 14, top, animate ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero)
        {
            EasingFunction = Rise,
        });
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
