using System.Runtime.InteropServices;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Interop;

namespace Kedip.Mascot;

/// <summary>
/// Tracks what kind of window is in the foreground.
/// OWNER: B.
/// </summary>
/// <remarks>
/// Out-of-context WinEvent hooks are delivered on the thread that installed them (the UI thread), so
/// <see cref="Changed"/> is raised there directly. Location changes are hooked only for the foreground
/// window's own thread and are debounced before the fullscreen check is repeated.
/// </remarks>
public sealed class ForegroundWatcher : IAppService
{
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const int RecheckDelayMs = 250;
    private const int RecheckMaxLatencyMs = 1000;

    private readonly NativeMethods.WinEventDelegate _proc;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private DispatcherTimer? _recheckTimer;
    private long _recheckFirstRequest;
    private IntPtr _foregroundHook;
    private IntPtr _minimizeHook;
    private IntPtr _locationHook;
    private IntPtr _locationHwnd;
    private bool _started;
    private bool _disposed;

    public ForegroundWatcher()
    {
        // Kept in a field: the native hook holds only a function pointer, the GC must not collect the delegate.
        _proc = OnWinEvent;
    }

    /// <summary>Desktop/shell (Progman, WorkerW, Shell_TrayWnd, Shell_SecondaryTrayWnd), no window, or a Kedip window.</summary>
    public bool IsDesktopForeground { get; private set; } = true;

    /// <summary>A non-shell window covers its whole monitor (game, video, presentation) — the primary one, where the notch is.</summary>
    public bool IsFullscreenAppForeground { get; private set; }

    /// <summary>The foreground app is a web browser (by process name).</summary>
    public bool IsBrowserForeground { get; private set; }

    /// <summary>
    /// The foreground app window touches the top edge of the primary monitor across its centre (maximized or snapped
    /// to the top), so its title bar or tab strip sits right under the notch.
    /// </summary>
    public bool IsTopEdgeForeground { get; private set; }

    /// <summary>Raised on the UI thread when any property changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _recheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RecheckDelayMs),
        };
        _recheckTimer.Tick += OnRecheckTick;

        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _minimizeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == IntPtr.Zero || _minimizeHook == IntPtr.Zero)
        {
            Log.Warn("ForegroundWatcher: SetWinEventHook failed; foreground changes are only noticed on refresh");
        }

        Evaluate(allowNoWindow: true);
    }

    /// <summary>Re-reads the foreground window now (safety net for missed events). Cheap; call at most every few seconds.</summary>
    public void Refresh()
    {
        if (_started && !_disposed)
        {
            Evaluate(allowNoWindow: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_recheckTimer is not null)
        {
            _recheckTimer.Stop();
            _recheckTimer.Tick -= OnRecheckTick;
        }

        Unhook(ref _locationHook);
        Unhook(ref _minimizeHook);
        Unhook(ref _foregroundHook);
        _locationHwnd = IntPtr.Zero;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // Never let an exception escape into the native caller.
        try
        {
            if (_disposed)
            {
                return;
            }

            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                    // Evaluate now, then once more after things settle (minimize-all and Win+D hand the
                    // foreground to the desktop a moment after the first event).
                    Evaluate(allowNoWindow: false);
                    ScheduleRecheck();
                    break;
                case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                    if (hwnd == _locationHwnd && idObject == OBJID_WINDOW && idChild == CHILDID_SELF)
                    {
                        ScheduleRecheck();
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("ForegroundWatcher event handling failed", ex);
        }
    }

    private void ScheduleRecheck()
    {
        if (_recheckTimer is null || _disposed)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (!_recheckTimer.IsEnabled)
        {
            _recheckFirstRequest = now;
        }
        else if (now - _recheckFirstRequest >= RecheckMaxLatencyMs)
        {
            // A window that keeps moving must not postpone the check forever.
            return;
        }

        _recheckTimer.Stop();
        _recheckTimer.Start();
    }

    private void OnRecheckTick(object? sender, EventArgs e)
    {
        _recheckTimer?.Stop();
        try
        {
            Evaluate(allowNoWindow: true);
        }
        catch (Exception ex)
        {
            Log.Error("ForegroundWatcher re-check failed", ex);
        }
    }

    private void Evaluate(bool allowNoWindow)
    {
        if (_disposed)
        {
            return;
        }

        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        var kind = Classify(hwnd);
        if (kind == ForegroundKind.Nothing && !allowNoWindow)
        {
            // Often transient while activation moves (restoring a minimized window, an app starting up);
            // the delayed re-check confirms it, so the eyes do not flash in between.
            return;
        }

        bool desktop = kind != ForegroundKind.App;
        bool fullscreen = !desktop && IsFullscreen(hwnd);
        bool browser = !desktop && IsBrowser(hwnd);
        bool topEdge = !desktop && !fullscreen && TouchesTopEdge(hwnd);
        TrackLocation(desktop ? IntPtr.Zero : hwnd);

        if (desktop == IsDesktopForeground && fullscreen == IsFullscreenAppForeground
            && browser == IsBrowserForeground && topEdge == IsTopEdgeForeground)
        {
            return;
        }

        IsDesktopForeground = desktop;
        IsFullscreenAppForeground = fullscreen;
        IsBrowserForeground = browser;
        IsTopEdgeForeground = topEdge;
        OnChanged();
    }

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen", "floorp", "waterfox",
        "librewolf", "thorium", "chromium", "yandex", "whale", "browser", "comet", "duckduckgo",
    };

    private uint _browserPid;
    private bool _browserPidIsBrowser;

    private bool IsBrowser(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return false;
        }

        if (pid != _browserPid)
        {
            _browserPid = pid;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                _browserPidIsBrowser = BrowserProcesses.Contains(process.ProcessName);
            }
            catch (Exception)
            {
                _browserPidIsBrowser = false;
            }
        }

        return _browserPidIsBrowser;
    }

    private static bool TouchesTopEdge(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info) || (info.dwFlags & MONITORINFOF_PRIMARY) == 0)
        {
            return false;
        }

        var m = info.rcMonitor;
        int centre = (m.Left + m.Right) / 2;
        // Maximized windows overhang by their invisible border (≈ −8 px); top-snapped ones start at the edge.
        return rect.Top <= m.Top + 8 && rect.Bottom > m.Top + 48 && rect.Left <= centre && rect.Right >= centre;
    }

    private enum ForegroundKind { Desktop, Nothing, App }

    private ForegroundKind Classify(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return ForegroundKind.Nothing;
        }

        if (IsShellClass(NativeMethods.GetClassName(hwnd)))
        {
            return ForegroundKind.Desktop;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownProcessId)
        {
            return ForegroundKind.Desktop;
        }

        // A minimized or hidden window that still holds the activation shows nothing in front: the desktop, once settled.
        return NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd)
            ? ForegroundKind.Nothing
            : ForegroundKind.App;
    }

    /// <summary>Desktop and taskbar windows (incl. the taskbar's tray overflow flyouts).</summary>
    internal static bool IsShellClass(string className) => className is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
        or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // The notch lives on the primary monitor only; a fullscreen window elsewhere does not cover it.
        if ((info.dwFlags & MONITORINFOF_PRIMARY) == 0)
        {
            return false;
        }

        var m = info.rcMonitor;
        if (rect.Left > m.Left || rect.Top > m.Top || rect.Right < m.Right || rect.Bottom < m.Bottom)
        {
            return false;
        }

        // A normal maximized window also overhangs the monitor when the taskbar auto-hides; real fullscreen
        // (F11, games, slideshows, video players) drops the caption.
        long style = NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        return !(Native.IsZoomed(hwnd) && (style & WS_CAPTION) == WS_CAPTION);
    }

    private void TrackLocation(IntPtr hwnd)
    {
        if (hwnd == _locationHwnd)
        {
            return;
        }

        Unhook(ref _locationHook);
        _locationHwnd = hwnd;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (thread == 0)
        {
            return;
        }

        // Scoped to the foreground window's thread: we never see the system-wide flood of location events.
        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _proc, pid, thread,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hWnd);
    }
}
