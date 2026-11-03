using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Interop;
using Kedip.Power;

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

    private readonly AppServices _services;
    private readonly DockApps _apps = new();
    private readonly NetworkInfo _network = new();
    private readonly SystemVolume _volume = new();
    private readonly TrayService _tray;
    private readonly DispatcherTimer _checkTimer;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _clockTimer;
    private IntPtr _hwnd;
    private bool _tucked;
    private bool _away;
    private bool _menuOpen;
    private GlassPanel? _panel;
    private AppBar? _appBar;
    private uint _taskbarCreatedMessage;
    private bool _taskbarPeek;
    private string _appsSignature = "";

    public DockWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DockAccent.Apply(Resources);
        _tray = new TrayService(Dispatcher);
        SeedPins();

        _checkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _checkTimer.Tick += (_, _) => Evaluate();
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += (_, _) => LiveTick();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
        _clockTimer.Tick += (_, _) => UpdateClock();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, e) =>
        {
            Place(animate: false);
            ScheduleShape();
            if (e.HeightChanged && _services.Settings.Current.DockReplaceTaskbar)
            {
                // The reserved strip follows the dock's real height (unknown until the first layout).
                ApplyTaskbarMode();
            }
        };
        MouseEnter += (_, _) => Evaluate();
        Closed += OnClosed;
        Bar.SizeChanged += (_, _) => ScheduleShape();

        StartButton.Click += (_, _) => TogglePanel(() => new AppsPanelWindow(_services), HorizontalAlignment.Left);
        StartButton.ContextMenu = QuickMenu.Start(this, () => _menuOpen = true, () =>
        {
            _menuOpen = false;
            Evaluate();
        });
        StatusButton.Click += (_, _) => TogglePanel(() => new ControlPanelWindow(_services, _volume), HorizontalAlignment.Right);
        StatusButton.MouseWheel += OnStatusWheel;
        TrayButton.Click += (_, _) => TogglePanel(
            () => new TrayPanelWindow(_tray, _services.Settings.Current.DockReplaceTaskbar, _taskbarPeek, ToggleTaskbarPeek),
            HorizontalAlignment.Center, TrayButton);
        ClockButton.Click += (_, _) => TogglePanel(() => new ClockPanelWindow(_services), HorizontalAlignment.Right);
        ClockButton.ContextMenu = ClockMenu();
        DesktopButton.Click += (_, _) => WinKeys.Win(WinKeys.D);
        TrayGlyph.Text = WinKeys.Glyph(0xE70E);

        _services.Settings.Changed += OnSettingsChanged;
        _services.Foreground.Changed += OnForegroundChanged;
        _services.Power.Changed += OnPowerChanged;
        _services.Weather.Updated += OnWeatherUpdated;
        _tray.Changed += (_, _) => UpdateTrayButton();
    }

    private bool Animations => _services.Settings.Current.Animations;

    private static double ScreenWidth => SystemParameters.PrimaryScreenWidth;

    private static double ScreenHeight => SystemParameters.PrimaryScreenHeight;

    /// <summary>Where the dock sits when shown: above the screen edge, or above the Windows taskbar while it is peeked.</summary>
    private double ShownTop
    {
        get
        {
            double bottom = ScreenHeight;
            if (_taskbarPeek && TaskbarControl.VisibleRect is { } taskbar)
            {
                var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                bottom = Math.Min(bottom, taskbar.Top / toDevice.M22);
            }

            return bottom - ActualHeight - BottomGap;
        }
    }

    // ---- Setup -------------------------------------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Glass.Apply(_hwnd, noActivate: true);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        _appBar = new AppBar(_hwnd);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    // ---- Shape ---------------------------------------------------------------------------------------------------

    private bool _shapePending;

    /// <summary>Re-cuts the window to the capsule once the layout has settled (apps come and go, widths change).</summary>
    private void ScheduleShape()
    {
        if (_shapePending)
        {
            return;
        }

        _shapePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _shapePending = false;
            ApplyShape();
        });
    }

    /// <summary>Only the capsule is window (blur, clicks): its round ends leave the corners to the desktop.</summary>
    private void ApplyShape()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var bounds = Bar.TransformToAncestor(this).TransformBounds(new Rect(Bar.RenderSize));
        Glass.Shape(_hwnd, [Rect.Transform(bounds, toDevice)], Bar.ActualHeight / 2 * toDevice.M22);
    }

    // ---- "Gantikan taskbar" --------------------------------------------------------------------------------------

    /// <summary>Hides or restores the Windows taskbar and reserves screen space when the dock is always shown.</summary>
    private void ApplyTaskbarMode()
    {
        var s = _services.Settings.Current;
        bool replace = s.DockReplaceTaskbar;
        UpdateTrayButton();
        if (!replace)
        {
            _taskbarPeek = false;
            if (TaskbarControl.IsHiddenByKedip)
            {
                TaskbarControl.Show();
            }
        }
        else if (!_taskbarPeek)
        {
            TaskbarControl.Hide();
        }

        if (replace && s.DockAutoHide == DockHide.Never && _appBar is not null && ActualHeight > 0)
        {
            var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            _appBar.Reserve((int)Math.Ceiling((ActualHeight + 2 * BottomGap) * toDevice.M22));
        }
        else
        {
            _appBar?.Release();
        }
    }

    private async void ToggleTaskbarPeek()
    {
        _taskbarPeek = !_taskbarPeek;
        if (_taskbarPeek)
        {
            TaskbarControl.Show();
            if (AppBar.TaskbarAutoHides())
            {
                // An auto-hide taskbar comes back slid away; Win+B reveals it with the tray (hidden icons) focused.
                WinKeys.Win(WinKeys.B);
            }
        }
        else
        {
            TaskbarControl.Hide();
        }

        ApplyTaskbarMode();
        Place(animate: true);
        // The taskbar slides in over a few hundred ms; settle above its final position.
        await Task.Delay(350);
        Place(animate: true);
    }

    /// <summary>Explorer re-shows its taskbar now and then (Start, restarts); hide it again unless a shell flyout is open.</summary>
    private void EnforceTaskbar()
    {
        if (!_services.Settings.Current.DockReplaceTaskbar || _taskbarPeek || !TaskbarControl.IsVisible)
        {
            return;
        }

        string cls = NativeMethods.GetClassName(NativeMethods.GetForegroundWindow());
        if (cls is "Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass" || Kedip.Mascot.ForegroundWatcher.IsShellClass(cls))
        {
            return;
        }

        TaskbarControl.Hide();
    }

    /// <summary>
    /// Opens a flyout above the dock (closing the other one), or closes it if it is already open. It lines up with the
    /// dock's left or right edge, or centres on <paramref name="anchor"/>.
    /// </summary>
    private void TogglePanel<T>(Func<T> create, HorizontalAlignment align, FrameworkElement? anchor = null) where T : GlassPanel
    {
        if (_panel is T open && !open.IsClosing)
        {
            open.CloseSmooth();
            return;
        }

        _panel?.CloseSmooth();
        var panel = create();
        _panel = panel;
        _menuOpen = true;
        panel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_panel, panel))
            {
                _panel = null;
                _menuOpen = false;
                Evaluate();
            }
        };
        double x = align switch
        {
            HorizontalAlignment.Right => Left + ActualWidth,
            HorizontalAlignment.Center when anchor is not null => Left + anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), this).X,
            _ => Left,
        };
        panel.ShowAt(x, ShownTop - 8, align, Animations);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWidgetVisibility();
        UpdateClock();
        UpdateBattery();
        UpdateDnd();
        _tray.Start();
        RefreshWeather();
        _appsSignature = "";
        LiveTick();
        Place(animate: false);
        ApplyTaskbarMode();
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
        _services.Weather.Updated -= OnWeatherUpdated;
        _tray.Dispose();
        _volume.Dispose();
        _appBar?.Release();
        if (TaskbarControl.IsHiddenByKedip)
        {
            TaskbarControl.Show();
        }
    }

    /// <summary>Starts out with the apps pinned to the Windows taskbar (in its order), or a default set if there are none.</summary>
    private void SeedPins()
    {
        var s = _services.Settings.Current;
        if (s.DockPinsFromWindows)
        {
            return;
        }

        var pins = WindowsPins.Read();
        _services.Settings.Update(settings =>
        {
            if (pins.Count > 0)
            {
                settings.DockPinned = pins;
            }
            else if (settings.DockPinned.Count == 0)
            {
                settings.DockPinned.AddRange(DockApps.DefaultPins);
            }

            settings.DockPinnedSeeded = true;
            settings.DockPinsFromWindows = true;
        });
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
        EnforceTaskbar();
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
        _tray.Prune();
    }

    private void UpdateTrayButton()
    {
        int count = _tray.Visible.Count();
        TrayButton.Visibility = count > 0 || _services.Settings.Current.DockReplaceTaskbar ? Visibility.Visible : Visibility.Collapsed;
        TrayButton.ToolTip = count > 0 ? $"Ikon tersembunyi ({count})" : "Taskbar Windows";
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

    /// <summary>
    /// An app on its own glass tile (all tiles the same size, so the row stays even whatever the logo); a dot below for
    /// running apps, wide for the one in front. The tile pops up a little under the cursor.
    /// </summary>
    private Button CreateTile(DockApp app)
    {
        UIElement logo;
        if (app.Icon is { } icon)
        {
            var image = new Image { Width = 28, Height = 28, Source = icon, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            logo = image;
        }
        else
        {
            logo = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var pop = new ScaleTransform();
        var face = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource(app.IsActive ? "AppTileActiveFill" : "AppTileFill"),
            BorderBrush = (Brush)FindResource("AppTileEdge"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = pop,
            Child = logo,
        };
        var dot = new Border
        {
            Height = 3.5,
            Width = app.IsActive ? 14 : 4,
            CornerRadius = new CornerRadius(1.75),
            Background = app.IsActive ? Brushes.White : DimDot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2.5),
            Visibility = app.IsRunning ? Visibility.Visible : Visibility.Hidden,
        };
        var body = new Grid { Width = 48, Height = 58 };
        body.Children.Add(face);
        body.Children.Add(dot);

        var tile = new Button { Style = (Style)FindResource("DockPlainButton"), Content = body, ToolTip = app.Name };
        AutomationProperties.SetName(tile, app.Name + (app.IsRunning ? $" ({app.Windows.Count})" : ""));
        void Scale(double to)
        {
            var duration = Animations ? TimeSpan.FromMilliseconds(140) : TimeSpan.Zero;
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { EasingFunction = SlideEase });
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = SlideEase });
        }

        tile.MouseEnter += (_, _) => Scale(1.1);
        tile.MouseLeave += (_, _) => Scale(1);
        tile.PreviewMouseLeftButtonDown += (_, _) => Scale(0.94);
        tile.PreviewMouseLeftButtonUp += (_, _) => Scale(tile.IsMouseOver ? 1.1 : 1);
        tile.Click += (_, _) =>
        {
            DockApps.Click(app);
            _appsSignature = "";
        };
        tile.ContextMenu = BuildAppMenu(app);
        return tile;
    }

    /// <summary>Right-click on the clock: the Windows date settings and Windows' own notification centre.</summary>
    private ContextMenu ClockMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            Evaluate();
        };
        menu.Items.Add(MenuEntry("Atur tanggal & waktu", "", () => SystemActions.Open("ms-settings:dateandtime")));
        menu.Items.Add(MenuEntry("Pusat notifikasi Windows", "", () => WinKeys.Win(WinKeys.N)));
        return menu;
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
        NetworkCell.ToolTip = _network.Connection switch
        {
            ConnectionKind.Wifi => RadioControl.ConnectedNetworkName() is { } ssid ? $"{ssid}\nAkses internet" : "Wi-Fi tersambung",
            ConnectionKind.Ethernet => "Ethernet tersambung",
            _ => "Tidak ada koneksi",
        };

        if (_volume.Read() is { } volume)
        {
            VolumeGlyph.Text = volume.Muted || volume.Level < 0.005 ? "\uE74F"
                : volume.Level < 0.34 ? "\uE993"
                : volume.Level < 0.67 ? "\uE994"
                : "\uE995";
            VolumeCell.ToolTip = volume.Muted ? "Suara dibisukan" : $"Volume {Math.Round(volume.Level * 100)}%";
        }
        else
        {
            VolumeGlyph.Text = "\uE767";
        }
    }

    private void UpdateBattery()
    {
        var power = _services.Power;
        BatteryCell.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        int level = Math.Clamp(power.Percent / 10, 0, 10);
        BatteryGlyph.Text = power.IsCharging ? char.ConvertFromUtf32(0xEBAB + level) : PowerService.BatteryGlyph(power.Percent);
        BatteryPercent.Text = $"{power.Percent}%";
        BatteryCell.ToolTip = $"Baterai {power.Percent}%" + (power.IsCharging ? " · mengisi daya" : "");
    }

    /// <summary>A small moon next to the clock while Do Not Disturb is on (it is switched in the control panel).</summary>
    private void UpdateDnd()
    {
        bool on = _services.Settings.Current.DoNotDisturb;
        DndBadge.Text = WinKeys.Glyph(0xE708);
        DndBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Scrolling over the status group changes the volume in steps of 2, like the Windows tray.</summary>
    private void OnStatusWheel(object sender, MouseWheelEventArgs e)
    {
        if (_volume.Read() is not { } volume)
        {
            return;
        }

        float level = Math.Clamp(volume.Level + Math.Sign(e.Delta) * 0.02f, 0, 1);
        _volume.SetLevel(level);
        if (volume.Muted && e.Delta > 0)
        {
            _volume.SetMuted(false);
        }

        e.Handled = true;
        RefreshNetworkAndVolume();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        DateText.Text = now.ToString("d/M/yyyy", CultureInfo.InvariantCulture);
        ClockButton.ToolTip = now.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("id-ID"))
            + (_services.Settings.Current.DoNotDisturb ? "\nJangan ganggu nyala" : "");
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
            _services.Weather.RefreshIfStale();
        }
    }

    private void ApplyWidgetVisibility()
    {
        var s = _services.Settings.Current;
        NetSpeedPanel.Visibility = s.DockNetSpeed ? Visibility.Visible : Visibility.Collapsed;
        WeatherPanel.Visibility = s.DockWeather && _services.Weather.HasData ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Events ------------------------------------------------------------------------------------------------

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyTaskbarMode();
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

    private void OnWeatherUpdated(object? sender, EventArgs e)
    {
        var weather = _services.Weather;
        WeatherGlyph.Text = weather.Glyph();
        WeatherText.Text = weather.Temperature is { } t ? $"{Math.Round(t)}°" : "";
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

        if (_appBar is not null && msg == _appBar.CallbackMessage && wParam.ToInt32() == AppBar.ABN_POSCHANGED)
        {
            _appBar.Reposition();
        }
        else if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            // Explorer restarted and brought a fresh taskbar.
            Dispatcher.InvokeAsync(ApplyTaskbarMode);
        }

        return IntPtr.Zero;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.RECT value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
