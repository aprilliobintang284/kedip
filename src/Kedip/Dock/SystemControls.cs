using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Windows.Devices.Radios;
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
