using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kedip.Interop;

/// <summary>
/// Shell lookups for apps and windows: names, icons (with real alpha), link targets, a window's AppUserModelID and
/// a process's exe path. All calls are best-effort and return null on failure. Use from an STA thread.
/// </summary>
internal static class ShellItems
{
    private static readonly PROPERTYKEY AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PROPERTYKEY LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    /// <summary>Display name of a parsing name such as <c>shell:AppsFolder\&lt;aumid&gt;</c> or an exe path.</summary>
    public static string? DisplayName(string parsingName)
    {
        var item = Create(parsingName);
        if (item is null)
        {
            return null;
        }

        try
        {
            item.GetDisplayName(SIGDN_NORMALDISPLAY, out string name);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Exe behind an AppsFolder entry of a desktop app (empty for packaged apps).</summary>
    public static string? TargetPath(string parsingName)
    {
        var item = Create(parsingName);
        if (item is null)
        {
            return null;
        }

        try
        {
            var key = LinkTargetParsingPath;
            return item.GetString(ref key, out string value) == 0 && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Frozen icon/thumbnail of <paramref name="parsingName"/> at about <paramref name="size"/> px, alpha preserved.</summary>
    public static BitmapSource? Image(string parsingName, int size)
    {
        var item = Create(parsingName);
        if (item is null)
        {
            return null;
        }

        try
        {
            if (item is not IShellItemImageFactory factory
                || factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out IntPtr hbitmap) != 0
                || hbitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return FromHBitmap(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>The AppUserModelID a window was given (UWP frames, Chrome profiles, WhatsApp …), or null.</summary>
    public static string? WindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out object? storeObject) != 0 || storeObject is not IPropertyStore store)
        {
            return null;
        }

        try
        {
            var key = AppUserModelId;
            if (store.GetValue(ref key, out PROPVARIANT value) != 0)
            {
                return null;
            }

            try
            {
                return value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>Full exe path of a process, or null (works for elevated processes too).</summary>
    public static string? ProcessPath(uint processId)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            int length = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static IShellItem2? Create(string parsingName)
    {
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItem2).GUID, out IShellItem2 item);
            return item;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or System.IO.FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Copies a 32-bpp HBITMAP top-down with its (premultiplied) alpha into a frozen BitmapSource.</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        var bm = new BITMAP();
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0)
        {
            return null;
        }

        int width = bm.bmWidth;
        int height = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        // Icons without an alpha channel come back fully transparent; treat them as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }

        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    private const uint SIGDN_NORMALDISPLAY = 0;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const ushort VT_LPWSTR = 31;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr pointer;
        public IntPtr extra;
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

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        void GetParent(out IntPtr ppsi);

        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IntPtr psi, uint hint, out int piOrder);

        void GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        void GetPropertyDescriptionList(IntPtr keyType, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        void Update(IntPtr pbc);

        void GetProperty(IntPtr key, IntPtr ppropvar);

        void GetCLSID(IntPtr key, out Guid pclsid);

        void GetFileTime(IntPtr key, out long pft);

        void GetInt32(IntPtr key, out int pi);

        [PreserveSig]
        int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem2 ppv);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, ref BITMAP bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
