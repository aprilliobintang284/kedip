using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Kedip.Interop;

namespace Kedip.DockBar;

/// <summary>
/// Plain blur of whatever is behind a borderless WPF window; the window paints its own smoked-black tint and hairline
/// edge on top. The window is cut to its rounded shapes with <see cref="Shape"/> (much rounder than the system's
/// corners). Falls back to the system acrylic when the blur is not available.
/// </summary>
internal static class Glass
{
    public static void Apply(IntPtr hwnd, bool noActivate)
    {
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;

        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() | NativeMethods.WS_EX_TOOLWINDOW;
        if (noActivate)
        {
            ex |= NativeMethods.WS_EX_NOACTIVATE;
        }

        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        // Kedip draws its own rounded shape and edge; the system's small corners and frame line would fight it.
        int corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        int border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        if (!Blur(hwnd))
        {
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
    }

    /// <summary>
    /// Cuts the window (blur included) to rounded rectangles given in device pixels. The cut is a little squarer than
    /// the shape WPF draws, so the visible edge is WPF's smooth one and the hard region edge only meets plain blur.
    /// </summary>
    public static void Shape(IntPtr hwnd, IReadOnlyList<Rect> rects, double radius)
    {
        IntPtr combined = CreateRectRgn(0, 0, 0, 0);
        int diameter = Math.Max(0, (int)Math.Round((radius - 1.5) * 2));
        foreach (var r in rects)
        {
            IntPtr part = CreateRoundRectRgn((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right) + 1,
                (int)Math.Ceiling(r.Bottom) + 1, diameter, diameter);
            CombineRgn(combined, combined, part, RGN_OR);
            DeleteObject(part);
        }

        // On success the window owns the region.
        if (SetWindowRgn(hwnd, combined, true) == 0)
        {
            DeleteObject(combined);
        }
    }

    private static bool Blur(IntPtr hwnd)
    {
        var accent = new ACCENT_POLICY { AccentState = ACCENT_ENABLE_BLURBEHIND };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = pointer, SizeOfData = size };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int RGN_OR = 2;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr a, IntPtr b, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
}
