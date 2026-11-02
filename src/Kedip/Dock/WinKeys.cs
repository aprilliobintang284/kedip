using System.Runtime.InteropServices;

namespace Kedip.DockBar;

/// <summary>Windows-key shortcuts for system surfaces (Start, notification centre, project, run, search …).</summary>
internal static class WinKeys
{
    public const byte A = 0x41;
    public const byte B = 0x42;
    public const byte D = 0x44;
    public const byte N = 0x4E;
    public const byte P = 0x50;
    public const byte R = 0x52;
    public const byte S = 0x53;
    public const byte X = 0x58;
    private const byte LeftWin = 0x5B;
    private const uint KeyUp = 0x0002;

    public static void Win(byte? key = null)
    {
        keybd_event(LeftWin, 0, 0, UIntPtr.Zero);
        if (key is { } k)
        {
            keybd_event(k, 0, 0, UIntPtr.Zero);
            keybd_event(k, 0, KeyUp, UIntPtr.Zero);
        }

        keybd_event(LeftWin, 0, KeyUp, UIntPtr.Zero);
    }

    /// <summary>A Segoe Fluent Icons character (kept numeric so sources stay ASCII).</summary>
    public static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
