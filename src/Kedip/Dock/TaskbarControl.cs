using System.IO;
using System.Runtime.InteropServices;
using Kedip.Core;
using Kedip.Interop;

namespace Kedip.DockBar;

/// <summary>
/// Hides and restores the Windows taskbar(s) for "Gantikan taskbar". A marker file records that Kedip hid them, so a
/// crash never leaves the user without a taskbar: the next start (or any exit path) shows them again.
/// </summary>
internal static class TaskbarControl
{
    private static string MarkerFile => Path.Combine(AppPaths.DataDir, "taskbar-hidden.flag");

    public static bool IsHiddenByKedip { get; private set; }

    /// <summary>Primary taskbar is currently on screen.</summary>
    public static bool IsVisible => ExplorerTray() is var hwnd && hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd);

    /// <summary>Explorer's primary taskbar window (skipping the hidden one Kedip's tray service owns).</summary>
    public static IntPtr ExplorerTray()
    {
        uint own = (uint)Environment.ProcessId;
        IntPtr hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_TrayWnd", null)) != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != own)
            {
                return hwnd;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Screen rectangle (px) of the primary taskbar, when visible.</summary>
    public static NativeMethods.RECT? VisibleRect
    {
        get
        {
            IntPtr hwnd = ExplorerTray();
            return hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindowRect(hwnd, out var r) ? r : null;
        }
    }

    public static void Hide()
    {
        foreach (var hwnd in Taskbars())
        {
            ShowWindow(hwnd, SW_HIDE);
        }

        if (!IsHiddenByKedip)
        {
            IsHiddenByKedip = true;
            TryWriteMarker(true);
        }
    }

    public static void Show()
    {
        foreach (var hwnd in Taskbars())
        {
            ShowWindow(hwnd, SW_SHOWNA);
        }

        IsHiddenByKedip = false;
        TryWriteMarker(false);
    }

    /// <summary>On start-up: bring the taskbar back if a previous run hid it and never got to restore it.</summary>
    public static void RestoreIfLeftHidden()
    {
        if (File.Exists(MarkerFile))
        {
            Log.Warn("Taskbar was left hidden by a previous run; restoring it");
            Show();
        }
    }

    private static IEnumerable<IntPtr> Taskbars()
    {
        var list = new List<IntPtr>();
        IntPtr primary = ExplorerTray();
        if (primary != IntPtr.Zero)
        {
            list.Add(primary);
        }

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            list.Add(secondary);
        }

        return list;
    }

    private static void TryWriteMarker(bool hidden)
    {
        try
        {
            if (hidden)
            {
                File.WriteAllText(MarkerFile, DateTime.Now.ToString("O"));
            }
            else if (File.Exists(MarkerFile))
            {
                File.Delete(MarkerFile);
            }
        }
        catch (IOException)
        {
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}

/// <summary>Reserves a strip at the bottom of the primary monitor (an "app bar") so maximized windows stop above the dock.</summary>
internal sealed class AppBar : IDisposable
{
    public const int ABN_POSCHANGED = 1;
    private const int ABM_NEW = 0;
    private const int ABM_REMOVE = 1;
    private const int ABM_QUERYPOS = 2;
    private const int ABM_SETPOS = 3;
    private const int ABE_BOTTOM = 3;

    private readonly IntPtr _hwnd;
    private bool _registered;
    private int _heightPx;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        CallbackMessage = RegisterWindowMessage("Kedip.AppBar");
    }

    public uint CallbackMessage { get; }

    public bool IsReserved => _registered;

    public void Reserve(int heightPx)
    {
        if (!_registered)
        {
            var data = Data();
            data.uCallbackMessage = CallbackMessage;
            if (SHAppBarMessage(ABM_NEW, ref data) == IntPtr.Zero)
            {
                return;
            }

            _registered = true;
        }

        _heightPx = heightPx;
        Reposition();
    }

    /// <summary>Re-claims the strip (after the shell rearranges app bars).</summary>
    public void Reposition()
    {
        if (!_registered)
        {
            return;
        }

        IntPtr monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var data = Data();
        data.uEdge = ABE_BOTTOM;
        data.rc = info.rcMonitor;
        data.rc.Top = data.rc.Bottom - _heightPx;
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Top = data.rc.Bottom - _heightPx;
        SHAppBarMessage(ABM_SETPOS, ref data);
    }

    public void Release()
    {
        if (_registered)
        {
            var data = Data();
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registered = false;
        }
    }

    public void Dispose() => Release();

    /// <summary>The Windows taskbar is set to hide automatically (then showing it leaves it slid away until revealed).</summary>
    public static bool TaskbarAutoHides()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64() & ABS_AUTOHIDE) != 0;
    }

    private const int ABM_GETSTATE = 4;
    private const long ABS_AUTOHIDE = 1;

    private APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeMethods.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
