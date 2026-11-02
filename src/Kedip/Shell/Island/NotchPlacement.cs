using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Kedip.Core;
using Kedip.Interop;

namespace Kedip.Shell.Island;

/// <summary>Win32 side of the notch window: tool/no-activate styles, top-edge anchoring and the topmost band.</summary>
internal static class NotchPlacement
{
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int MA_NOACTIVATE = 3;

    /// <summary>Margin between the island and the screen edge for the Left/Right positions.</summary>
    public const double EdgeMargin = 24;

    /// <summary>Not in Alt+Tab, never activated by clicks.</summary>
    public static void ApplyWindowStyles(IntPtr hwnd)
    {
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Moves the window to y = 0 of the primary monitor, horizontally per <paramref name="position"/>.</summary>
    public static void Anchor(Window window, IntPtr hwnd, NotchPosition position)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info) ||
            !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            Log.Warn("Notch placement skipped: monitor or window rect unavailable");
            return;
        }

        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int width = rect.Right - rect.Left;
        int margin = (int)Math.Round(EdgeMargin * scale);
        var screen = info.rcMonitor;
        int x = position switch
        {
            NotchPosition.Left => screen.Left + margin,
            NotchPosition.Right => screen.Right - width - margin,
            _ => screen.Left + (screen.Right - screen.Left - width) / 2,
        };

        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST, x, screen.Top, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static void ReassertTopmost(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>True when the mouse cursor is over the painted island (works during drag-and-drop, too).</summary>
    public static bool IsCursorOver(IslandSurface surface)
    {
        if (!NativeMethods.GetCursorPos(out var point) || PresentationSource.FromVisual(surface) is null)
        {
            return false;
        }

        try
        {
            return surface.HitsIsland(surface.PointFromScreen(new Point(point.X, point.Y)));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
