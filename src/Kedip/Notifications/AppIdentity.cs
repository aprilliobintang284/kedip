using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kedip.Core;

namespace Kedip.Notifications;

/// <summary>Friendly name and icon of an app by its AppUserModelID, via the shell's AppsFolder (cached).</summary>
internal static partial class AppIdentity
{
    private static readonly Dictionary<string, (string Name, ImageSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>Call on an STA thread (the UI thread is fine; lookups are cached after the first one).</summary>
    public static (string Name, ImageSource? Icon) Get(string appId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(appId, out var hit))
            {
                return hit;
            }
        }

        string name = FallbackName(appId);
        ImageSource? icon = null;
        try
        {
            SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            item.GetDisplayName(SIGDN_NORMALDISPLAY, out string display);
            if (!string.IsNullOrWhiteSpace(display) && !display.Contains('!') && !display.Contains('_'))
            {
                name = display;
            }

            if (item is IShellItemImageFactory factory)
            {
                factory.GetImage(new SIZE { cx = 64, cy = 64 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbitmap);
                if (hbitmap != IntPtr.Zero)
                {
                    try
                    {
                        var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        icon = source;
                    }
                    finally
                    {
                        DeleteObject(hbitmap);
                    }
                }
            }

            Marshal.ReleaseComObject(item);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or System.IO.FileNotFoundException)
        {
            // Not in AppsFolder (system toasts, unregistered apps): keep the derived name.
        }
        catch (Exception ex)
        {
            Log.Warn($"App identity for {appId} failed: {ex.Message}");
        }

        lock (Gate)
        {
            Cache[appId] = (name, icon);
        }

        return (name, icon);
    }

    /// <summary>"5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" → "WhatsApp Desktop"; "Windows.SystemToast.LowDisk" → "Low Disk".</summary>
    internal static string FallbackName(string appId)
    {
        string id = appId;
        int bang = id.IndexOf('!');
        if (bang > 0)
        {
            id = id[..bang];
        }

        int underscore = id.LastIndexOf('_');
        if (underscore > 0)
        {
            id = id[..underscore];
        }

        int dot = id.LastIndexOf('.');
        string last = dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
        last = last.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        string spaced = CamelBoundary().Replace(last, " ");
        return spaced.Length > 0 ? char.ToUpperInvariant(spaced[0]) + spaced[1..] : appId;
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    private const uint SIGDN_NORMALDISPLAY = 0;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
