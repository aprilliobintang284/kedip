using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Kedip.Core;
using Kedip.Interop;
using Kedip.Mascot;
using Kedip.Notifications;

namespace Kedip.DockBar;

/// <summary>An app on the dock: pinned, running, or both.</summary>
internal sealed class DockApp
{
    public required string Key { get; init; }
    public string? AppId { get; init; }
    public string? ExePath { get; init; }
    public required string Name { get; set; }
    public ImageSource? Icon { get; set; }
    public bool IsPinned { get; init; }
    public List<IntPtr> Windows { get; } = [];
    public bool IsActive { get; set; }

    public bool IsRunning => Windows.Count > 0;
}

/// <summary>Top-level app windows (the ones the taskbar would show), grouped into dock apps; launching and switching.</summary>
internal sealed class DockApps
{
    /// <summary>Pinned on first run, if installed.</summary>
    public static readonly string[] DefaultPins =
    [
        "aumid:Microsoft.Windows.Explorer",
        "aumid:MSEdge",
        "aumid:Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows",
        "aumid:5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App",
        "aumid:Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic",
        "aumid:Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
        "aumid:Chrome",
        "aumid:com.squirrel.Discord.Discord",
        "aumid:windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
    ];

    private readonly Dictionary<string, (string Name, ImageSource? Icon, string? Exe)> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string?> _processPaths = [];
    private readonly Dictionary<uint, string?> _processAppIds = [];
    private readonly Dictionary<IntPtr, (string? AppId, string? Name, string? Icon)> _windowIds = [];
    private readonly Dictionary<string, ImageSource?> _windowIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pinned apps (in order) followed by running unpinned ones. UI thread.</summary>
    public List<DockApp> Build(IReadOnlyList<string> pinned)
    {
        var windows = EnumerateWindows();
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var apps = new List<DockApp>();
        foreach (string key in pinned)
        {
            if (Resolve(key) is { } app)
            {
                apps.Add(app);
            }
        }

        // Grouped the way the Windows taskbar does it: by AppUserModelID. A window that names its own id only joins a pin
        // with that id (so each Chrome profile gets its own button); a window without one joins by program.
        var extra = new Dictionary<string, DockApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            var owner = w.AppId is not null
                ? apps.FirstOrDefault(a => SameAppId(a.AppId, w.AppId))
                : apps.FirstOrDefault(a => a.ExePath is not null && w.ExePath is not null && SameApp(a.ExePath, w.ExePath));
            if (owner is null)
            {
                string key = w.AppId is not null ? "aumid:" + w.AppId : "path:" + (w.ExePath ?? "?");
                if (!extra.TryGetValue(key, out owner))
                {
                    owner = Resolve(key, pinned: false, fallbackName: w.Name ?? w.Title) ?? new DockApp { Key = key, Name = w.Name ?? w.Title };
                    if (w.Name is not null)
                    {
                        owner.Name = w.Name;
                    }

                    extra[key] = owner;
                }
            }

            // The window's own icon wins, as on the taskbar (Chrome shows the profile picture on its button).
            if (owner.Windows.Count == 0 && w.Icon is not null && WindowIcon(w.Icon) is { } icon)
            {
                owner.Icon = icon;
            }

            owner.Windows.Add(w.Hwnd);
            owner.IsActive |= w.Hwnd == foreground;
        }

        apps.AddRange(extra.Values);
        return apps;
    }

    /// <summary>Same AppUserModelID, or for packaged apps the same package ("Publisher.App_hash!Entry" up to the "!").</summary>
    private static bool SameAppId(string? pinned, string? window)
    {
        if (pinned is null || window is null)
        {
            return false;
        }

        if (string.Equals(pinned, window, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int a = pinned.IndexOf('!');
        int b = window.IndexOf('!');
        return a > 0 && a == b && pinned.AsSpan(0, a).Equals(window.AsSpan(0, b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same exe, or a Squirrel-installed app (Discord, Slack, …): the pin points at a launcher in the app's folder
    /// (<c>…\Discord\Update.exe</c> or <c>…\Discord\Discord.exe</c>) and the window runs from a versioned subfolder
    /// (<c>…\Discord\app-1.0.9266\Discord.exe</c>).
    /// </summary>
    private static bool SameApp(string? pinnedExe, string windowExe)
    {
        if (pinnedExe is null)
        {
            return false;
        }

        if (string.Equals(pinnedExe, windowExe, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? windowFolder = Path.GetDirectoryName(windowExe);
        return windowFolder is not null
            && Path.GetFileName(windowFolder).StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(windowFolder), Path.GetDirectoryName(pinnedExe), StringComparison.OrdinalIgnoreCase);
    }

    private ImageSource? WindowIcon(string resource)
    {
        if (!_windowIcons.TryGetValue(resource, out var icon))
        {
            icon = ShellItems.IconResource(resource, 64);
            _windowIcons[resource] = icon;
        }

        return icon;
    }

    /// <summary>Opens the app if it has no window; otherwise brings it forward, or minimizes it if it is already in front.</summary>
    public static void Click(DockApp app)
    {
        if (!app.IsRunning)
        {
            Launch(app);
            return;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (app.Windows.Contains(foreground) && !NativeMethods.IsIconic(foreground))
        {
            ShowWindowAsync(foreground, SW_MINIMIZE);
            return;
        }

        // Several windows: cycle to the one after the current foreground, else the first.
        int index = app.Windows.IndexOf(foreground);
        Activate(app.Windows[(index + 1) % app.Windows.Count]);
    }

    public static void Launch(DockApp app)
    {
        try
        {
            if (app.AppId is { } id)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + id) { UseShellExecute = true });
            }
            else if (app.ExePath is { } path && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start {app.Name}: {ex.Message}");
        }
    }

    public static void CloseWindows(DockApp app)
    {
        foreach (var hwnd in app.Windows)
        {
            CloseWindow(hwnd);
        }
    }

    public static void CloseWindow(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static void Activate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            ShowWindowAsync(hwnd, SW_RESTORE);
        }

        // Windows only hands the foreground to the input owner; borrowing the current foreground thread's input works.
        uint current = GetCurrentThreadId();
        uint target = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = target != 0 && target != current && AttachThreadInput(current, target, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(current, target, false);
            }
        }
    }

    private DockApp? Resolve(string key, bool pinned = true, string? fallbackName = null)
    {
        if (!_identities.TryGetValue(key, out var identity))
        {
            identity = LookUp(key, fallbackName);
            if (identity.Name.Length == 0)
            {
                return null;
            }

            _identities[key] = identity;
        }

        bool isAumid = key.StartsWith("aumid:", StringComparison.Ordinal);
        return new DockApp
        {
            Key = key,
            AppId = isAumid ? key[6..] : null,
            ExePath = identity.Exe,
            Name = identity.Name,
            Icon = identity.Icon,
            IsPinned = pinned,
        };
    }

    private static (string Name, ImageSource? Icon, string? Exe) LookUp(string key, string? fallbackName)
    {
        if (key.StartsWith("aumid:", StringComparison.Ordinal))
        {
            string id = key[6..];
            string parsing = @"shell:AppsFolder\" + id;
            // Not installed (no AppsFolder entry) → skip pins; running windows still get a fallback name.
            if (ShellItems.DisplayName(parsing) is null && fallbackName is null)
            {
                return ("", null, null);
            }

            var (name, icon) = AppIdentity.Get(id);
            // File Explorer's Start entry points at a shell folder, not a program; its windows run from explorer.exe.
            string? exe = string.Equals(id, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                : ShellItems.TargetPath(parsing);
            if (exe is not null && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                exe = null;
            }

            return (fallbackName is not null && name == AppIdentity.FallbackName(id) ? fallbackName : name, icon, exe);
        }

        string path = key.StartsWith("path:", StringComparison.Ordinal) ? key[5..] : key;
        string display = fallbackName ?? Path.GetFileNameWithoutExtension(path);
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
            {
                display = description;
            }
        }
        catch (Exception)
        {
        }

        return (display, File.Exists(path) ? ShellItems.Image(path, 64) : null, path);
    }

    private sealed record AppWindow(IntPtr Hwnd, string? AppId, string? ExePath, string Title, string? Name, string? Icon);

    private List<AppWindow> EnumerateWindows()
    {
        var result = new List<AppWindow>();
        var seenPids = new HashSet<uint>();
        var seenHwnds = new HashSet<IntPtr>();
        uint own = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd, own, out uint pid))
            {
                return true;
            }

            seenPids.Add(pid);
            seenHwnds.Add(hwnd);
            if (!_processPaths.TryGetValue(pid, out string? path))
            {
                path = ShellItems.ProcessPath(pid);
                _processPaths[pid] = path;
            }

            if (!_windowIds.TryGetValue(hwnd, out var identity))
            {
                // The window's own id (UWP frames, Chrome profiles …), else the id of its packaged process (Store apps).
                identity = ShellItems.WindowIdentity(hwnd);
                if (identity.AppId is null)
                {
                    if (!_processAppIds.TryGetValue(pid, out string? packaged))
                    {
                        packaged = ShellItems.PackagedAppId(pid);
                        _processAppIds[pid] = packaged;
                    }

                    identity.AppId = packaged;
                }

                _windowIds[hwnd] = identity;
            }

            result.Add(new AppWindow(hwnd, identity.AppId, path, WindowTitle(hwnd), identity.Name, identity.Icon));
            return true;
        }, IntPtr.Zero);

        // Forget closed processes/windows so recycled ids are looked up afresh.
        foreach (uint pid in _processPaths.Keys.Where(p => !seenPids.Contains(p)).ToList())
        {
            _processPaths.Remove(pid);
            _processAppIds.Remove(pid);
        }

        foreach (IntPtr hwnd in _windowIds.Keys.Where(h => !seenHwnds.Contains(h)).ToList())
        {
            _windowIds.Remove(hwnd);
        }

        return result;
    }

    /// <summary>
    /// The windows the Windows taskbar would show: visible, not cloaked, unowned or forced with WS_EX_APPWINDOW, and no
    /// tool/no-activate windows unless forced. Kedip's own windows only when they ask for a taskbar button (settings).
    /// </summary>
    private static bool IsAppWindow(IntPtr hwnd, uint ownPid, out uint pid)
    {
        pid = 0;
        if (!NativeMethods.IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        bool forced = (ex & WS_EX_APPWINDOW) != 0;
        if (!forced && (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero
                        || (ex & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0))
        {
            return false;
        }

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == ownPid && !forced)
        {
            return false;
        }

        string cls = NativeMethods.GetClassName(hwnd);
        return !ForegroundWatcher.IsShellClass(cls) && cls != "Windows.UI.Core.CoreWindow";
    }

    internal static string WindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        int copied = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, copied);
    }

    private const uint GW_OWNER = 4;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, char[] text, int max);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
