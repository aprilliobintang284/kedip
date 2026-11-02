using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using Kedip.Core;

namespace Kedip.DockBar;

/// <summary>A Wi-Fi network in range (the strongest access point of its name).</summary>
internal sealed record WifiNetwork(string Ssid, int Bars, bool Secured, WiFiAvailableNetwork Network);

/// <summary>Listing, joining and leaving Wi-Fi networks (Windows.Devices.WiFi).</summary>
internal static class WifiControl
{
    /// <summary>The first Wi-Fi adapter, or null with the reason (Windows may withhold networks from desktop apps without location access).</summary>
    public static async Task<(WiFiAdapter? Adapter, string? Problem)> GetAdapterAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
            {
                return (null, access.ToString());
            }

            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            return adapters.Count > 0 ? (adapters[0], null) : (null, "NoAdapter");
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi adapter unavailable: {ex.Message}");
            return (null, ex.Message);
        }
    }

    public static async Task ScanAsync(WiFiAdapter adapter)
    {
        try
        {
            await adapter.ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi scan failed: {ex.Message}");
        }
    }

    public static List<WifiNetwork> Networks(WiFiAdapter adapter)
    {
        try
        {
            return adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars, IsSecured(n), n))
                .OrderByDescending(n => n.Bars)
                .ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi report unreadable: {ex.Message}");
            return [];
        }
    }

    public static async Task<string?> ConnectedSsidAsync(WiFiAdapter adapter)
    {
        try
        {
            var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
            return profile?.WlanConnectionProfileDetails?.GetConnectedSsid() ?? profile?.ProfileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Joins a network: with a saved profile no password is needed; <c>InvalidCredential</c> means "ask for one".</summary>
    public static async Task<WiFiConnectionStatus> ConnectAsync(WiFiAdapter adapter, WifiNetwork network, bool automatic, string? password)
    {
        var kind = automatic ? WiFiReconnectionKind.Automatic : WiFiReconnectionKind.Manual;
        try
        {
            var result = password is null
                ? await adapter.ConnectAsync(network.Network, kind)
                : await adapter.ConnectAsync(network.Network, kind, new PasswordCredential { Password = password });
            return result.ConnectionStatus;
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi connect to {network.Ssid} failed: {ex.Message}");
            return WiFiConnectionStatus.UnspecifiedFailure;
        }
    }

    public static void Disconnect(WiFiAdapter adapter)
    {
        try
        {
            adapter.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi disconnect failed: {ex.Message}");
        }
    }

    private static bool IsSecured(WiFiAvailableNetwork network) =>
        network.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211
            or NetworkAuthenticationType.None or NetworkAuthenticationType.Owe);
}

internal enum BtKind { Audio, Keyboard, Mouse, Gamepad, Phone, Computer, Other }

/// <summary>A paired Bluetooth device (classic and LE halves of one gadget are merged by container).</summary>
internal sealed record BtDevice(string Name, BtKind Kind, bool Connected, Guid? Container);

/// <summary>Paired Bluetooth devices and whether they are connected.</summary>
internal static class BluetoothDevices
{
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string ContainerId = "System.Devices.Aep.ContainerId";
    private const string CodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";

    public static async Task<List<BtDevice>> ListAsync()
    {
        var devices = new List<BtDevice>();
        try
        {
            string[] properties = [IsConnected, ContainerId, CodMajor, CodMinor, Appearance];
            foreach (string selector in new[]
                     {
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                     })
            {
                foreach (var info in await DeviceInformation.FindAllAsync(selector, properties, DeviceInformationKind.AssociationEndpoint))
                {
                    if (string.IsNullOrWhiteSpace(info.Name))
                    {
                        continue;
                    }

                    bool connected = info.Properties.TryGetValue(IsConnected, out object? c) && c is true;
                    Guid? container = info.Properties.TryGetValue(ContainerId, out object? g) && g is Guid id && id != Guid.Empty ? id : null;
                    var device = new BtDevice(info.Name, KindOf(info), connected, container);
                    int same = container is null ? -1 : devices.FindIndex(d => d.Container == container);
                    if (same >= 0)
                    {
                        var other = devices[same];
                        devices[same] = other with
                        {
                            Connected = other.Connected || connected,
                            Kind = other.Kind == BtKind.Other ? device.Kind : other.Kind,
                        };
                    }
                    else
                    {
                        devices.Add(device);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Bluetooth devices unavailable: {ex.Message}");
        }

        return devices.OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static int Glyph(BtKind kind) => kind switch
    {
        BtKind.Audio => 0xE7F6,
        BtKind.Keyboard => 0xE765,
        BtKind.Mouse => 0xE962,
        BtKind.Gamepad => 0xE7FC,
        BtKind.Phone => 0xE8EA,
        BtKind.Computer => 0xE770,
        _ => 0xE702,
    };

    private static BtKind KindOf(DeviceInformation info)
    {
        // Classic: Class of Device. LE: GAP appearance (category = value >> 6).
        if (info.Properties.TryGetValue(CodMajor, out object? major) && major is not null)
        {
            int minor = info.Properties.TryGetValue(CodMinor, out object? m) && m is not null ? Convert.ToInt32(m) : 0;
            switch (Convert.ToInt32(major))
            {
                case 1:
                    return BtKind.Computer;
                case 2:
                    return BtKind.Phone;
                case 4:
                    return BtKind.Audio;
                case 5:
                    return (minor & 0x30) switch
                    {
                        0x10 => BtKind.Keyboard,
                        0x20 or 0x30 => BtKind.Mouse,
                        _ => (minor & 0x0F) is 1 or 2 ? BtKind.Gamepad : BtKind.Other,
                    };
            }
        }

        if (info.Properties.TryGetValue(Appearance, out object? a) && a is not null)
        {
            int appearance = Convert.ToInt32(a);
            switch (appearance >> 6)
            {
                case 1:
                    return BtKind.Phone;
                case 2:
                    return BtKind.Computer;
                case 15:
                    return (appearance & 0x3F) switch
                    {
                        1 => BtKind.Keyboard,
                        2 => BtKind.Mouse,
                        3 or 4 => BtKind.Gamepad,
                        _ => BtKind.Other,
                    };
                case 37 or 33 or 34:
                    return BtKind.Audio;
            }
        }

        string name = info.Name.ToLowerInvariant();
        return new[] { "buds", "headphone", "headset", "airpods", "speaker", "earbud", "wh-", "wf-", "jbl" }.Any(name.Contains)
            ? BtKind.Audio
            : BtKind.Other;
    }
}
