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

    private static readonly IEasingFunction Rise = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 });
    private static readonly IEasingFunction Sink = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    /// <summary>0 = folded into the dock, 1 = fully open. Drives the content's scale, slide and fade and the window's cut.</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(1.0, (d, e) => ((GlassPanel)d).OnReveal((double)e.NewValue)));

    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _slide = new();
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
        Foreground = (Brush)Application.Current.FindResource("DockTextBrush");
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

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    private void OnReveal(double p)
    {
        p = Math.Clamp(p, 0, 1);
        if (Content is FrameworkElement root)
        {
            if (root.RenderTransform is not TransformGroup)
            {
                root.RenderTransformOrigin = new Point(0.5, 1);
                root.RenderTransform = new TransformGroup { Children = { _scale, _slide } };
            }

            double s = 0.9 + 0.1 * p;
            _scale.ScaleX = s;
            _scale.ScaleY = s;
            _slide.Y = (1 - p) * 16;
            root.Opacity = Math.Min(1, p * 1.8);
        }

        ApplyShape();
    }

    private void ApplyShape()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        double w = ActualWidth * toDevice.M11;
        double h = ActualHeight * toDevice.M22;
        double p = Math.Clamp(Reveal, 0, 1);
        double s = 0.9 + 0.1 * p;
        // The same scale (about the bottom centre) and slide as the content, so the blur follows the glass.
        double sw = w * s, sh = h * s;
        double top = h - sh + (1 - p) * 16 * toDevice.M22;
        var rect = new Rect((w - sw) / 2, Math.Min(top, h - 2), sw, Math.Max(2, Math.Min(sh, h - top)));
        Glass.Shape(hwnd, [rect], Radius * s * toDevice.M22);
    }

    /// <summary>
    /// Shows the panel with its bottom at <paramref name="bottom"/> (DIP): its left edge, right edge or centre at
    /// <paramref name="x"/>, kept on screen.
    /// </summary>
    public void ShowAt(double x, double bottom, HorizontalAlignment align, bool animate)
    {
        Left = -10000;
        Top = -10000;
        Reveal = animate ? 0 : 1;
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
        Top = Math.Round(bottom - ActualHeight);
        _shownAt = Environment.TickCount64;
        DockApps.Activate(new WindowInteropHelper(this).Handle);
        Activate();

        // Anchored from now on: content that arrives late (brightness, lists) makes the panel grow upwards.
        _bottom = bottom;
        if (animate)
        {
            BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = Rise });
        }
    }

    public void CloseSmooth()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (Content is UIElement && IsVisible)
        {
            var fold = new DoubleAnimation(Reveal, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = Sink };
            fold.Completed += (_, _) => Close();
            BeginAnimation(RevealProperty, fold);
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
