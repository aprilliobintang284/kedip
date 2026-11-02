using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kedip.Core;

namespace Kedip.DockBar;

/// <summary>A notification-area icon of some app, as it was last reported to the tray.</summary>
internal sealed class TrayItem
{
    public required string Key { get; init; }
    public required IntPtr Hwnd { get; set; }
    public required uint Id { get; init; }
    public uint ProcessId { get; set; }

    /// <summary>The app's name, for icons that come without a tooltip.</summary>
    public string AppName { get; set; } = "";

    public string Title => Tip.Length > 0 ? Tip : AppName;
    public uint CallbackMessage { get; set; }
    public uint Version { get; set; }
    public string Tip { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// Collects the apps' tray icons for the dock. Apps hand their icons to the first top-level "Shell_TrayWnd" window, so
/// Kedip keeps a hidden one of its own above Explorer's, reads every message and passes it on to Explorer unchanged
/// (Explorer's tray keeps working exactly as before). Runs on its own thread so a busy UI never stalls other apps.
/// Closing it (or Kedip exiting / crashing) simply leaves Explorer's window as the first one again.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private const string TrayClass = "Shell_TrayWnd";
    private const int SignatureTray = 0x34753423;
    private const uint WM_STOP = 0x8000 + 0x51; // WM_APP range: never forwarded
    private static readonly IntPtr TimerId = new(1);

    private readonly Dispatcher _ui;
    private readonly List<TrayItem> _items = [];
    private Thread? _thread;
    private WndProcDelegate? _wndProc; // kept alive while the window class is registered
    private IntPtr _hwnd;
    private IntPtr _explorerTray;
    private uint _taskbarCreated;
    private bool _changePosted;

    public TrayService(Dispatcher ui) => _ui = ui;

    /// <summary>Raised on the UI thread when icons are added, changed or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Known icons in the order they appeared (UI thread).</summary>
    public IReadOnlyList<TrayItem> Items => _items;

    public IEnumerable<TrayItem> Visible => _items.Where(i => !i.Hidden && i.Icon is not null);

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Kedip tray" };
        _thread.Start();
    }

    public void Dispose()
    {
        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WM_STOP, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(1000);
        }

        _thread = null;
    }

    /// <summary>Drops icons whose window is gone (apps that exit without removing their icon).</summary>
    public void Prune()
    {
        if (_items.RemoveAll(i => !IsWindow(i.Hwnd)) > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Talking to the icon's app -----------------------------------------------------------------------------

    /// <summary>Plays a mouse action on an icon the way Explorer does, at <paramref name="screenPx"/>.</summary>
    public static void Send(TrayItem item, TrayMouse action, Point screenPx)
    {
        // The app usually brings a window or menu to the front; let it.
        AllowSetForegroundWindow(item.ProcessId);
        foreach (uint message in Messages(action, item.Version))
        {
            IntPtr wParam;
            IntPtr lParam;
            if (item.Version >= 4)
            {
                wParam = MakeLong((int)screenPx.X, (int)screenPx.Y);
                lParam = MakeLong((int)message, (int)item.Id);
            }
            else
            {
                wParam = new IntPtr(item.Id);
                lParam = new IntPtr(message);
            }

            SendNotifyMessage(item.Hwnd, item.CallbackMessage, wParam, lParam);
        }
    }

    /// <summary>What Explorer sends for a mouse action; icons that set a version (3+) also get the select/menu notifications.</summary>
    private static uint[] Messages(TrayMouse action, uint version) => action switch
    {
        TrayMouse.LeftDown => [WM_LBUTTONDOWN],
        TrayMouse.LeftUp => version >= 3 ? [WM_LBUTTONUP, NIN_SELECT] : [WM_LBUTTONUP],
        TrayMouse.LeftDouble => [WM_LBUTTONDBLCLK],
        TrayMouse.RightDown => [WM_RBUTTONDOWN],
        TrayMouse.RightUp => version >= 3 ? [WM_RBUTTONUP, WM_CONTEXTMENU] : [WM_RBUTTONUP],
        TrayMouse.MiddleUp => [WM_MBUTTONDOWN, WM_MBUTTONUP],
        _ => [WM_MOUSEMOVE],
    };

    private static IntPtr MakeLong(int low, int high) => new((int)(((uint)high << 16) | ((uint)low & 0xFFFF)));

    // ---- Tray thread ---------------------------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            _wndProc = WndProc;
            IntPtr instance = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = TrayClass,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                Log.Warn($"Tray: could not register the window class ({Marshal.GetLastWin32Error()})");
                return;
            }

            _hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, TrayClass, "", WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                UnregisterClass(TrayClass, instance);
                return;
            }

            // Sandboxed (low integrity) apps must still be able to hand us their icons.
            ChangeWindowMessageFilterEx(_hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _explorerTray = TaskbarControl.ExplorerTray();
            Raise();
            SetTimer(_hwnd, TimerId, 500, IntPtr.Zero);
            // Every app re-adds its icons on "TaskbarCreated"; that is how the ones already showing reach us.
            SendNotifyMessage(HWND_BROADCAST, _taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            Log.Info($"Tray: listening (Explorer tray {_explorerTray})");

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnregisterClass(TrayClass, instance);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray stopped: {ex.Message}");
        }
        finally
        {
            _hwnd = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_COPYDATA:
                    return OnCopyData(wParam, lParam);
                case WM_TIMER when wParam == TimerId:
                    Watch();
                    return IntPtr.Zero;
                case WM_STOP:
                    KillTimer(hwnd, TimerId);
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            // Commands other programs send to "the taskbar" (show desktop, minimize all, …) belong to Explorer.
            if (msg == WM_COMMAND || msg is >= WM_USER and < 0x8000)
            {
                return Forward(msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray message {msg:X} failed: {ex.Message}");
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Stays the first "Shell_TrayWnd" (Explorer re-raises its taskbar) and mirrors its rectangle for apps that measure it.</summary>
    private void Watch()
    {
        IntPtr explorer = TaskbarControl.ExplorerTray();
        bool explorerChanged = explorer != _explorerTray;
        _explorerTray = explorer;
        if (FindWindow(TrayClass, null) != _hwnd || explorerChanged)
        {
            Raise();
            if (explorerChanged && explorer != IntPtr.Zero)
            {
                // Explorer restarted; its own "TaskbarCreated" may have reached it before us.
                SendNotifyMessage(HWND_BROADCAST, _taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private void Raise()
    {
        if (_explorerTray != IntPtr.Zero && GetWindowRect(_explorerTray, out RECT r))
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE);
        }
        else
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private IntPtr Forward(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_explorerTray == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        uint how = InSendMessageEx(IntPtr.Zero);
        if ((how & ISMEX_SEND) == 0 && msg != WM_COPYDATA)
        {
            if ((how & ISMEX_NOTIFY) != 0)
            {
                SendNotifyMessage(_explorerTray, msg, wParam, lParam);
            }
            else
            {
                PostMessage(_explorerTray, msg, wParam, lParam);
            }

            return IntPtr.Zero;
        }

        return SendMessageTimeout(_explorerTray, msg, wParam, lParam, SMTO_ABORTIFHUNG, 4000, out IntPtr result) != IntPtr.Zero
            ? result
            : IntPtr.Zero;
    }

    private IntPtr OnCopyData(IntPtr wParam, IntPtr lParam)
    {
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        if (cds.dwData == new IntPtr(1) && cds.lpData != IntPtr.Zero && cds.cbData >= 8 + 24)
        {
            // Zero-padded copy: older apps send a shorter NOTIFYICONDATA.
            var data = new byte[Math.Max(cds.cbData, TrayDataSize)];
            Marshal.Copy(cds.lpData, data, 0, Math.Min(cds.cbData, data.Length));
            if (BitConverter.ToInt32(data, 0) == SignatureTray)
            {
                Collect(data);
            }
        }

        // Explorer still gets everything (icons, app bars, icon rectangles) and its answer goes back to the app.
        IntPtr answer = Forward(WM_COPYDATA, wParam, lParam);
        return _explorerTray == IntPtr.Zero && cds.dwData == new IntPtr(1) ? new IntPtr(1) : answer;
    }

    private void Collect(byte[] data)
    {
        uint message = BitConverter.ToUInt32(data, 4);
        const int n = 8; // NOTIFYICONDATA (32-bit layout, as the tray protocol always uses)
        IntPtr hwnd = new(BitConverter.ToInt32(data, n + 4)); // handles are sign-extended
        uint id = BitConverter.ToUInt32(data, n + 8);
        uint flags = BitConverter.ToUInt32(data, n + 12);
        uint callback = BitConverter.ToUInt32(data, n + 16);
        IntPtr icon = new(BitConverter.ToInt32(data, n + 20));
        string tip = ReadString(data, n + 24, 128);
        uint state = BitConverter.ToUInt32(data, n + 280);
        uint stateMask = BitConverter.ToUInt32(data, n + 284);
        uint version = BitConverter.ToUInt32(data, n + 800);
        var guid = new Guid(data.AsSpan(n + 936, 16));
        string key = (flags & NIF_GUID) != 0 && guid != Guid.Empty ? "g:" + guid : $"{hwnd}:{id}";

        // The app may destroy its icon right after this call: take a copy now, convert it on the UI thread.
        IntPtr iconCopy = (flags & NIF_ICON) != 0 && icon != IntPtr.Zero ? CopyIcon(icon) : IntPtr.Zero;
        GetWindowThreadProcessId(hwnd, out uint pid);
        var update = new Update(message, key, hwnd, id, pid, flags, callback, iconCopy, tip, state, stateMask, version);
        _ui.BeginInvoke(DispatcherPriority.Background, () => Apply(update));
    }

    private sealed record Update(
        uint Message, string Key, IntPtr Hwnd, uint Id, uint Pid, uint Flags, uint Callback, IntPtr Icon, string Tip,
        uint State, uint StateMask, uint Version);

    // ---- UI thread ---------------------------------------------------------------------------------------------

    private void Apply(Update u)
    {
        var item = _items.Find(i => i.Key == u.Key);
        switch (u.Message)
        {
            case NIM_DELETE:
                if (item is not null)
                {
                    _items.Remove(item);
                }

                break;
            case NIM_ADD or NIM_MODIFY:
                if (item is null)
                {
                    if (IsExplorerStatusIcon(u))
                    {
                        break;
                    }

                    item = new TrayItem { Key = u.Key, Hwnd = u.Hwnd, Id = u.Id, ProcessId = u.Pid, AppName = AppNameOf(u.Pid) };
                    _items.Add(item);
                }
                else if (u.Message == NIM_ADD && u.Hwnd != IntPtr.Zero)
                {
                    // Same GUID re-added from a new window (the app restarted its tray).
                    item.Hwnd = u.Hwnd;
                    item.ProcessId = u.Pid;
                }

                if ((u.Flags & NIF_MESSAGE) != 0)
                {
                    item.CallbackMessage = u.Callback;
                }

                if ((u.Flags & NIF_TIP) != 0)
                {
                    item.Tip = u.Tip;
                }

                if ((u.Flags & NIF_STATE) != 0 && (u.StateMask & NIS_HIDDEN) != 0)
                {
                    item.Hidden = (u.State & NIS_HIDDEN) != 0;
                }

                if (u.Icon != IntPtr.Zero)
                {
                    item.Icon = ToImage(u.Icon) ?? item.Icon;
                }

                break;
            case NIM_SETVERSION:
                if (item is not null)
                {
                    item.Version = u.Version;
                }

                break;
        }

        if (u.Icon != IntPtr.Zero && (item is null || u.Message is not (NIM_ADD or NIM_MODIFY)))
        {
            DestroyIcon(u.Icon);
        }

        // Icon animations send bursts of updates; repaint once per burst.
        if (!_changePosted)
        {
            _changePosted = true;
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _changePosted = false;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>Explorer's own volume / network / power icons: the dock shows those itself.</summary>
    private static bool IsExplorerStatusIcon(Update u) =>
        u.Key.StartsWith("g:7820ae", StringComparison.OrdinalIgnoreCase) && u.Key.EndsWith("-23e3-4229-82c1-e41cb67d5b9c", StringComparison.OrdinalIgnoreCase);

    private static string AppNameOf(uint pid)
    {
        if (Kedip.Interop.ShellItems.ProcessPath(pid) is not { } path)
        {
            return "";
        }

        try
        {
            string? description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? System.IO.Path.GetFileNameWithoutExtension(path) : description.Trim();
        }
        catch (Exception)
        {
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>
    /// Bitmap of an icon with its transparency intact. Tray icons carry straight (not premultiplied) alpha, often with
    /// stray colour in fully transparent pixels, so the pixels are read directly; icons without alpha use their mask.
    /// Destroys <paramref name="icon"/>.
    /// </summary>
    private static ImageSource? ToImage(IntPtr icon)
    {
        try
        {
            if (!GetIconInfo(icon, out ICONINFO info))
            {
                return null;
            }

            try
            {
                if (info.hbmColor == IntPtr.Zero || ReadBits(info.hbmColor) is not var (pixels, width, height))
                {
                    return null;
                }

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length && !hasAlpha; i += 4)
                {
                    hasAlpha = pixels[i] != 0;
                }

                if (!hasAlpha && info.hbmMask != IntPtr.Zero && ReadBits(info.hbmMask) is var (mask, _, _))
                {
                    for (int i = 0; i + 3 < pixels.Length && i < mask.Length; i += 4)
                    {
                        pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                    }
                }

                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                image.Freeze();
                return image;
            }
            finally
            {
                DeleteObject(info.hbmColor);
                DeleteObject(info.hbmMask);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    /// <summary>A bitmap's pixels as top-down 32-bit BGRA.</summary>
    private static (byte[] Pixels, int Width, int Height)? ReadBits(IntPtr bitmap)
    {
        var bm = new BITMAP();
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            return null;
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = bm.bmWidth,
            biHeight = -bm.bmHeight,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[bm.bmWidth * bm.bmHeight * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            return GetDIBits(dc, bitmap, 0, (uint)bm.bmHeight, pixels, ref header, 0) == 0 ? null : (pixels, bm.bmWidth, bm.bmHeight);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static string ReadString(byte[] data, int offset, int maxChars)
    {
        var text = Encoding.Unicode.GetString(data, offset, maxChars * 2);
        int end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    // ---- Interop -------------------------------------------------------------------------------------------------

    private const int TrayDataSize = 8 + 956;
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_STATE = 0x08;
    private const uint NIF_GUID = 0x20;
    private const uint NIS_HIDDEN = 0x01;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_USER = 0x0400;
    private const uint NIN_SELECT = WM_USER;
    private const uint ISMEX_SEND = 0x1;
    private const uint ISMEX_NOTIFY = 0x2;
    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const uint MSGFLT_ALLOW = 1;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width,
        int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout,
        out IntPtr result);

    [DllImport("user32.dll")]
    private static extern uint InSendMessageEx(IntPtr reserved);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeFilterStruct);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}

internal enum TrayMouse { Move, LeftDown, LeftUp, LeftDouble, RightDown, RightUp, MiddleUp }
