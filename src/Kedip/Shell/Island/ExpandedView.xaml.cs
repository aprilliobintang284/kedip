using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kedip.Core;
using Kedip.Views.Panels;

namespace Kedip.Shell.Island;

/// <summary>Expanded island: tab row (glyph buttons + status) and the feature panel host.</summary>
public partial class ExpandedView : UserControl
{
    public const double BodyWidth = 460;
    public const double BodyHeight = 190;

    private const double TabPitch = 40;
    private static readonly TimeSpan PanelIn = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PanelOut = TimeSpan.FromMilliseconds(120);

    private readonly AppServices _services;
    private readonly Dictionary<IslandTab, FrameworkElement> _panels = [];
    private readonly Dictionary<IslandTab, Button> _tabButtons = [];
    private readonly List<IslandTab> _tabs = [];
    private IslandTab? _active;

    internal ExpandedView(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DropGlyph.Text = Glyphs.Download;
        TopRow.MouseLeftButtonUp += OnTopRowClick;
        _services.Power.Changed += (_, _) => UpdateBattery();
        _services.Weather.Updated += (_, _) => UpdateWeather();
        _services.Settings.Changed += (_, _) => UpdateWeather();
        UpdateBattery();
        UpdateWeather();
        RebuildTabs();
    }

    /// <summary>The user clicked the empty part of the tab row.</summary>
    internal event EventHandler? CollapseRequested;

    /// <summary>The user picked a tab.</summary>
    internal event EventHandler<IslandTab>? TabSelected;

    internal IslandTab? ActiveTab => _active;

    internal IReadOnlyList<IslandTab> Tabs => _tabs;

    internal bool IsOpen { get; private set; }

    internal void Open(IslandTab tab, bool animate)
    {
        IsOpen = true;
        Select(tab, animate: false);
        Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Zoom.ScaleX = animate ? 0.94 : 1;
        Zoom.ScaleY = animate ? 0.9 : 1;
        Motion.To(Zoom, ScaleTransform.ScaleXProperty, 1, Motion.Grow, Motion.Spring, animate);
        Motion.To(Zoom, ScaleTransform.ScaleYProperty, 1, Motion.Grow, Motion.Spring, animate);
        Motion.FadeTo(this, true, animate, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(70));
    }

    internal void Close(bool animate)
    {
        IsOpen = false;
        SetDropHint(false, animate);
        Motion.FadeTo(this, false, animate, TimeSpan.FromMilliseconds(100));
    }

    internal void SetClock(string text) => ClockText.Text = text;

    internal void SetDropHint(bool visible, bool animate) => Motion.FadeTo(DropHint, visible, animate);

    /// <summary>Re-reads which features are enabled; keeps the active tab when it still exists.</summary>
    internal void RebuildTabs()
    {
        var settings = _services.Settings.Current;
        var wanted = new List<IslandTab>(5);
        if (settings.MusicEnabled)
        {
            wanted.Add(IslandTab.Music);
        }

        if (settings.PrayerEnabled)
        {
            wanted.Add(IslandTab.Prayer);
        }

        if (settings.WeatherEnabled)
        {
            wanted.Add(IslandTab.Weather);
        }

        wanted.Add(IslandTab.Timer);
        wanted.Add(IslandTab.Shelf);
        if (wanted.SequenceEqual(_tabs))
        {
            return;
        }

        _tabs.Clear();
        _tabs.AddRange(wanted);
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            TabStrip.Children.Add(GetButton(tab));
        }

        if (_active is { } active && !_tabs.Contains(active))
        {
            // Keep the instance (it may hold subscriptions); just hide it until the feature is enabled again.
            if (_panels.TryGetValue(active, out var hidden))
            {
                Motion.FadeTo(hidden, false, animate: false);
            }

            _active = null;
            if (IsOpen)
            {
                Select(_tabs[0], animate: false);
            }
        }
        else if (_active is { } still)
        {
            UpdateTabVisuals(still, animate: false);
        }
    }

    internal void Select(IslandTab tab, bool animate)
    {
        if (!_tabs.Contains(tab))
        {
            tab = _tabs[0];
        }

        var previous = _active;
        if (previous == tab)
        {
            UpdateTabVisuals(tab, animate);
            return;
        }

        _active = tab;
        var incoming = GetPanel(tab);
        int direction = previous is { } p && _tabs.IndexOf(p) > _tabs.IndexOf(tab) ? -1 : 1;
        foreach (var (key, panel) in _panels)
        {
            if (key != tab && panel.Visibility == Visibility.Visible)
            {
                Motion.FadeTo(panel, false, animate, PanelOut);
                Motion.To((TranslateTransform)panel.RenderTransform, TranslateTransform.XProperty, -12 * direction, PanelOut,
                    Motion.Decelerate, animate);
            }
        }

        var shift = (TranslateTransform)incoming.RenderTransform;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = animate && previous is not null ? 12 * direction : 0;
        if (incoming.Visibility != Visibility.Visible)
        {
            incoming.Opacity = 0;
        }

        Motion.To(shift, TranslateTransform.XProperty, 0, PanelIn, Motion.Decelerate, animate);
        Motion.FadeTo(incoming, true, animate, PanelIn, TimeSpan.FromMilliseconds(animate && previous is not null ? 40 : 0));
        UpdateTabVisuals(tab, animate && previous is not null);
    }

    private Button GetButton(IslandTab tab)
    {
        if (_tabButtons.TryGetValue(tab, out var existing))
        {
            return existing;
        }

        var (glyph, name) = tab switch
        {
            IslandTab.Music => (Glyphs.Music, "Musik"),
            IslandTab.Prayer => (Glyphs.Moon, "Sholat"),
            IslandTab.Weather => (Glyphs.Cloud, "Cuaca"),
            IslandTab.Timer => (Glyphs.Timer, "Timer"),
            _ => (Glyphs.Shelf, "Shelf"),
        };
        var button = new Button
        {
            Style = (Style)Resources["TabButtonStyle"],
            ToolTip = name,
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 14,
                Foreground = (Brush)FindResource("DockTextBrush"),
                Opacity = 0.5,
            },
        };
        button.Click += (_, _) =>
        {
            Select(tab, animate: _services.Settings.Current.Animations);
            TabSelected?.Invoke(this, tab);
        };
        _tabButtons[tab] = button;
        return button;
    }

    private FrameworkElement GetPanel(IslandTab tab)
    {
        if (_panels.TryGetValue(tab, out var existing))
        {
            return existing;
        }

        FrameworkElement panel;
        try
        {
            panel = tab switch
            {
                IslandTab.Music => new MusicPanel(_services),
                IslandTab.Prayer => new PrayerPanel(_services),
                IslandTab.Weather => new WeatherPanel(_services),
                IslandTab.Timer => new TimerPanel(_services),
                _ => new ShelfPanel(_services),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Could not create the {tab} panel", ex);
            panel = new TextBlock
            {
                Text = "Panel ini lagi nggak bisa dibuka",
                Foreground = (Brush)FindResource("DockText2Brush"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        panel.Width = 440;
        panel.Height = 150;
        panel.Opacity = 0;
        panel.Visibility = Visibility.Collapsed;
        panel.RenderTransform = new TranslateTransform();
        _panels[tab] = panel;
        PanelHost.Children.Add(panel);
        return panel;
    }

    private void UpdateTabVisuals(IslandTab active, bool animate)
    {
        foreach (var (tab, button) in _tabButtons)
        {
            if (button.Content is UIElement glyph)
            {
                Motion.To(glyph, OpacityProperty, tab == active ? 1 : 0.5, Motion.Hover, Motion.Soft, animate);
            }
        }

        int index = _tabs.IndexOf(active);
        if (index < 0)
        {
            TabIndicator.Opacity = 0;
            return;
        }

        bool firstShow = TabIndicator.Opacity < 0.5;
        TabIndicator.Opacity = 1;
        Motion.To(IndicatorShift, TranslateTransform.XProperty, index * TabPitch, TimeSpan.FromMilliseconds(260), Motion.Spring,
            animate && !firstShow);
    }

    private void UpdateBattery()
    {
        var power = _services.Power;
        BatteryStatus.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        Battery.Percent = power.Percent;
        Battery.IsCharging = power.IsCharging;
        BatteryText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(power.Percent, 0, 100)}%");
    }

    /// <summary>"☁ 26°" before the clock while weather is on.</summary>
    private void UpdateWeather()
    {
        var weather = _services.Weather;
        bool show = _services.Settings.Current.WeatherEnabled && weather.Temperature is not null;
        WeatherStatus.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            WeatherGlyph.Text = weather.Glyph();
            WeatherText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(weather.Temperature!.Value)}°");
        }
    }

    private void OnTopRowClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TopRow))
        {
            e.Handled = true;
            CollapseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
