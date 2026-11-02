using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Prayer;
using Kedip.Shell.Settings;

namespace Kedip.Shell;

/// <summary>Settings UI; every change applies live via SettingsStore.Update. OWNER: F.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Stored in <see cref="AppSettings.PrayerCity"/> when the user enters coordinates by hand.</summary>
    public const string CustomCityName = "Lokasi kustom";

    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        "ScrollOffset", typeof(double), typeof(SettingsWindow), new PropertyMetadata(0.0, OnScrollOffsetChanged));

    private static readonly NotchSizePreset[] SizePresets =
        [NotchSizePreset.Small, NotchSizePreset.Medium, NotchSizePreset.Large, NotchSizePreset.Custom];

    private readonly AppServices _services;
    private readonly List<Action<AppSettings>> _loaders = [];
    private readonly List<(RadioButton Nav, StackPanel Page)> _pages = [];
    private readonly DispatcherTimer _sizeDebounce;
    private readonly IReadOnlyList<City> _cities;
    private StackPanel? _currentPage;
    private bool _syncing;
    private bool _writing;
    private bool _sizePending;
    private bool _scrollAnimating;
    private double _scrollTarget;
    private DoubleAnimation? _scrollAnimation;

    public SettingsWindow(AppServices services)
    {
        _services = services;
        _cities = Cities.All;
        InitializeComponent();
        DwmStyling.UseDarkFrame(this);

        _sizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _sizeDebounce.Tick += (_, _) => FlushSize();

        NavVersionText.Text = $"Versi {AppInfo.Version}";
        AboutVersionText.Text = $"Versi {AppInfo.Version}";
        DataPathText.Text = AppPaths.DataDir;
        DataPathText.ToolTip = AppPaths.DataDir;

        SetUpNavigation();
        SetUpAppearance();
        SetUpDock();
        SetUpMascot();
        SetUpMusic();
        SetUpNotifications();
        SetUpPrayer();
        SetUpWater();
        SetUpTimer();
        SetUpGeneral();
        Refresh(animate: false);

        Store.Changed += OnSettingsChanged;
        Scroller.PreviewMouseWheel += OnPreviewMouseWheel;
        KeyDown += OnWindowKeyDown;
        Loaded += (_, _) => AnimatePageIn(_currentPage);
        Closed += OnClosed;
    }

    private SettingsStore Store => _services.Settings;

    // ---- Sections -------------------------------------------------------------------------------

    private void SetUpNavigation()
    {
        _pages.AddRange(
        [
            (NavAppearance, PageAppearance),
            (NavDock, PageDock),
            (NavMascot, PageMascot),
            (NavMusic, PageMusic),
            (NavNotifications, PageNotifications),
            (NavPrayer, PagePrayer),
            (NavWater, PageWater),
            (NavTimer, PageTimer),
            (NavGeneral, PageGeneral),
        ]);
        foreach (var (nav, page) in _pages)
        {
            page.RenderTransform = new TranslateTransform();
            nav.Checked += (_, _) => ShowPage(page);
        }

        NavAppearance.IsChecked = true;
    }

    private void SetUpAppearance()
    {
        _loaders.Add(s =>
        {
            SizeSegments.SelectedIndex = Array.IndexOf(SizePresets, s.SizePreset);
            var (width, height) = s.GetNotchSize();
            WidthSlider.Value = width;
            HeightSlider.Value = height;
            PositionSegments.SelectedIndex = s.Position switch
            {
                NotchPosition.Left => 0,
                NotchPosition.Right => 2,
                _ => 1,
            };
        });

        SizeSegments.SelectionChanged += (_, _) =>
        {
            if (SizeSegments.SelectedIndex < 0)
            {
                return;
            }

            var preset = SizePresets[SizeSegments.SelectedIndex];
            CancelPendingSize();
            Apply(s => s.SizePreset = preset);
        };

        WidthSlider.ValueChanged += OnSizeSliderChanged;
        HeightSlider.ValueChanged += OnSizeSliderChanged;
        WidthSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => FlushSize()));
        HeightSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => FlushSize()));

        PositionSegments.SelectionChanged += (_, _) =>
        {
            var position = PositionSegments.SelectedIndex switch
            {
                0 => NotchPosition.Left,
                2 => NotchPosition.Right,
                _ => NotchPosition.Center,
            };
            Apply(s => s.Position = position);
        };

        _loaders.Add(s => TuckSegments.SelectedIndex = s.Tuck switch
        {
            TuckMode.Off => 0,
            TuckMode.AnyWindow => 2,
            _ => 1,
        });
        TuckSegments.SelectionChanged += (_, _) =>
        {
            var tuck = TuckSegments.SelectedIndex switch
            {
                0 => TuckMode.Off,
                2 => TuckMode.AnyWindow,
                _ => TuckMode.Browser,
            };
            Apply(s => s.Tuck = tuck);
        };
        _loaders.Add(s => TuckMusicSegments.SelectedIndex = (int)s.TuckMusic);
        TuckMusicSegments.SelectionChanged += (_, _) =>
        {
            if (TuckMusicSegments.SelectedIndex >= 0)
            {
                var view = (TuckMusicView)TuckMusicSegments.SelectedIndex;
                Apply(s => s.TuckMusic = view);
            }
        };
        BindSwitch(TuckClickSwitch, s => s.TuckClickToOpen, (s, v) => s.TuckClickToOpen = v);
        BindSwitch(TuckLyricsSwitch, s => s.TuckKeepsLyrics, (s, v) => s.TuckKeepsLyrics = v);

        BindSwitch(AnimationsSwitch, s => s.Animations, (s, v) => s.Animations = v);
        BindSwitch(FullscreenSwitch, s => s.HideInFullscreen, (s, v) => s.HideInFullscreen = v);
    }

    private void SetUpDock()
    {
        _loaders.Add(s => ShowSegments.SelectedIndex = (int)s.Show);
        ShowSegments.SelectionChanged += (_, _) =>
        {
            if (ShowSegments.SelectedIndex >= 0)
            {
                var mode = (ShowMode)ShowSegments.SelectedIndex;
                Apply(s => s.Show = mode);
            }
        };

        _loaders.Add(s => DockHideSegments.SelectedIndex = s.DockAutoHide == DockHide.Never ? 1 : 0);
        DockHideSegments.SelectionChanged += (_, _) =>
        {
            var hide = DockHideSegments.SelectedIndex == 1 ? DockHide.Never : DockHide.Smart;
            Apply(s => s.DockAutoHide = hide);
        };

        BindSwitch(DockTimerSwitch, s => s.DockTimer, (s, v) => s.DockTimer = v);
        BindSwitch(DockWeatherSwitch, s => s.DockWeather, (s, v) => s.DockWeather = v);
        BindSwitch(DockNetSwitch, s => s.DockNetSpeed, (s, v) => s.DockNetSpeed = v);
    }

    private void SetUpMascot()
    {
        _loaders.Add(s => IdleStyleSegments.SelectedIndex = s.IdleStyle == IdleStyle.Clock ? 1 : 0);
        IdleStyleSegments.SelectionChanged += (_, _) =>
        {
            var style = IdleStyleSegments.SelectedIndex == 1 ? IdleStyle.Clock : IdleStyle.Eyes;
            Apply(s => s.IdleStyle = style);
        };

        BindSwitch(EyesOnDesktopSwitch, s => s.EyesOnDesktop, (s, v) => s.EyesOnDesktop = v);
        BindNumber(EyesIdleBox, s => s.EyesIdleSeconds, (s, v) => s.EyesIdleSeconds = (int)v);
        BindNumber(DrowsyBox, s => s.DrowsyAfterMinutes, (s, v) =>
        {
            s.DrowsyAfterMinutes = (int)v;
            if (s.SleepAfterMinutes <= s.DrowsyAfterMinutes)
            {
                s.SleepAfterMinutes = s.DrowsyAfterMinutes + 1;
            }
        });
        BindNumber(SleepBox, s => s.SleepAfterMinutes, (s, v) => s.SleepAfterMinutes = Math.Max((int)v, s.DrowsyAfterMinutes + 1));
        BindSwitch(FollowCursorSwitch, s => s.EyesFollowCursor, (s, v) => s.EyesFollowCursor = v);
    }

    private void SetUpMusic()
    {
        BindSwitch(MusicSwitch, s => s.MusicEnabled, (s, v) => s.MusicEnabled = v);
        BindSwitch(LyricsSwitch, s => s.LyricsEnabled, (s, v) => s.LyricsEnabled = v);
        BindSwitch(LyricsInNotchSwitch, s => s.LyricsInNotch, (s, v) => s.LyricsInNotch = v);
        BindSwitch(LyricsExtraSwitch, s => s.LyricsExtraSources, (s, v) => s.LyricsExtraSources = v);
    }

    private void SetUpNotifications()
    {
        BindSwitch(DndSwitch, s => s.DoNotDisturb, (s, v) => s.DoNotDisturb = v);
        BindSwitch(NotificationsSwitch, s => s.NotificationsEnabled, (s, v) => s.NotificationsEnabled = v);
        BindSwitch(NotificationContentSwitch, s => s.NotificationShowContent, (s, v) => s.NotificationShowContent = v);
        BuildNotificationApps();
        _services.Notifications.AppsChanged += OnNotificationAppsChanged;
    }

    private void OnNotificationAppsChanged(object? sender, EventArgs e) => BuildNotificationApps();

    private void BuildNotificationApps()
    {
        NotificationAppsPanel.Children.Clear();
        var apps = _services.Notifications.GetKnownApps();
        NotificationAppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var muted = Store.Current.NotificationMutedApps;
        foreach (var app in apps)
        {
            if (NotificationAppsPanel.Children.Count > 0)
            {
                NotificationAppsPanel.Children.Add(new Border { Style = (Style)FindResource("SDivider") });
            }

            var row = new CheckBox
            {
                Content = app.Name,
                IsChecked = !muted.Contains(app.AppId, StringComparer.OrdinalIgnoreCase),
            };
            string id = app.AppId;
            row.Click += (_, _) =>
            {
                bool on = row.IsChecked == true;
                Apply(s =>
                {
                    s.NotificationMutedApps.RemoveAll(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase));
                    if (!on)
                    {
                        s.NotificationMutedApps.Add(id);
                    }
                });
            };
            NotificationAppsPanel.Children.Add(row);
        }
    }

    private void SetUpPrayer()
    {
        BindSwitch(PrayerSwitch, s => s.PrayerEnabled, (s, v) => s.PrayerEnabled = v);

        var names = new List<string>(_cities.Count + 1) { CustomCityName };
        names.AddRange(_cities.Select(c => c.Name));
        CityCombo.ItemsSource = names;
        _loaders.Add(s => CityCombo.SelectedIndex = FindCity(s.PrayerCity) + 1);
        CityCombo.SelectionChanged += (_, _) =>
        {
            int index = CityCombo.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            if (index == 0)
            {
                Apply(s => s.PrayerCity = CustomCityName);
                return;
            }

            var city = _cities[index - 1];
            Apply(s =>
            {
                s.PrayerCity = city.Name;
                s.Latitude = city.Latitude;
                s.Longitude = city.Longitude;
                s.TimeZoneHours = city.TimeZoneHours;
            });
        };

        BindNumber(LatitudeBox, s => s.Latitude, (s, v) => s.Latitude = v);
        BindNumber(LongitudeBox, s => s.Longitude, (s, v) => s.Longitude = v);
        BindNumber(TimeZoneBox, s => s.TimeZoneHours, (s, v) => s.TimeZoneHours = v);
        LatitudeBox.CustomTextHandler = TryApplyCoordinatePair;
        LongitudeBox.CustomTextHandler = TryApplyCoordinatePair;

        BindNumber(IhtiyatBox, s => s.IhtiyatMinutes, (s, v) => s.IhtiyatMinutes = (int)v);
        BindSwitch(PrayerReminderSwitch, s => s.PrayerReminder, (s, v) => s.PrayerReminder = v);
        BindSwitch(PrayerSoundSwitch, s => s.PrayerSound, (s, v) => s.PrayerSound = v);
    }

    private void SetUpWater()
    {
        BindSwitch(WaterSwitch, s => s.WaterEnabled, (s, v) => s.WaterEnabled = v);
        BindNumber(WaterGoalBox, s => s.WaterGoalMl, (s, v) => s.WaterGoalMl = (int)v);
        BindNumber(GlassBox, s => s.GlassMl, (s, v) => s.GlassMl = (int)v);
        BindNumber(WaterIntervalBox, s => s.WaterIntervalMinutes, (s, v) => s.WaterIntervalMinutes = (int)v);

        // Start 00–22, end 01–23, and the end always stays after the start.
        WaterStartCombo.ItemsSource = Hours(0, 22);
        WaterEndCombo.ItemsSource = Hours(1, 23);
        _loaders.Add(s =>
        {
            WaterStartCombo.SelectedIndex = Math.Clamp(s.WaterStartHour, 0, 22);
            WaterEndCombo.SelectedIndex = Math.Clamp(s.WaterEndHour, 1, 23) - 1;
        });
        WaterStartCombo.SelectionChanged += (_, _) =>
        {
            int start = WaterStartCombo.SelectedIndex;
            if (start < 0)
            {
                return;
            }

            Apply(s =>
            {
                s.WaterStartHour = start;
                if (s.WaterEndHour <= start)
                {
                    s.WaterEndHour = start + 1;
                }
            });
        };
        WaterEndCombo.SelectionChanged += (_, _) =>
        {
            if (WaterEndCombo.SelectedIndex < 0)
            {
                return;
            }

            int end = WaterEndCombo.SelectedIndex + 1;
            Apply(s =>
            {
                s.WaterEndHour = end;
                if (s.WaterStartHour >= end)
                {
                    s.WaterStartHour = end - 1;
                }
            });
        };
    }

    private void SetUpTimer()
    {
        BindNumber(FocusBox, s => s.FocusMinutes, (s, v) => s.FocusMinutes = (int)v);
        BindNumber(ShortBreakBox, s => s.ShortBreakMinutes, (s, v) => s.ShortBreakMinutes = (int)v);
        BindNumber(LongBreakBox, s => s.LongBreakMinutes, (s, v) => s.LongBreakMinutes = (int)v);
        BindNumber(SessionsBox, s => s.SessionsBeforeLongBreak, (s, v) => s.SessionsBeforeLongBreak = (int)v);
        BindSwitch(TimerSoundSwitch, s => s.TimerSound, (s, v) => s.TimerSound = v);
    }

    private void SetUpGeneral() =>
        BindSwitch(StartupSwitch, s => s.StartWithWindows, (s, v) => s.StartWithWindows = v);

    // ---- Settings <-> controls ------------------------------------------------------------------

    private void BindSwitch(ToggleButton toggle, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        _loaders.Add(s => toggle.IsChecked = get(s));
        void OnToggled(object sender, RoutedEventArgs e)
        {
            bool value = toggle.IsChecked == true;
            Apply(s => set(s, value));
        }

        toggle.Checked += OnToggled;
        toggle.Unchecked += OnToggled;
    }

    private void BindNumber(NumberBox box, Func<AppSettings, double> get, Action<AppSettings, double> set)
    {
        _loaders.Add(s => box.Value = get(s));
        box.ValueCommitted += (_, _) =>
        {
            double value = box.Value;
            Apply(s => set(s, value));
        };
    }

    /// <summary>Writes a change (ignored while controls are being filled), then re-reads everything it may have adjusted.</summary>
    private void Apply(Action<AppSettings> mutate)
    {
        if (_syncing)
        {
            return;
        }

        if (_sizePending)
        {
            FlushSize();
        }

        _writing = true;
        try
        {
            Store.Update(mutate);
        }
        catch (Exception ex)
        {
            Log.Error("Settings window: could not apply a change", ex);
        }
        finally
        {
            _writing = false;
        }

        Refresh(animate: true);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Changes from elsewhere (tray menu, services) while the window is open.
        if (!_writing)
        {
            Refresh(animate: true);
        }
    }

    private void Refresh(bool animate)
    {
        var s = Store.Current;
        Sync(() =>
        {
            foreach (var load in _loaders)
            {
                load(s);
            }
        });
        UpdateDependents(s);
        UpdateSizeLabels();
        UpdatePreview(animate);
    }

    /// <summary>Enables/describes rows that depend on other settings.</summary>
    private void UpdateDependents(AppSettings s)
    {
        bool eyes = s.IdleStyle == IdleStyle.Eyes;
        EyesOptions.IsEnabled = eyes;
        IdleStyleRow.Description = eyes
            ? "Mata maskot muncul saat kamu sedang diam"
            : "Jam yang tampil, mata maskot istirahat dulu";

        LyricsSwitch.IsEnabled = s.MusicEnabled;
        LyricsInNotchSwitch.IsEnabled = s.MusicEnabled && s.LyricsEnabled;
        LyricsExtraSwitch.IsEnabled = s.MusicEnabled && s.LyricsEnabled;
        NotificationContentSwitch.IsEnabled = s.NotificationsEnabled;
        TuckMusicSegments.IsEnabled = s.Tuck != TuckMode.Off;
        TuckMusicRow.IsEnabled = s.Tuck != TuckMode.Off;
        // With "Lirik tetap tampil" on, songs with lyrics keep their lyric line, so this choice only covers the rest.
        TuckMusicRow.Header = s.TuckKeepsLyrics ? "Saat ada lagu tanpa lirik" : "Saat ada lagu";
        TuckMusicRow.Description = s.TuckKeepsLyrics
            ? "Khusus lagu yang liriknya belum tersedia. Lagu yang ada liriknya tetap menampilkan lirik (sakelar \"Lirik tetap tampil\" di bawah)."
            : "Berlaku untuk semua lagu yang diputar. Gelombang bergoyang mengikuti beat.";
        TuckClickSwitch.IsEnabled = s.Tuck != TuckMode.Off;
        TuckLyricsSwitch.IsEnabled = s.Tuck != TuckMode.Off;
        NotificationAppsPanel.IsEnabled = s.NotificationsEnabled;

        bool custom = FindCity(s.PrayerCity) < 0;
        PrayerLocation.IsEnabled = s.PrayerEnabled;
        PrayerReminderOptions.IsEnabled = s.PrayerEnabled;
        LatitudeRow.IsEnabled = custom;
        LongitudeRow.IsEnabled = custom;
        TimeZoneRow.IsEnabled = custom;
        TimeZoneRow.Description = DescribeZone(s.TimeZoneHours);
        PrayerSoundSwitch.IsEnabled = s.PrayerReminder;

        WaterTarget.IsEnabled = s.WaterEnabled;
        WaterSchedule.IsEnabled = s.WaterEnabled;
        int glasses = (int)Math.Ceiling(s.WaterGoalMl / (double)Math.Max(1, s.GlassMl));
        WaterGoalRow.Description = $"Sekitar {glasses} gelas sehari";
    }

    private void Sync(Action action)
    {
        bool was = _syncing;
        _syncing = true;
        try
        {
            action();
        }
        finally
        {
            _syncing = was;
        }
    }

    // ---- Notch size -----------------------------------------------------------------------------

    private void OnSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSizeLabels();
        if (_syncing)
        {
            return;
        }

        // Moving a slider means "custom size"; the notch follows after a short pause (debounced write).
        Sync(() => SizeSegments.SelectedIndex = Array.IndexOf(SizePresets, NotchSizePreset.Custom));
        UpdatePreview(animate: false);
        _sizePending = true;
        _sizeDebounce.Stop();
        _sizeDebounce.Start();
    }

    private void FlushSize()
    {
        _sizeDebounce.Stop();
        if (!_sizePending)
        {
            return;
        }

        _sizePending = false;
        double width = Math.Clamp(Math.Round(WidthSlider.Value), 80, 320);
        double height = Math.Clamp(Math.Round(HeightSlider.Value), 22, 44);
        Apply(s =>
        {
            s.SizePreset = NotchSizePreset.Custom;
            s.CustomWidth = width;
            s.CustomHeight = height;
        });
    }

    private void CancelPendingSize()
    {
        _sizeDebounce.Stop();
        _sizePending = false;
    }

    private void UpdateSizeLabels()
    {
        WidthValue.Text = WidthSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        HeightValue.Text = HeightSlider.Value.ToString("0", CultureInfo.InvariantCulture);
    }

    private void UpdatePreview(bool animate)
    {
        var position = PositionSegments.SelectedIndex switch
        {
            0 => NotchPosition.Left,
            2 => NotchPosition.Right,
            _ => NotchPosition.Center,
        };
        var idle = IdleStyleSegments.SelectedIndex == 1 ? IdleStyle.Clock : IdleStyle.Eyes;
        Preview.Show(Math.Round(WidthSlider.Value), Math.Round(HeightSlider.Value), position, idle, animate);
    }

    // ---- Prayer helpers -------------------------------------------------------------------------

    private int FindCity(string name)
    {
        for (int i = 0; i < _cities.Count; i++)
        {
            if (string.Equals(_cities[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Accepts a pasted "lat, lon" pair (e.g. from Google Maps) in either coordinate box.</summary>
    private bool TryApplyCoordinatePair(string text)
    {
        if (!TryParseCoordinatePair(text, out double latitude, out double longitude))
        {
            return false;
        }

        Apply(s =>
        {
            s.PrayerCity = CustomCityName;
            s.Latitude = Math.Round(latitude, 4);
            s.Longitude = Math.Round(longitude, 4);
        });
        return true;
    }

    internal static bool TryParseCoordinatePair(string text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        string[] parts = text.Split([';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim(','))
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length == 1 && parts[0].Contains('.') && parts[0].Count(c => c == ',') == 1)
        {
            parts = parts[0].Split(',', StringSplitOptions.TrimEntries);
        }

        return parts.Length == 2
            && NumberBox.TryParse(parts[0], out latitude) && latitude is >= -90 and <= 90
            && NumberBox.TryParse(parts[1], out longitude) && longitude is >= -180 and <= 180;
    }

    internal static string DescribeZone(double hours)
    {
        string sign = hours < 0 ? "−" : "+";
        string offset = "UTC" + sign + Math.Abs(hours).ToString("0.##", CultureInfo.InvariantCulture);
        string? name = hours switch
        {
            7d => "WIB",
            8d => "WITA",
            9d => "WIT",
            _ => null,
        };
        return name is null ? offset : $"{name} · {offset}";
    }

    private static List<string> Hours(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(h => h.ToString("00", CultureInfo.InvariantCulture) + ".00").ToList();

    // ---- Navigation, scrolling, lifetime --------------------------------------------------------

    private void ShowPage(StackPanel page)
    {
        if (ReferenceEquals(page, _currentPage))
        {
            return;
        }

        if (_currentPage is not null)
        {
            _currentPage.Visibility = Visibility.Collapsed;
        }

        _currentPage = page;
        page.Visibility = Visibility.Visible;
        StopScrollAnimation();
        Scroller.ScrollToVerticalOffset(0);
        if (IsLoaded)
        {
            AnimatePageIn(page);
        }
    }

    private static void AnimatePageIn(StackPanel? page)
    {
        if (page is null)
        {
            return;
        }

        Motion.FromTo(page, OpacityProperty, 0, 1, 180);
        if (page.RenderTransform is TranslateTransform shift)
        {
            Motion.FromTo(shift, TranslateTransform.YProperty, 12, 0, 280);
        }
    }

    /// <summary>Smooth, eased wheel scrolling (the stock ScrollViewer jumps in 48 px steps).</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Leave the wheel alone while something has capture (e.g. an open combo box list).
        if (e.Handled || Mouse.Captured is not null || !Motion.Enabled || Scroller.ScrollableHeight <= 0)
        {
            return;
        }

        double from = Scroller.VerticalOffset;
        double start = _scrollAnimating ? _scrollTarget : from;
        _scrollTarget = Math.Clamp(start - e.Delta * 0.75, 0, Scroller.ScrollableHeight);
        e.Handled = true;
        if (Math.Abs(_scrollTarget - from) < 0.5)
        {
            return;
        }

        var animation = new DoubleAnimation(from, _scrollTarget, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.EaseOut };
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(_scrollAnimation, animation))
            {
                _scrollAnimating = false;
            }
        };
        _scrollAnimation = animation;
        _scrollAnimating = true;
        BeginAnimation(ScrollOffsetProperty, animation);
    }

    private void StopScrollAnimation()
    {
        // Clear the flag first so the property falling back to its base value does not scroll.
        _scrollAnimating = false;
        _scrollAnimation = null;
        BeginAnimation(ScrollOffsetProperty, null);
    }

    private static void OnScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var window = (SettingsWindow)d;
        if (window._scrollAnimating)
        {
            window.Scroller.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => AppInfo.OpenDataFolder();

    private void OnClosed(object? sender, EventArgs e)
    {
        FlushSize();
        Store.Changed -= OnSettingsChanged;
        _services.Notifications.AppsChanged -= OnNotificationAppsChanged;
        StopScrollAnimation();
    }
}
