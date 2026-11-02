using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using static Kedip.DockBar.WinKeys;

namespace Kedip.DockBar;

/// <summary>The control panel's detail pages: Wi-Fi networks, Bluetooth devices and the sound output.</summary>
public partial class ControlPanelWindow
{
    private WiFiAdapter? _wifiAdapter;
    private List<WifiNetwork> _wifiNetworks = [];
    private string? _wifiConnected;
    private string? _wifiSelected;
    private string? _wifiAsking;
    private string? _wifiMessage;
    private bool _wifiBusy;

    private List<BtDevice> _btDevices = [];
    private string? _btSelected;
    private string? _btMessage;
    private bool _btBusy;
    private string? _bluetoothLabel;

    private async Task SetRadioAsync(Radio? radio, bool on)
    {
        if (radio is not null)
        {
            await RadioControl.SetAsync(radio, on);
        }

        UpdateRadioTiles();
    }

    // ---- Wi-Fi ---------------------------------------------------------------------------------------------------

    private void ShowWifiPage()
    {
        OpenDetail(Page.Wifi, "Wi-Fi", _wifi is null ? null : _wifi.State == RadioState.On, on => _ = SetRadioAsync(_wifi, on),
            "Pengaturan Wi-Fi lainnya", "ms-settings:network-wifi");
        _wifiSelected = null;
        _wifiAsking = null;
        _wifiMessage = null;
        _ = RefreshWifiAsync(scan: true);
        RepeatWhileOpen(Page.Wifi, () => RefreshWifiAsync(scan: false), TimeSpan.FromSeconds(5));
    }

    private async Task RefreshWifiAsync(bool scan)
    {
        if (_wifi is not null && _wifi.State != RadioState.On)
        {
            DetailStatus.Text = "Wi-Fi mati. Nyalakan dengan sakelar di atas.";
            DetailList.Children.Clear();
            return;
        }

        if (_wifiAdapter is null)
        {
            (_wifiAdapter, string? problem) = await WifiControl.GetAdapterAsync();
            if (_wifiAdapter is null)
            {
                ShowWifiBlocked(problem == "NoAdapter" ? "Tidak ada adaptor Wi-Fi." : null);
                return;
            }
        }

        if (scan)
        {
            if (DetailList.Children.Count == 0)
            {
                DetailStatus.Text = "Mencari jaringan…";
            }

            await WifiControl.ScanAsync(_wifiAdapter);
        }

        var networks = WifiControl.Networks(_wifiAdapter);
        string? connected = await WifiControl.ConnectedSsidAsync(_wifiAdapter);
        if (_page != Page.Wifi)
        {
            return;
        }

        _wifiConnected = connected;
        _wifiNetworks = networks.OrderByDescending(n => n.Ssid == connected).ToList();
        if (networks.Count == 0 && scan)
        {
            // Windows hides nearby networks from desktop apps that may not see the location.
            ShowWifiBlocked(null);
            return;
        }

        // Never rebuild under the user's fingers (password box, connecting).
        if (_wifiAsking is null && !_wifiBusy)
        {
            RenderWifi();
        }
    }

    private void ShowWifiBlocked(string? reason)
    {
        DetailStatus.Text = reason
            ?? "Windows tidak membagikan daftar Wi-Fi ke Kedip. Nyalakan Privasi › Lokasi › \"Izinkan aplikasi desktop mengakses lokasi\", atau pakai daftar Wi-Fi Windows.";
        DetailList.Children.Clear();
        var buttons = new WrapPanel { Margin = new Thickness(10, 4, 10, 6) };
        buttons.Children.Add(ActionButton("Daftar Wi-Fi Windows", accent: true, () => OpenAndClose("ms-availablenetworks:")));
        if (reason is null)
        {
            buttons.Children.Add(ActionButton("Pengaturan lokasi", accent: false, () => OpenAndClose("ms-settings:privacy-location")));
        }

        DetailList.Children.Add(buttons);
    }

    private void RenderWifi()
    {
        DetailStatus.Text = _wifiNetworks.Count == 0 ? "Tidak ada jaringan yang terlihat." : "";
        DetailList.Children.Clear();
        foreach (var network in _wifiNetworks)
        {
            bool isConnected = network.Ssid == _wifiConnected;
            bool selected = network.Ssid == _wifiSelected;
            string subtitle = isConnected
                ? network.Secured ? "Terhubung, aman" : "Terhubung, terbuka"
                : network.Secured ? "Aman" : "Terbuka";
            DetailList.Children.Add(Row(WifiIcon(network.Bars, network.Secured), network.Ssid, subtitle, isConnected,
                () => SelectWifi(network.Ssid), selected ? WifiActions(network, isConnected) : null));
        }
    }

    private void SelectWifi(string ssid)
    {
        if (_wifiBusy)
        {
            return;
        }

        _wifiSelected = _wifiSelected == ssid ? null : ssid;
        _wifiAsking = null;
        _wifiMessage = null;
        RenderWifi();
    }

    private UIElement WifiActions(WifiNetwork network, bool isConnected)
    {
        var panel = new StackPanel();
        if (_wifiMessage is { } message)
        {
            panel.Children.Add(Note(message));
        }

        if (isConnected)
        {
            panel.Children.Add(Right(ActionButton("Putuskan", accent: false, () => _ = DisconnectWifiAsync())));
            return panel;
        }

        if (_wifiAsking == network.Ssid)
        {
            panel.Children.Add(Note("Masukkan kunci keamanan jaringan"));
            var password = new PasswordBox { Style = (Style)FindResource("PanelPassword"), Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(password, "Kata sandi Wi-Fi");
            password.Loaded += (_, _) => Keyboard.Focus(password);
            void Submit()
            {
                if (password.Password.Length > 0)
                {
                    _ = ConnectWifiAsync(network, automatic: true, password.Password);
                }
            }

            password.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Submit();
                }
            };
            panel.Children.Add(password);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(ActionButton("Batal", accent: false, () =>
            {
                _wifiAsking = null;
                _wifiMessage = null;
                RenderWifi();
            }));
            var next = ActionButton("Berikutnya", accent: true, Submit);
            next.Margin = new Thickness(8, 0, 0, 0);
            next.IsEnabled = !_wifiBusy;
            row.Children.Add(next);
            panel.Children.Add(row);
            return panel;
        }

        var auto = new CheckBox { Style = (Style)FindResource("PanelCheck"), Content = "Hubungkan otomatis", IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(auto);
        var connect = ActionButton(_wifiBusy ? "Menghubungkan…" : "Hubungkan", accent: true,
            () => _ = ConnectWifiAsync(network, auto.IsChecked == true, null));
        connect.IsEnabled = !_wifiBusy;
        panel.Children.Add(Right(connect));
        return panel;
    }

    private async Task ConnectWifiAsync(WifiNetwork network, bool automatic, string? password)
    {
        if (_wifiAdapter is null || _wifiBusy)
        {
            return;
        }

        _wifiBusy = true;
        _wifiMessage = "Menghubungkan…";
        _wifiAsking = password is null ? null : network.Ssid;
        RenderWifi();
        var status = await WifiControl.ConnectAsync(_wifiAdapter, network, automatic, password);
        _wifiBusy = false;
        if (status == WiFiConnectionStatus.Success)
        {
            _wifiMessage = null;
            _wifiAsking = null;
            _wifiSelected = null;
            await RefreshWifiAsync(scan: false);
            UpdateRadioTiles();
            return;
        }

        // A secured network without a saved profile: ask for the key (also when Windows gives a vaguer failure).
        bool askKey = network.Secured && status is WiFiConnectionStatus.InvalidCredential or WiFiConnectionStatus.UnspecifiedFailure;
        _wifiAsking = askKey ? network.Ssid : null;
        _wifiMessage = status switch
        {
            WiFiConnectionStatus.InvalidCredential when password is not null => "Kata sandi salah. Coba lagi.",
            _ when askKey => password is null ? null : "Tidak bisa terhubung. Periksa kata sandinya.",
            WiFiConnectionStatus.NetworkNotAvailable => "Jaringan ini sudah tidak terjangkau.",
            WiFiConnectionStatus.Timeout => "Waktu habis. Coba lagi.",
            WiFiConnectionStatus.UnsupportedAuthenticationProtocol => "Jaringan ini perlu login khusus; sambungkan lewat pengaturan Wi-Fi.",
            WiFiConnectionStatus.AccessRevoked => "Windows menolak permintaan ini.",
            _ => "Tidak bisa terhubung.",
        };
        RenderWifi();
    }

    private async Task DisconnectWifiAsync()
    {
        if (_wifiAdapter is null)
        {
            return;
        }

        WifiControl.Disconnect(_wifiAdapter);
        _wifiSelected = null;
        await Task.Delay(900);
        await RefreshWifiAsync(scan: false);
        UpdateRadioTiles();
    }

    /// <summary>Signal bars over a dim full fan (as Windows draws them), with a small lock for secured networks.</summary>
    private Grid WifiIcon(int bars, bool secured)
    {
        var icons = (FontFamily)FindResource("IconFont");
        var grid = new Grid { Width = 24, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new TextBlock { Text = Glyph(0xEC3F), FontFamily = icons, FontSize = 17, Opacity = 0.28, HorizontalAlignment = HorizontalAlignment.Center });
        int glyph = bars switch { <= 1 => 0xEC3C, 2 => 0xEC3D, 3 => 0xEC3E, _ => 0xEC3F };
        grid.Children.Add(new TextBlock { Text = Glyph(glyph), FontFamily = icons, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center });
        if (secured)
        {
            grid.Children.Add(new TextBlock
            {
                Text = Glyph(0xE72E),
                FontFamily = icons,
                FontSize = 8.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }

        return grid;
    }

    // ---- Bluetooth -----------------------------------------------------------------------------------------------

    private void ShowBluetoothPage()
    {
        OpenDetail(Page.Bluetooth, "Bluetooth", _bluetooth is null ? null : _bluetooth.State == RadioState.On,
            on => _ = SetRadioAsync(_bluetooth, on), "Pengaturan Bluetooth lainnya", "ms-settings:bluetooth");
        _btSelected = null;
        _btMessage = null;
        _ = RefreshBluetoothAsync();
        RepeatWhileOpen(Page.Bluetooth, RefreshBluetoothAsync, TimeSpan.FromSeconds(3));
    }

    private async Task RefreshBluetoothAsync()
    {
        if (_bluetooth is not null && _bluetooth.State != RadioState.On)
        {
            DetailStatus.Text = "Bluetooth mati. Nyalakan dengan sakelar di atas.";
            DetailList.Children.Clear();
            return;
        }

        var devices = await BluetoothDevices.ListAsync();
        if (_page != Page.Bluetooth)
        {
            return;
        }

        _btDevices = devices;
        if (!_btBusy)
        {
            RenderBluetooth();
        }
    }

    private void RenderBluetooth()
    {
        DetailStatus.Text = _btDevices.Count == 0 ? "Belum ada perangkat yang dipasangkan." : "";
        DetailList.Children.Clear();
        foreach (var device in _btDevices)
        {
            bool selected = device.Name == _btSelected;
            DetailList.Children.Add(Row(IconText(BluetoothDevices.Glyph(device.Kind)), device.Name,
                device.Connected ? "Terhubung" : "Dipasangkan", device.Connected, () =>
                {
                    _btSelected = _btSelected == device.Name ? null : device.Name;
                    _btMessage = null;
                    RenderBluetooth();
                }, selected ? BluetoothActions(device) : null));
        }

        var add = new Button { Style = (Style)FindResource("PanelLink"), Margin = new Thickness(0, 4, 0, 0) };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = Glyph(0xE710), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Margin = new Thickness(6, 1, 14, 0) });
        label.Children.Add(new TextBlock { Text = "Tambah perangkat" });
        add.Content = label;
        add.Click += (_, _) => OpenAndClose("ms-settings-connectabledevices:devicediscovery");
        DetailList.Children.Add(add);
    }

    private UIElement BluetoothActions(BtDevice device)
    {
        var panel = new StackPanel();
        if (_btMessage is { } message)
        {
            panel.Children.Add(Note(message));
        }

        if (device.Kind == BtKind.Audio)
        {
            var button = ActionButton(_btBusy ? "Sebentar…" : device.Connected ? "Putuskan" : "Hubungkan", accent: !device.Connected,
                () => _ = SetBluetoothAudioAsync(device, !device.Connected));
            button.IsEnabled = !_btBusy;
            panel.Children.Add(Right(button));
        }
        else
        {
            panel.Children.Add(Right(ActionButton("Buka pengaturan", accent: false, () => OpenAndClose("ms-settings:bluetooth"))));
        }

        return panel;
    }

    private async Task SetBluetoothAudioAsync(BtDevice device, bool connect)
    {
        _btBusy = true;
        _btMessage = connect ? "Menghubungkan…" : "Memutuskan…";
        RenderBluetooth();
        bool sent = await Task.Run(() => AudioDevices.SetBluetoothConnected(device.Container, device.Name, connect));
        if (sent)
        {
            await Task.Delay(2500);
        }

        _btBusy = false;
        _btMessage = sent ? null : "Perangkat ini tidak bisa diatur dari sini. Buka pengaturan Bluetooth.";
        await RefreshBluetoothAsync();
        await UpdateBluetoothLabelAsync();
    }

    /// <summary>The Bluetooth tile shows the connected device's name, like Windows.</summary>
    private async Task UpdateBluetoothLabelAsync()
    {
        if (_bluetooth?.State != RadioState.On)
        {
            return;
        }

        var devices = await BluetoothDevices.ListAsync();
        _bluetoothLabel = devices.FirstOrDefault(d => d.Connected)?.Name;
        UpdateRadioTiles();
    }

    // ---- Sound output --------------------------------------------------------------------------------------------

    private void ShowSoundPage()
    {
        OpenDetail(Page.Sound, "Output suara", null, null, "Pengaturan suara lainnya", "ms-settings:sound");
        RenderSound();
    }

    private void RenderSound()
    {
        var outputs = AudioDevices.Outputs();
        DetailStatus.Text = outputs.Count == 0 ? "Tidak ada perangkat suara." : "";
        DetailList.Children.Clear();
        foreach (var output in outputs)
        {
            string name = output.Name.ToLowerInvariant();
            int glyph = name.Contains("headphone") || name.Contains("headset") || name.Contains("earphone") ? 0xE7F6
                : name.Contains("hdmi") || name.Contains("display") ? 0xE7F4
                : 0xE7F5;
            DetailList.Children.Add(Row(IconText(glyph), output.Name, output.IsDefault ? "Sedang dipakai" : "", output.IsDefault, () =>
            {
                if (!output.IsDefault && AudioDevices.SetDefault(output.Id))
                {
                    _volume.Refresh();
                    RenderSound();
                }
            }));
        }
    }

    // ---- Bits ------------------------------------------------------------------------------------------------------

    private Button ActionButton(string text, bool accent, Action click)
    {
        var button = new Button { Style = (Style)FindResource(accent ? "PanelAccentButton" : "PanelButton"), Content = text };
        button.Click += (_, _) => click();
        return button;
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("TextSecondaryBrush"),
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static FrameworkElement Right(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Right;
        return element;
    }
}
