using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Interop;
using Kedip.Power;
using Kedip.Productivity;

namespace Kedip.DockBar;

/// <summary>
/// The dock: a glass bar at the bottom centre with pinned/running apps, widgets and status icons. In smart mode it
/// slides down (leaving a thin handle) while the foreground window covers it; touching the handle brings it back.
/// Never takes focus. Live data is only sampled while the dock is visible.
/// </summary>
public partial class DockWindow : Window
{
    private const double BottomGap = 10;
    private const double HandleHeight = 5;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(240);
    private static readonly IEasingFunction SlideEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly Brush DimDot = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DndOn = Frozen(new SolidColorBrush(Color.FromRgb(0xBF, 0x9C, 0xFF)));

    private readonly AppServices _services;
    private readonly DockApps _apps = new();
    private readonly NetworkInfo _network = new();
    private readonly SystemVolume _volume = new();
    private readonly WeatherInfo _weather = new();
    private readonly DispatcherTimer _checkTimer;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _clockTimer;
    private IntPtr _hwnd;
    private bool _tucked;
    private bool _away;
    private bool _menuOpen;
    private string _appsSignature = "";

    public DockWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        SeedPins();

        _checkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _checkTimer.Tick += (_, _) => Evaluate();
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += (_, _) => LiveTick();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
        _clockTimer.Tick += (_, _) => UpdateClock();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => Place(animate: false);
        MouseEnter += (_, _) => Evaluate();
        Closed += OnClosed;

        StartButton.Click += (_, _) => Keys.Win();
        StartButton.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Keys.Win(Keys.X);
        };
        TimerButton.Click += (_, _) => ToggleTimer();
        NetworkButton.Click += (_, _) => Keys.Win(Keys.A);
        VolumeButton.Click += (_, _) => Keys.Win(Keys.A);
        BatteryButton.Click += (_, _) => Keys.Win(Keys.A);
        DndButton.Click += (_, _) => _services.Settings.Update(s => s.DoNotDisturb = !s.DoNotDisturb);
        ClockButton.Click += (_, _) => Keys.Win(Keys.N);
        DesktopButton.Click += (_, _) => Keys.Win(Keys.D);

        _services.Settings.Changed += OnSettingsChanged;
        _services.Foreground.Changed += OnForegroundChanged;
        _services.Power.Changed += OnPowerChanged;
        _services.Pomodoro.Changed += OnPomodoroChanged;
        _weather.Updated += OnWeatherUpdated;
    }

    private bool Animations => _services.Settings.Current.Animations;

    private static double ScreenWidth => SystemParameters.PrimaryScreenWidth;

    private static double ScreenHeight => SystemParameters.PrimaryScreenHeight;

    private double ShownTop => ScreenHeight - ActualHeight - BottomGap;

    // ---- Setup -------------------------------------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));

        // Windows 11 acrylic behind the whole client area, dark, with system-rounded corners and a soft border.
        int on = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        int corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        int border = 0x00403B38; // COLORREF (BGR): a dim warm grey edge
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWidgetVisibility();
        UpdateClock();
        UpdateTimer();
        UpdateBattery();
        UpdateDnd();
        RefreshWeather();
        _appsSignature = "";
        LiveTick();
        Place(animate: false);
        _checkTimer.Start();
        Evaluate();
        UpdateLiveTimer();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _checkTimer.Stop();
        _liveTimer.Stop();
        _clockTimer.Stop();
        _services.Settings.Changed -= OnSettingsChanged;
        _services.Foreground.Changed -= OnForegroundChanged;
        _services.Power.Changed -= OnPowerChanged;
        _services.Pomodoro.Changed -= OnPomodoroChanged;
        _weather.Updated -= OnWeatherUpdated;
        _volume.Dispose();
    }

    private void SeedPins()
    {
        if (!_services.Settings.Current.DockPinnedSeeded)
        {
            _services.Settings.Update(s =>
            {
                if (s.DockPinned.Count == 0)
                {
                    s.DockPinned.AddRange(DockApps.DefaultPins);
                }

                s.DockPinnedSeeded = true;
            });
        }
    }

    // ---- Placement & smart hide --------------------------------------------------------------------------------

    private void Place(bool animate)
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        Left = Math.Round((ScreenWidth - ActualWidth) / 2);
        double top = _away ? ScreenHeight + 4 : _tucked ? ScreenHeight - HandleHeight : ShownTop;
        var slide = new DoubleAnimation(top, animate && Animations ? SlideTime : TimeSpan.Zero) { EasingFunction = SlideEase };
        BeginAnimation(TopProperty, slide);
    }

    private void Evaluate()
    {
        var s = _services.Settings.Current;
        bool away = s.HideInFullscreen && _services.Foreground.IsFullscreenAppForeground;
        bool tucked = !away && s.DockAutoHide == DockHide.Smart && !IsMouseOver && !_menuOpen && ForegroundCovers();
        if (away == _away && tucked == _tucked)
        {
            return;
        }

        bool wasVisible = !_tucked && !_away;
        _away = away;
        _tucked = tucked;
        Place(animate: true);
        UpdateLiveTimer();
        if (!wasVisible && !_tucked && !_away)
        {
            LiveTick();
        }
    }

    /// <summary>True when the foreground app window overlaps where the dock sits when shown.</summary>
    private bool ForegroundCovers()
    {
        if (_services.Foreground.IsDesktopForeground)
        {
            return false;
        }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || NativeMethods.IsIconic(fg))
        {
            return false;
        }

        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT r, Marshal.SizeOf<NativeMethods.RECT>()) != 0
            && !NativeMethods.GetWindowRect(fg, out r))
        {
            return false;
        }

        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        double left = Left * toDevice.M11;
        double top = ShownTop * toDevice.M22;
        double right = left + ActualWidth * toDevice.M11;
        double bottom = top + ActualHeight * toDevice.M22;
        return r.Left < right && r.Right > left && r.Top < bottom && r.Bottom > top;
    }

    private void UpdateLiveTimer()
    {
        if (!_tucked && !_away)
        {
            _liveTimer.Start();
        }
        else
        {
            _liveTimer.Stop();
        }
    }

    // ---- Live data ----------------------------------------------------------------------------------------------

    private void LiveTick()
    {
        RefreshApps();
        RefreshNetworkAndVolume();
    }

    private void RefreshApps()
    {
        var list = _apps.Build(_services.Settings.Current.DockPinned);
        string signature = string.Join("|", list.Select(a => $"{a.Key}:{a.Windows.Count}:{a.IsActive}"));
        if (signature == _appsSignature)
        {
            return;
        }

        _appsSignature = signature;
        PinnedPanel.Children.Clear();
        RunningPanel.Children.Clear();
        foreach (var app in list)
        {
            (app.IsPinned ? PinnedPanel : RunningPanel).Children.Add(CreateTile(app));
        }

        RunningDivider.Visibility = RunningPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button CreateTile(DockApp app)
    {
        UIElement face;
        if (app.Icon is { } icon)
        {
            var image = new Image { Width = 34, Height = 34, Source = icon, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            face = image;
        }
        else
        {
            face = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = (Brush)FindResource("SurfaceBrush"),
                Child = new TextBlock
                {
                    Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        var dot = new Border
        {
            Height = 3.5,
            Width = app.IsActive ? 14 : 4,
            CornerRadius = new CornerRadius(1.75),
            Background = app.IsActive ? Brushes.White : DimDot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
            Visibility = app.IsRunning ? Visibility.Visible : Visibility.Hidden,
        };
        var body = new Grid { Width = 40, Height = 50 };
        ((FrameworkElement)face).VerticalAlignment = VerticalAlignment.Center;
        ((FrameworkElement)face).Margin = new Thickness(0, 0, 0, 3);
        body.Children.Add(face);
        body.Children.Add(dot);

        var tile = new Button
        {
            Style = (Style)FindResource("DockButton"),
            Content = body,
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            ToolTip = app.Name,
        };
        tile.Click += (_, _) =>
        {
            DockApps.Click(app);
            _appsSignature = "";
        };
        tile.ContextMenu = BuildAppMenu(app);
        return tile;
    }

    private ContextMenu BuildAppMenu(DockApp app)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            Evaluate();
        };

        menu.Items.Add(MenuEntry(app.IsRunning ? "Buka jendela baru" : "Buka", "\uE8A7", () => DockApps.Launch(app)));
        menu.Items.Add(MenuEntry(app.IsPinned ? "Lepas dari dock" : "Sematkan di dock", app.IsPinned ? "\uE77A" : "\uE718", () => TogglePin(app)));
        if (app.IsRunning)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(app.Windows.Count > 1 ? "Tutup semua jendela" : "Tutup jendela", "\uE711", () => DockApps.CloseWindows(app)));
        }

        return menu;
    }

    private static MenuItem MenuEntry(string text, string glyph, Action action)
    {
        var item = new MenuItem { Header = text, Tag = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private void TogglePin(DockApp app)
    {
        _services.Settings.Update(s =>
        {
            s.DockPinned.RemoveAll(k => string.Equals(k, app.Key, StringComparison.OrdinalIgnoreCase));
            if (!app.IsPinned)
            {
                s.DockPinned.Add(app.Key);
            }
        });
    }

    private void RefreshNetworkAndVolume()
    {
        _network.Sample();
        if (_services.Settings.Current.DockNetSpeed)
        {
            DownText.Text = "↓ " + NetworkInfo.Format(_network.DownBytesPerSecond);
            UpText.Text = "↑ " + NetworkInfo.Format(_network.UpBytesPerSecond);
        }

        NetworkGlyph.Text = _network.Connection == ConnectionKind.Ethernet ? "\uE839" : "\uE701";
        NetworkGlyph.Opacity = _network.Connection == ConnectionKind.None ? 0.35 : 1;
        NetworkButton.ToolTip = _network.Connection switch
        {
            ConnectionKind.Wifi => "Wi-Fi tersambung",
            ConnectionKind.Ethernet => "Ethernet tersambung",
            _ => "Tidak ada koneksi",
        };

        if (_volume.Read() is { } volume)
        {
            VolumeGlyph.Text = volume.Muted || volume.Level < 0.005 ? "\uE74F"
                : volume.Level < 0.34 ? "\uE993"
                : volume.Level < 0.67 ? "\uE994"
                : "\uE995";
            VolumeButton.ToolTip = volume.Muted ? "Suara dibisukan" : $"Volume {Math.Round(volume.Level * 100)}%";
        }
        else
        {
            VolumeGlyph.Text = "\uE767";
        }
    }

    private void UpdateBattery()
    {
        var power = _services.Power;
        BatteryButton.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        int level = Math.Clamp(power.Percent / 10, 0, 10);
        BatteryGlyph.Text = power.IsCharging ? char.ConvertFromUtf32(0xEBAB + level) : PowerService.BatteryGlyph(power.Percent);
        BatteryButton.ToolTip = $"Baterai {power.Percent}%" + (power.IsCharging ? " · mengisi daya" : "");
    }

    private void UpdateDnd()
    {
        bool on = _services.Settings.Current.DoNotDisturb;
        DndGlyph.Text = "\uE708";
        DndGlyph.Foreground = on ? DndOn : (Brush)FindResource("TextPrimaryBrush");
        DndGlyph.Opacity = on ? 1 : 0.55;
        DndButton.ToolTip = on ? "Jangan ganggu: nyala (klik untuk mematikan)" : "Jangan ganggu: mati (klik untuk menyalakan)";
    }

    private void UpdateTimer()
    {
        var pomodoro = _services.Pomodoro;
        bool active = pomodoro.Phase != PomodoroPhase.Idle;
        TimerText.Text = active ? $"{(int)pomodoro.Remaining.TotalMinutes:00}:{pomodoro.Remaining.Seconds:00}" : "--:--";
        TimerCaption.Text = pomodoro.Phase switch
        {
            PomodoroPhase.Focus => pomodoro.IsRunning ? "fokus" : "dijeda",
            PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak => pomodoro.IsRunning ? "istirahat" : "dijeda",
            _ => "timer",
        };
    }

    private void ToggleTimer()
    {
        var pomodoro = _services.Pomodoro;
        if (pomodoro.IsRunning)
        {
            pomodoro.Pause();
        }
        else
        {
            pomodoro.StartOrResume();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        DateText.Text = now.ToString("d/M/yyyy", CultureInfo.InvariantCulture);
        ClockButton.ToolTip = now.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("id-ID"));
        _clockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) + TimeSpan.FromMilliseconds(50);
        _clockTimer.Start();
        if (now.Minute % 10 == 0)
        {
            RefreshWeather();
        }
    }

    private void RefreshWeather()
    {
        var s = _services.Settings.Current;
        if (s.DockWeather)
        {
            _weather.RefreshIfStale(s.Latitude, s.Longitude);
        }
    }

    private void ApplyWidgetVisibility()
    {
        var s = _services.Settings.Current;
        TimerButton.Visibility = s.DockTimer ? Visibility.Visible : Visibility.Collapsed;
        NetSpeedPanel.Visibility = s.DockNetSpeed ? Visibility.Visible : Visibility.Collapsed;
        WeatherPanel.Visibility = s.DockWeather && _weather.Temperature is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Events ------------------------------------------------------------------------------------------------

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyWidgetVisibility();
        UpdateDnd();
        RefreshWeather();
        _appsSignature = "";
        if (!_tucked && !_away)
        {
            RefreshApps();
        }

        Evaluate();
    }

    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        Evaluate();
        if (!_tucked && !_away)
        {
            RefreshApps();
        }
    }

    private void OnPowerChanged(object? sender, EventArgs e) => UpdateBattery();

    private void OnPomodoroChanged(object? sender, EventArgs e) => UpdateTimer();

    private void OnWeatherUpdated(object? sender, EventArgs e)
    {
        WeatherGlyph.Text = _weather.Glyph;
        WeatherText.Text = _weather.Temperature is { } t ? $"{Math.Round(t)}°" : "";
        WeatherPanel.ToolTip = $"Cuaca {_services.Settings.Current.PrayerCity}";
        ApplyWidgetVisibility();
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

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Windows-key shortcuts for the system surfaces the dock opens (until Kedip has its own).</summary>
    private static class Keys
    {
        public const byte A = 0x41;
        public const byte D = 0x44;
        public const byte N = 0x4E;
        public const byte X = 0x58;
        private const byte LeftWin = 0x5B;
        private const uint KeyUp = 0x0002;

        public static void Win(byte? key = null)
        {
            keybd_event(LeftWin, 0, 0, UIntPtr.Zero);
            if (key is { } k)
            {
                keybd_event(k, 0, 0, UIntPtr.Zero);
                keybd_event(k, 0, KeyUp, UIntPtr.Zero);
            }

            keybd_event(LeftWin, 0, KeyUp, UIntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}
