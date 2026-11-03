using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Kedip.Core;
using Kedip.Power;
using Kedip.Productivity;
using static Kedip.DockBar.WinKeys;

namespace Kedip.DockBar;

/// <summary>
/// Kedip's own quick settings: split tiles (toggle + a chevron that opens the Wi-Fi / Bluetooth page), volume with an
/// output picker, brightness and the battery. Right-click a tile for its Windows settings page.
/// </summary>
public partial class ControlPanelWindow : GlassPanel
{
    private readonly AppServices _services;
    private readonly SystemVolume _volume;
    private readonly DispatcherTimer _brightnessDebounce;
    private readonly Tile _wifiTile;
    private readonly Tile _bluetoothTile;
    private readonly Tile _planeTile;
    private readonly Tile _dndTile;
    private readonly Tile _focusTile;
    private readonly Tile _saverTile;
    private readonly Tile _nightTile;
    private readonly Tile _hotspotTile;
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _syncing;
    private int _pendingBrightness = -1;

    internal ControlPanelWindow(AppServices services, SystemVolume volume)
    {
        _services = services;
        _volume = volume;
        InitializeComponent();

        _wifiTile = AddTile(0xE701, "Wi-Fi", "ms-settings:network-wifi", () => _ = ToggleRadioAsync(_wifi, "ms-availablenetworks:"), ShowWifiPage);
        _bluetoothTile = AddTile(0xE702, "Bluetooth", "ms-settings:bluetooth", () => _ = ToggleRadioAsync(_bluetooth, "ms-settings:bluetooth"),
            ShowBluetoothPage);
        _planeTile = AddTile(0xE709, "Mode pesawat", "ms-settings:network-airplanemode", () => _ = TogglePlaneAsync());
        _dndTile = AddTile(0xE708, "Jangan ganggu", null, () => _services.Settings.Update(s => s.DoNotDisturb = !s.DoNotDisturb));
        _focusTile = AddTile(0xE916, "Timer fokus", null, ToggleFocus);
        _saverTile = AddTile(0xEBB6, "Hemat daya", "ms-settings:powersleep", ToggleSaver);
        _nightTile = AddTile(0xF08C, "Lampu malam", "ms-settings:nightlight", ToggleNightLight);
        _hotspotTile = AddTile(0xE704, "Hotspot", "ms-settings:network-mobilehotspot", () => _ = ToggleHotspotAsync());
        AddTile(0xE7F4, "Proyeksikan", "ms-settings:display", () =>
        {
            CloseSmooth();
            Win(P);
        });

        _brightnessDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _brightnessDebounce.Tick += (_, _) => FlushBrightness();

        SettingsButton.Click += (_, _) => OpenAndClose("ms-settings:");
        KedipSettingsButton.Click += (_, _) => OpenKedipSettings();
        BatteryButton.Click += (_, _) => OpenAndClose("ms-settings:batterysaver");
        MuteButton.Click += (_, _) => ToggleMute();
        OutputButton.Click += (_, _) => ShowSoundPage();
        VolumeSlider.ValueChanged += OnVolumeSliderChanged;
        BrightnessSlider.ValueChanged += OnBrightnessSliderChanged;
        VolumeRow.ContextMenu = SettingsMenu("ms-settings:sound", "Buka pengaturan suara");
        BrightnessRow.ContextMenu = SettingsMenu("ms-settings:display", "Buka pengaturan layar");
        DetailBack.Click += (_, _) => ShowMain();
        DetailSwitch.Click += (_, _) => _detailSwitch?.Invoke(DetailSwitch.IsChecked == true);
        DetailMore.Click += (_, _) => OpenAndClose(_detailMore);

        _services.Settings.Changed += OnServicesChanged;
        _services.Pomodoro.Changed += OnServicesChanged;
        _services.Power.Changed += OnServicesChanged;
        Loaded += OnLoaded;
        Closed += OnPanelClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVolume();
        UpdateLocalTiles();
        UpdateBattery();
        _ = LoadBrightnessAsync();
        _ = UpdateSystemTilesAsync();

        (_wifi, _bluetooth) = await RadioControl.GetAsync();
        if (_wifi is not null)
        {
            _wifi.StateChanged += OnRadioStateChanged;
        }

        if (_bluetooth is not null)
        {
            _bluetooth.StateChanged += OnRadioStateChanged;
        }

        UpdateRadioTiles();
        _ = UpdateBluetoothLabelAsync();
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        FlushBrightness();
        _detailTimer?.Stop();
        _services.Settings.Changed -= OnServicesChanged;
        _services.Pomodoro.Changed -= OnServicesChanged;
        _services.Power.Changed -= OnServicesChanged;
        if (_wifi is not null)
        {
            _wifi.StateChanged -= OnRadioStateChanged;
        }

        if (_bluetooth is not null)
        {
            _bluetooth.StateChanged -= OnRadioStateChanged;
        }
    }

    // ---- Tiles ---------------------------------------------------------------------------------------------------

    /// <summary>A quick-settings tile: a rounded button (split in two when it has a page) with its label underneath.</summary>
    private sealed class Tile
    {
        public required StackPanel Root { get; init; }
        public required Border Plate { get; init; }
        public required Button Main { get; init; }
        public Button? More { get; init; }
        public Rectangle? Split { get; init; }
        public required TextBlock Label { get; init; }
        public required string Title { get; init; }
    }

    private Tile AddTile(int glyph, string title, string? settingsPage, Action toggle, Action? more = null)
    {
        var plate = new Border
        {
            Height = 50,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
        };
        var grid = new Grid();
        var main = new Button
        {
            Style = (Style)FindResource("TilePart"),
            Content = new TextBlock { Text = Glyph(glyph), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16 },
            Tag = more is null ? new CornerRadius(13) : new CornerRadius(13, 0, 0, 13),
            ToolTip = title,
        };
        AutomationProperties.SetName(main, title);
        main.Click += (_, _) => toggle();
        grid.Children.Add(main);

        Button? moreButton = null;
        Rectangle? split = null;
        if (more is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            split = new Rectangle { Width = 1, Margin = new Thickness(0, 11, 0, 11) };
            Grid.SetColumn(split, 1);
            moreButton = new Button
            {
                Style = (Style)FindResource("TilePart"),
                Content = new TextBlock { Text = Glyph(0xE76C), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 },
                Tag = new CornerRadius(0, 13, 13, 0),
                ToolTip = $"Pengaturan {title} lainnya",
            };
            AutomationProperties.SetName(moreButton, title + " lainnya");
            Grid.SetColumn(moreButton, 2);
            moreButton.Click += (_, _) => more();
            grid.Children.Add(split);
            grid.Children.Add(moreButton);
        }

        plate.Child = grid;
        var label = new TextBlock
        {
            Text = title,
            FontSize = 12,
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 104,
        };
        var root = new StackPanel { Width = 106, Margin = new Thickness(5, 4, 5, 6), Background = Brushes.Transparent };
        root.Children.Add(plate);
        root.Children.Add(label);
        root.ContextMenu = settingsPage is null
            ? KedipMenu()
            : SettingsMenu(settingsPage, "Buka Pengaturan");
        Tiles.Children.Add(root);

        var tile = new Tile { Root = root, Plate = plate, Main = main, More = moreButton, Split = split, Label = label, Title = title };
        SetState(tile, false, title);
        return tile;
    }

    /// <summary>On = filled with the accent (dark glyphs), like Windows; the label can carry a status (network name, time left).</summary>
    private void SetState(Tile tile, bool on, string label)
    {
        tile.Label.Text = label;
        tile.Plate.SetResourceReference(Border.BackgroundProperty, on ? "DockAccentBrush" : "PanelCard");
        tile.Plate.SetResourceReference(Border.BorderBrushProperty, on ? "DockAccentBrush" : "PanelCardEdge");
        string foreground = on ? "DockOnAccentBrush" : "TextPrimaryBrush";
        tile.Main.SetResourceReference(ForegroundProperty, foreground);
        tile.More?.SetResourceReference(ForegroundProperty, foreground);
        tile.Split?.SetValue(Shape.FillProperty, on ? new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    }

    private ContextMenu SettingsMenu(string page, string text)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = text, Tag = Glyph(0xE713) };
        item.Click += (_, _) => OpenAndClose(page);
        menu.Items.Add(item);
        return menu;
    }

    private ContextMenu KedipMenu()
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Buka pengaturan Kedip", Tag = Glyph(0xE713) };
        item.Click += (_, _) => OpenKedipSettings();
        menu.Items.Add(item);
        return menu;
    }

    private void UpdateRadioTiles()
    {
        bool wifiOn = _wifi?.State == RadioState.On;
        bool btOn = _bluetooth?.State == RadioState.On;
        SetState(_wifiTile, wifiOn, wifiOn ? RadioControl.ConnectedNetworkName() ?? "Wi-Fi" : "Wi-Fi");
        SetState(_bluetoothTile, btOn, btOn && _bluetoothLabel is not null ? _bluetoothLabel : "Bluetooth");
        bool plane = (_wifi is not null || _bluetooth is not null) && !wifiOn && !btOn;
        SetState(_planeTile, plane, "Mode pesawat");
        if (_page == Page.Wifi || _page == Page.Bluetooth)
        {
            SyncDetailSwitch(_page == Page.Wifi ? wifiOn : btOn);
        }
    }

    /// <summary>Power mode, night light and hotspot: read their real state (the hotspot query can take a moment).</summary>
    private async Task UpdateSystemTilesAsync()
    {
        SetState(_saverTile, PowerMode.IsEfficient(), "Hemat daya");
        SetState(_nightTile, NightLight.IsOn() == true, "Lampu malam");
        bool hotspot = await Task.Run(() => Hotspot.IsOn() == true);
        SetState(_hotspotTile, hotspot, "Hotspot");
    }

    private void ToggleSaver()
    {
        bool on = !PowerMode.IsEfficient();
        if (!PowerMode.SetEfficient(on))
        {
            OpenAndClose("ms-settings:powersleep");
            return;
        }

        SetState(_saverTile, PowerMode.IsEfficient(), "Hemat daya");
    }

    private void ToggleNightLight()
    {
        if (NightLight.IsOn() is not { } on || !NightLight.Set(!on))
        {
            // Unknown layout of Windows' setting: let Settings do it.
            OpenAndClose("ms-settings:nightlight");
            return;
        }

        SetState(_nightTile, !on, "Lampu malam");
    }

    private async Task ToggleHotspotAsync()
    {
        bool on = await Task.Run(() => Hotspot.IsOn() == true);
        SetState(_hotspotTile, !on, on ? "Mematikan…" : "Menyalakan…");
        string? problem = await Hotspot.SetAsync(!on);
        if (problem is not null)
        {
            Log.Warn($"Hotspot: {problem}");
            OpenAndClose("ms-settings:network-mobilehotspot");
            return;
        }

        await UpdateSystemTilesAsync();
    }

    private void UpdateLocalTiles()
    {
        SetState(_dndTile, _services.Settings.Current.DoNotDisturb, "Jangan ganggu");
        var pomodoro = _services.Pomodoro;
        SetState(_focusTile, pomodoro.IsRunning,
            pomodoro.Phase == PomodoroPhase.Idle ? "Timer fokus"
            : $"{(int)pomodoro.Remaining.TotalMinutes:00}:{pomodoro.Remaining.Seconds:00}" + (pomodoro.IsRunning ? "" : " · jeda"));
    }

    private async Task ToggleRadioAsync(Radio? radio, string fallback)
    {
        if (radio is null)
        {
            OpenAndClose(fallback);
            return;
        }

        await RadioControl.SetAsync(radio, radio.State != RadioState.On);
        UpdateRadioTiles();
    }

    private async Task TogglePlaneAsync()
    {
        if (_wifi is null && _bluetooth is null)
        {
            OpenAndClose("ms-settings:network-airplanemode");
            return;
        }

        bool anyOn = _wifi?.State == RadioState.On || _bluetooth?.State == RadioState.On;
        foreach (var radio in new[] { _wifi, _bluetooth })
        {
            if (radio is not null)
            {
                await RadioControl.SetAsync(radio, !anyOn);
            }
        }

        UpdateRadioTiles();
    }

    private void ToggleFocus()
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

        UpdateLocalTiles();
    }

    private void OnRadioStateChanged(Radio sender, object args) => Dispatcher.InvokeAsync(() =>
    {
        UpdateRadioTiles();
        if (_page == Page.Wifi)
        {
            _ = RefreshWifiAsync(scan: true);
        }
        else if (_page == Page.Bluetooth)
        {
            _ = RefreshBluetoothAsync();
        }
    });

    private void OnServicesChanged(object? sender, EventArgs e)
    {
        UpdateLocalTiles();
        UpdateBattery();
    }

    // ---- Pages ---------------------------------------------------------------------------------------------------

    private enum Page { Main, Wifi, Bluetooth, Sound }

    private Page _page = Page.Main;
    private Action<bool>? _detailSwitch;
    private string _detailMore = "ms-settings:";
    private DispatcherTimer? _detailTimer;

    /// <summary>Slides in the detail page; <paramref name="switchOn"/> null hides the header switch.</summary>
    private void OpenDetail(Page page, string title, bool? switchOn, Action<bool>? onSwitch, string moreText, string moreTarget)
    {
        _page = page;
        DetailTitle.Text = title;
        DetailSwitch.Visibility = switchOn is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(DetailSwitch, title);
        SyncDetailSwitch(switchOn == true);
        _detailSwitch = onSwitch;
        DetailMore.Content = moreText;
        _detailMore = moreTarget;
        DetailStatus.Text = "";
        DetailList.Children.Clear();
        DetailScroll.ScrollToTop();
        Slide(MainPage, DetailPage, forward: true);
    }

    private void ShowMain()
    {
        _page = Page.Main;
        _detailTimer?.Stop();
        Slide(DetailPage, MainPage, forward: false);
        UpdateRadioTiles();
        UpdateVolume();
    }

    private void SyncDetailSwitch(bool on)
    {
        _syncing = true;
        DetailSwitch.IsChecked = on;
        _syncing = false;
    }

    private void Slide(FrameworkElement from, FrameworkElement to, bool forward)
    {
        from.Visibility = Visibility.Collapsed;
        to.Visibility = Visibility.Visible;
        if (!_services.Settings.Current.Animations)
        {
            return;
        }

        var shift = new TranslateTransform(forward ? 36 : -36, 0);
        to.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>Re-reads the open page every few seconds (connections change on their own).</summary>
    private void RepeatWhileOpen(Page page, Func<Task> refresh, TimeSpan every)
    {
        _detailTimer?.Stop();
        _detailTimer = new DispatcherTimer { Interval = every };
        _detailTimer.Tick += async (_, _) =>
        {
            if (_page == page && IsVisible)
            {
                await refresh();
            }
        };
        _detailTimer.Start();
    }

    /// <summary>One list row: glyph, title, subtitle; optional extra content below while it is the selected one.</summary>
    private UIElement Row(UIElement icon, string title, string subtitle, bool selected, Action click, UIElement? expanded = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var head = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(text);
        var button = new Button { Style = (Style)FindResource("PanelRow"), Content = head, Tag = selected && expanded is null ? "on" : "off" };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => click();
        if (expanded is null)
        {
            return button;
        }

        var box = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
        box.Children.Add(button);
        if (expanded is FrameworkElement fe)
        {
            fe.Margin = new Thickness(46, 0, 10, 10);
        }

        box.Children.Add(expanded);
        return new Border { CornerRadius = new CornerRadius(12), Child = box, ClipToBounds = true, Margin = new Thickness(0, 1, 0, 1) };
    }

    private TextBlock IconText(int glyph, double size = 16) => new()
    {
        Text = Glyph(glyph),
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = size,
        Width = 24,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- Volume & brightness -------------------------------------------------------------------------------------

    private void UpdateVolume()
    {
        if (_volume.Read() is not { } volume)
        {
            VolumeSlider.IsEnabled = false;
            return;
        }

        _syncing = true;
        VolumeSlider.Value = Math.Round(volume.Level * 100);
        _syncing = false;
        ShowVolume(volume.Level, volume.Muted);
    }

    private void ShowVolume(float level, bool muted)
    {
        VolumeValue.Text = muted ? "Bisu" : $"{Math.Round(level * 100)}";
        MuteButton.Content = Glyph(muted || level < 0.005 ? 0xE74F : level < 0.34 ? 0xE993 : level < 0.67 ? 0xE994 : 0xE995);
        MuteButton.ToolTip = muted ? "Nyalakan suara" : "Bisukan";
    }

    private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
        {
            return;
        }

        float level = (float)(e.NewValue / 100);
        _volume.SetLevel(level);
        if (_volume.Read() is { } now && now.Muted && level > 0)
        {
            _volume.SetMuted(false);
        }

        ShowVolume(level, false);
    }

    private void ToggleMute()
    {
        if (_volume.Read() is { } volume)
        {
            _volume.SetMuted(!volume.Muted);
            ShowVolume(volume.Level, !volume.Muted);
        }
    }

    private async Task LoadBrightnessAsync()
    {
        int? level = await Task.Run(BrightnessControl.Read);
        if (level is null)
        {
            return;
        }

        _syncing = true;
        BrightnessSlider.Value = level.Value;
        _syncing = false;
        BrightnessValue.Text = level.Value.ToString();
        BrightnessRow.Visibility = Visibility.Visible;
    }

    private void OnBrightnessSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
        {
            return;
        }

        _pendingBrightness = (int)Math.Round(e.NewValue);
        BrightnessValue.Text = _pendingBrightness.ToString();
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start();
    }

    private void FlushBrightness()
    {
        _brightnessDebounce.Stop();
        if (_pendingBrightness < 0)
        {
            return;
        }

        int value = _pendingBrightness;
        _pendingBrightness = -1;
        _ = Task.Run(() => BrightnessControl.Set(value));
    }

    // ---- Misc ------------------------------------------------------------------------------------------------------

    private void UpdateBattery()
    {
        var power = _services.Power;
        if (!power.HasBattery)
        {
            BatteryGlyph.Text = Glyph(0xE83E);
            BatteryText.Text = "Daya listrik";
            return;
        }

        int level = Math.Clamp(power.Percent / 10, 0, 10);
        BatteryGlyph.Text = power.IsCharging ? Glyph(0xEBAB + level) : PowerService.BatteryGlyph(power.Percent);
        BatteryText.Text = $"{power.Percent}%" + (power.IsCharging ? " · mengisi daya" : "");
    }

    private void OpenAndClose(string target)
    {
        CloseSmooth();
        SystemActions.Open(target);
    }

    private void OpenKedipSettings()
    {
        CloseSmooth();
        (Application.Current as App)?.OpenSettings();
    }
}
