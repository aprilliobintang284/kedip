using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Kedip.Shell.Settings;

/// <summary>Windows 11 frame styling via DWM: dark title bar in our window colour, rounded popup menus. No-ops on older builds.</summary>
internal static class DwmStyling
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwcpRound = 2;

    // COLORREF values (0x00BBGGRR).
    private const int CaptionColor = 0x001E1C1C; // #1C1C1E, the settings window background
    private const int CaptionTextColor = 0x00F7F5F5; // #F5F5F7
    private const int MenuBorderColor = 0x004C4848; // #48484C

    /// <summary>True when DWM draws rounded corners (Windows 11).</summary>
    public static bool HasRoundedCorners { get; } = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Gives <paramref name="window"/> a dark caption that blends into its dark content.</summary>
    public static void UseDarkFrame(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (Set(hwnd, DwmwaUseImmersiveDarkMode, 1) != 0)
            {
                Set(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, 1);
            }

            if (HasRoundedCorners)
            {
                Set(hwnd, DwmwaCaptionColor, CaptionColor);
                Set(hwnd, DwmwaTextColor, CaptionTextColor);
            }
        };
    }

    /// <summary>Rounded corners and a dark hairline border for a popup (context menu) window.</summary>
    public static void RoundPopup(IntPtr hwnd)
    {
        if (!HasRoundedCorners || hwnd == IntPtr.Zero)
        {
            return;
        }

        Set(hwnd, DwmwaWindowCornerPreference, DwmwcpRound);
        Set(hwnd, DwmwaBorderColor, MenuBorderColor);
    }

    private static int Set(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
