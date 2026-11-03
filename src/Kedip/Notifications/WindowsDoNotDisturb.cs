using System.Runtime.InteropServices;
using Kedip.Core;

namespace Kedip.Notifications;

/// <summary>
/// Windows' own Do Not Disturb: notifications still reach the notification list, but without banners or sound.
/// (Not Kedip's "Jangan ganggu", which quiets the notch.) Uses the shell's quiet-hours settings object, the same one
/// the Windows notification centre's bell button switches.
/// </summary>
public static class WindowsDoNotDisturb
{
    private const string Off = "Microsoft.QuietHoursProfile.Unrestricted";
    private const string On = "Microsoft.QuietHoursProfile.PriorityOnly";
    private static readonly Guid SettingsClsid = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");

    /// <summary>True when on (priority only or alarms only), false when off, null when Windows does not say.</summary>
    public static bool? IsOn() => Profile() is { } profile ? !string.Equals(profile, Off, StringComparison.OrdinalIgnoreCase) : null;

    public static bool Set(bool on)
    {
        try
        {
            var settings = Create();
            try
            {
                return settings.put_UserSelectedProfile(on ? On : Off) == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(settings);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Warn($"Windows Do Not Disturb could not be switched: {ex.Message}");
            return false;
        }
    }

    private static string? Profile()
    {
        try
        {
            var settings = Create();
            try
            {
                return settings.get_UserSelectedProfile(out string id) == 0 ? id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(settings);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    private static IQuietHoursSettings Create() =>
        (IQuietHoursSettings)Activator.CreateInstance(Type.GetTypeFromCLSID(SettingsClsid, throwOnError: true)!)!;

    [ComImport]
    [Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig]
        int get_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int put_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string id);
    }
}
