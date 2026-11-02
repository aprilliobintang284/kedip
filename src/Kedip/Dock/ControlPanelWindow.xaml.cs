using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Kedip.Core;
using Kedip.Power;
using Kedip.Productivity;
using static Kedip.DockBar.WinKeys;

namespace Kedip.DockBar;

/// <summary>Kedip's own quick settings: radios, Do Not Disturb, focus timer, shortcuts, volume and brightness.</summary>
public partial class ControlPanelWindow : GlassPanel
{
    private readonly AppServices _services;
    private readonly SystemVolume _volume;
    private readonly DispatcherTimer _brightnessDebounce;
    private readonly ToggleButton _wifiTile;
    private readonly ToggleButton _bluetoothTile;
    private readonly ToggleButton _planeTile;
    private readonly ToggleButton _dndTile;
    private readonly ToggleButton _focusTile;
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _syncing;
    private int _pendingBrightness = -1;

    internal ControlPanelWindow(AppServices services, SystemVolume volume)
    {
        _services = services;
        _volume = volume;
        InitializeComponent();

        _wifiTile = AddTile(0xE701, "Wi-Fi", t => _ = ToggleRadioAsync(_wifi, "ms-availablenetworks:"));
        _bluetoothTile = AddTile(0xE702, "Bluetooth", t => _ = ToggleRadioAsync(_bluetooth, "ms-settings:bluetooth"));
        _planeTile = AddTile(0xE709, "Mode pesawat", t => _ = TogglePlaneAsync());
        _dndTile = AddTile(0xE708, "Jangan ganggu", _ => _services.Settings.Update(s => s.DoNotDisturb = !s.DoNotDisturb));
        _focusTile = AddTile(0xE916, "Timer fokus", _ => ToggleFocus());
        AddShortcut(0xE850, "Hemat baterai", "ms-settings:batterysaver");
        AddShortcut(0xE771, "Lampu malam", "ms-settings:nightlight");
        AddShortcut(0xE704, "Hotspot", "ms-settings:network-mobilehotspot");
        var project = AddTile(0xE7F4, "Proyeksikan", tile =>
        {
            tile.IsChecked = false;
            CloseSmooth();
            Win(P);
        });
        SetStatus(project, false, "Layar kedua");

        _brightnessDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _brightnessDebounce.Tick += (_, _) => FlushBrightness();

        SettingsButton.Click += (_, _) => OpenAndClose("ms-settings:");
        WifiListButton.Click += (_, _) => OpenAndClose("ms-availablenetworks:");
        BluetoothSettingsButton.Click += (_, _) => OpenAndClose("ms-settings:bluetooth");
        MuteButton.Click += (_, _) => ToggleMute();
        VolumeSlider.ValueChanged += OnVolumeSliderChanged;
        BrightnessSlider.ValueChanged += OnBrightnessSliderChanged;

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
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        FlushBrightness();
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

    private ToggleButton AddTile(int glyph, string title, Action<ToggleButton> click)
    {
        var status = new TextBlock { FontSize = 11, Opacity = 0.78, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel { Width = 90 };
        content.Children.Add(new TextBlock { Text = Glyph(glyph), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 17 });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 7, 0, 1),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(status);

        var tile = new ToggleButton { Style = (Style)FindResource("QuickTile"), Content = content, Tag = status, ToolTip = title };
        tile.Click += (_, _) => click(tile);
        Tiles.Children.Add(tile);
        return tile;
    }

    private void AddShortcut(int glyph, string title, string settingsPage)
    {
        var tile = AddTile(glyph, title, t =>
        {
            t.IsChecked = false;
            OpenAndClose(settingsPage);
        });
        SetStatus(tile, false, "Buka pengaturan");
    }

    private static void SetStatus(ToggleButton tile, bool on, string text)
    {
        tile.IsChecked = on;
        ((TextBlock)tile.Tag).Text = text;
    }

    private void UpdateRadioTiles()
    {
        bool wifiOn = _wifi?.State == RadioState.On;
        bool btOn = _bluetooth?.State == RadioState.On;
        SetStatus(_wifiTile, wifiOn, _wifi is null ? "Pengaturan" : wifiOn ? RadioControl.ConnectedNetworkName() ?? "Nyala" : "Mati");
        SetStatus(_bluetoothTile, btOn, _bluetooth is null ? "Pengaturan" : btOn ? "Nyala" : "Mati");
        bool plane = (_wifi is not null || _bluetooth is not null) && !wifiOn && !btOn;
        SetStatus(_planeTile, plane, plane ? "Semua radio mati" : "Mati");
    }

    private void UpdateLocalTiles()
    {
        bool dnd = _services.Settings.Current.DoNotDisturb;
        SetStatus(_dndTile, dnd, dnd ? "Notifikasi ditahan" : "Mati");
        var pomodoro = _services.Pomodoro;
        SetStatus(_focusTile, pomodoro.IsRunning,
            pomodoro.Phase == PomodoroPhase.Idle ? "Mulai 25 menit"
            : $"{(int)pomodoro.Remaining.TotalMinutes:00}:{pomodoro.Remaining.Seconds:00}" + (pomodoro.IsRunning ? "" : " · dijeda"));
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

    private void OnRadioStateChanged(Radio sender, object args) => Dispatcher.InvokeAsync(UpdateRadioTiles);

    private void OnServicesChanged(object? sender, EventArgs e)
    {
        UpdateLocalTiles();
        UpdateBattery();
    }

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
}
