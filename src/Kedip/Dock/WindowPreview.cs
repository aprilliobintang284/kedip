using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kedip.Interop;
using static Kedip.DockBar.WinKeys;

namespace Kedip.DockBar;

/// <summary>
/// Live previews of an app's windows above its dock tile (like the taskbar's): each window as a card with its title,
/// a close button and a thumbnail the desktop compositor keeps up to date. Click a card to bring that window forward.
/// Never takes focus.
/// </summary>
internal sealed class WindowPreview : Window
{
    private const double ThumbWidth = 208;
    private const double ThumbHeight = 124;
    private const double Radius = 18;

    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
    private readonly List<(IntPtr Thumb, Border Area)> _thumbs = [];
    private IntPtr _hwnd;
    private double _centerX;
    private double _bottom;

    public WindowPreview()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Title = "Pratinjau jendela";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = (Brush)Application.Current.FindResource("DockTextBrush");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Kedip;component/Dock/DockTheme.xaml", UriKind.Relative) });
        DockAccent.Apply(Resources);
        Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("PanelSurface"),
            BorderBrush = (Brush)Application.Current.FindResource("DockEdge"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Child = _cards,
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Glass.Apply(_hwnd, noActivate: true);
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        };
        SizeChanged += (_, _) =>
        {
            Place();
            Shape();
            UpdateThumbnails();
        };
    }

    /// <summary>Which app is shown (by dock key), or null while hidden.</summary>
    public string? AppKey { get; private set; }

    /// <summary>Shows <paramref name="app"/>'s windows centred on <paramref name="centerX"/>, bottom at <paramref name="bottom"/> (DIP).</summary>
    public void ShowFor(DockApp app, double centerX, double bottom, bool animate)
    {
        bool wasVisible = IsVisible;
        new WindowInteropHelper(this).EnsureHandle(); // thumbnails need the window handle before the cards are built
        AppKey = app.Key;
        _centerX = centerX;
        _bottom = bottom;
        Fill(app);
        if (!wasVisible)
        {
            Left = -10000;
            Top = -10000;
            Show();
        }

        UpdateLayout();
        Place();
        Shape();
        UpdateThumbnails();
        if (!wasVisible && animate && Content is UIElement content)
        {
            content.Opacity = 0;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }
    }

    public void HideNow()
    {
        AppKey = null;
        ClearThumbnails();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbnails();
        base.OnClosed(e);
    }

    // ---- Cards -------------------------------------------------------------------------------------------------------

    private void Fill(DockApp app)
    {
        ClearThumbnails();
        _cards.Children.Clear();
        foreach (IntPtr window in app.Windows)
        {
            _cards.Children.Add(Card(app, window));
        }
    }

    private Border Card(DockApp app, IntPtr window)
    {
        var title = new TextBlock
        {
            Text = DockApps.WindowTitle(window),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Style = (Style)Resources["RoundIconButton"],
            Width = 24,
            Height = 24,
            FontSize = 10,
            Content = Glyph(0xE711),
            ToolTip = "Tutup jendela",
            Visibility = Visibility.Hidden,
        };
        AutomationProperties.SetName(close, "Tutup " + title.Text);
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        if (app.Icon is { } icon)
        {
            var image = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(image, Dock.Left);
            head.Children.Add(image);
        }

        head.Children.Add(title);

        // The compositor draws the live thumbnail over this area; the app icon shows through for minimized windows.
        var area = new Border
        {
            Width = ThumbWidth,
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(8),
            Background = DockPalette.Tint(0x0D),
            Child = app.Icon is { } big ? new Image { Source = big, Width = 40, Height = 40, Opacity = 0.7 } : null,
        };
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(area);

        var card = new Border
        {
            Child = body,
            Width = ThumbWidth + 16,
            Padding = new Thickness(8, 6, 8, 8),
            CornerRadius = new CornerRadius(13),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetName(card, title.Text);
        card.MouseEnter += (_, _) =>
        {
            card.Background = (Brush)Application.Current.FindResource("DockHover");
            close.Visibility = Visibility.Visible;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = Brushes.Transparent;
            close.Visibility = Visibility.Hidden;
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside(source, close))
            {
                return;
            }

            HideNow();
            DockApps.Activate(window);
        };
        close.Click += (_, _) =>
        {
            DockApps.CloseWindow(window);
            app.Windows.Remove(window);
            if (app.Windows.Count == 0)
            {
                HideNow();
            }
            else
            {
                ShowFor(app, _centerX, _bottom, animate: false);
            }
        };

        _thumbs.Add((Register(window), area));
        return card;
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Placement -----------------------------------------------------------------------------------------------------

    private void Place()
    {
        if (ActualWidth <= 0 || AppKey is null)
        {
            return;
        }

        double screen = SystemParameters.PrimaryScreenWidth;
        Left = Math.Round(Math.Clamp(_centerX - ActualWidth / 2, 8, screen - ActualWidth - 8));
        Top = Math.Round(_bottom - ActualHeight);
    }

    private void Shape()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        Glass.Shape(_hwnd, [new Rect(0, 0, ActualWidth * toDevice.M11, ActualHeight * toDevice.M22)], Radius * toDevice.M22);
    }

    // ---- Thumbnails (desktop window manager) -----------------------------------------------------------------------

    private IntPtr Register(IntPtr source) =>
        _hwnd != IntPtr.Zero && DwmRegisterThumbnail(_hwnd, source, out IntPtr thumb) == 0 ? thumb : IntPtr.Zero;

    /// <summary>Fits each live thumbnail into its card's area (device pixels), keeping the window's proportions.</summary>
    private void UpdateThumbnails()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        for (int i = 0; i < _thumbs.Count; i++)
        {
            var (thumb, area) = _thumbs[i];
            if (thumb == IntPtr.Zero || !area.IsLoaded)
            {
                continue;
            }

            var topLeft = area.TransformToAncestor(this).Transform(new Point(0, 0));
            double x = topLeft.X * toDevice.M11;
            double y = topLeft.Y * toDevice.M22;
            double width = area.ActualWidth * toDevice.M11;
            double height = area.ActualHeight * toDevice.M22;
            if (DwmQueryThumbnailSourceSize(thumb, out SIZE size) == 0 && size.cx > 0 && size.cy > 0)
            {
                double scale = Math.Min(width / size.cx, height / size.cy);
                double w = size.cx * scale;
                double h = size.cy * scale;
                x += (width - w) / 2;
                y += (height - h) / 2;
                width = w;
                height = h;
            }

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(x),
                    Top = (int)Math.Round(y),
                    Right = (int)Math.Round(x + width),
                    Bottom = (int)Math.Round(y + height),
                },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    private void ClearThumbnails()
    {
        foreach (var (thumb, _) in _thumbs)
        {
            if (thumb != IntPtr.Zero)
            {
                DwmUnregisterThumbnail(thumb);
            }
        }

        _thumbs.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int DWM_TNP_RECTDESTINATION = 0x1;
    private const int DWM_TNP_OPACITY = 0x4;
    private const int DWM_TNP_VISIBLE = 0x8;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public NativeMethods.RECT rcDestination;
        public NativeMethods.RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);
}
