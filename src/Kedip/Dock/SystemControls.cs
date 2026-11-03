using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Kedip.Core;

namespace Kedip.DockBar;

/// <summary>Wi-Fi / Bluetooth radios via Windows.Devices.Radios (null when not allowed or not present).</summary>
internal static class RadioControl
{
    public static async Task<(Radio? Wifi, Radio? Bluetooth)> GetAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                return (null, null);
            }

            var radios = await Radio.GetRadiosAsync();
            return (radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi), radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth));
        }
        catch (Exception ex)
        {
            Log.Warn($"Radios unavailable: {ex.Message}");
            return (null, null);
        }
    }

    public static async Task SetAsync(Radio radio, bool on)
    {
        try
        {
            await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not switch {radio.Kind}: {ex.Message}");
        }
    }

    /// <summary>Name of the Wi-Fi network the PC uses for internet, or null.</summary>
    public static string? ConnectedNetworkName()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            return profile is { IsWlanConnectionProfile: true } ? profile.ProfileName : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Built-in display brightness via WMI (laptop panels). Slow-ish: call off the UI thread.</summary>
internal static class BrightnessControl
{
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"]);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var methods = new ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null);
            foreach (ManagementObject item in methods.GetInstances())
            {
                using (item)
                {
                    item.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Log.Warn($"Brightness change failed: {ex.Message}");
        }
    }
}

/// <summary>Opening system places and the session/power actions of the quick menu.</summary>
internal static class SystemActions
{
    /// <summary>Starts a program, settings page (ms-settings:…) or console (.msc). Cancelling a UAC prompt is fine.</summary>
    public static void Open(string target, string? arguments = null, bool admin = false)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments is not null)
            {
                info.Arguments = arguments;
            }

            if (admin)
            {
                info.Verb = "runas";
            }

            Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to the elevation prompt.
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void SignOut() => ExitWindowsEx(EWX_LOGOFF, 0);

    public static void Restart() => RunHidden("shutdown.exe", "/r /t 0");

    public static void ShutDown() => RunHidden("shutdown.exe", "/s /t 0");

    private static void RunHidden(string exe, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"{exe} {arguments} failed: {ex.Message}");
        }
    }

    private const uint EWX_LOGOFF = 0;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

/// <summary>
/// Windows' power mode (Settings › Power): "best power efficiency" saves battery on battery and charger alike. Windows
/// gives apps no switch for Energy saver itself while plugged in, so this is what the "Hemat daya" tile turns on.
/// </summary>
internal static class PowerMode
{
    private static readonly Guid Efficiency = new("961CC777-2547-4F9D-8174-7D86181B8A7A");
    private static Guid _before = Guid.Empty; // Balanced

    public static bool IsEfficient() => PowerGetEffectiveOverlayScheme(out Guid mode) == 0 && mode == Efficiency;

    /// <summary>On: best power efficiency. Off: back to the mode that was set before (normally Balanced).</summary>
    public static bool SetEfficient(bool on)
    {
        if (on)
        {
            if (PowerGetActualOverlayScheme(out Guid current) == 0 && current != Efficiency)
            {
                _before = current;
            }

            return PowerSetActiveOverlayScheme(Efficiency) == 0;
        }

        return PowerSetActiveOverlayScheme(_before == Efficiency ? Guid.Empty : _before) == 0;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActualOverlayScheme(out Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid mode);
}

/// <summary>
/// Night light, switched through the state Windows keeps for it in the user's CloudStore (the Settings app writes the
/// same value; Windows applies a change within a second). Only the known layout is touched; anything else returns
/// null / false so the caller can open Settings instead.
/// </summary>
internal static class NightLight
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\"
        + @"default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    private const int StateByte = 18;
    private const byte Off = 0x13;
    private const byte On = 0x15;
    private const int OnFlagAt = 23;
    private const int TimestampAt = 10;

    public static bool? IsOn() => Read() is { Length: > 25 } data ? data[StateByte] switch { On => true, Off => false, _ => null } : null;

    public static bool Set(bool on)
    {
        if (Read() is not { Length: > 25 } data || data[StateByte] is not (On or Off))
        {
            return false;
        }

        if ((data[StateByte] == On) == on)
        {
            return true;
        }

        byte[] next;
        if (on)
        {
            // Turning it on adds a two-byte field (0x10 0x00) and lengthens the record by two.
            next = [.. data[..OnFlagAt], 0x10, 0x00, .. data[OnFlagAt..]];
            next[StateByte] = On;
        }
        else
        {
            if (data[OnFlagAt] != 0x10 || data[OnFlagAt + 1] != 0x00)
            {
                return false;
            }

            next = [.. data[..OnFlagAt], .. data[(OnFlagAt + 2)..]];
            next[StateByte] = Off;
        }

        if (!BumpTimestamp(next))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("Data", next, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn($"Night light could not be switched: {ex.Message}");
            return false;
        }
    }

    private static byte[]? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("Data") as byte[];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The change time (a varint) must move forward for Windows to pick the new state up; same length only.</summary>
    private static bool BumpTimestamp(byte[] data)
    {
        ulong value = 0;
        int shift = 0;
        int i = TimestampAt;
        for (; i < data.Length && i < TimestampAt + 10; i++)
        {
            value |= (ulong)(data[i] & 0x7F) << shift;
            shift += 7;
            if ((data[i] & 0x80) == 0)
            {
                break;
            }
        }

        int length = i - TimestampAt + 1;
        value++;
        if (length > 9 || value >> (7 * length) != 0)
        {
            return false;
        }

        for (int k = 0; k < length; k++)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            data[TimestampAt + k] = k < length - 1 ? (byte)(b | 0x80) : b;
        }

        return true;
    }
}

/// <summary>Mobile hotspot (sharing this PC's internet), through Windows' tethering manager.</summary>
internal static class Hotspot
{
    public static bool? IsOn()
    {
        try
        {
            return Manager()?.TetheringOperationalState switch
            {
                TetheringOperationalState.On or TetheringOperationalState.InTransition => true,
                TetheringOperationalState.Off => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Null when it worked, else a short reason.</summary>
    public static async Task<string?> SetAsync(bool on)
    {
        try
        {
            if (Manager() is not { } manager)
            {
                return "Tidak ada koneksi internet untuk dibagikan";
            }

            var result = on ? await manager.StartTetheringAsync() : await manager.StopTetheringAsync();
            return result.Status == TetheringOperationStatus.Success ? null : result.Status.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn($"Hotspot could not be switched: {ex.Message}");
            return ex.Message;
        }
    }

    private static NetworkOperatorTetheringManager? Manager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        return profile is null ? null : NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }
}
