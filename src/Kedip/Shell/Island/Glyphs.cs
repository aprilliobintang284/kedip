namespace Kedip.Shell.Island;

/// <summary>Segoe Fluent Icons code points used by the island (kept as numbers so the source stays plain ASCII).</summary>
internal static class Glyphs
{
    public static readonly string Music = Of(0xE8D6);
    public static readonly string Note = Of(0xEC4F);
    public static readonly string Moon = Of(0xE708);
    public static readonly string Timer = Of(0xE916);
    public static readonly string Shelf = Of(0xE7B8);
    public static readonly string Cloud = Of(0xE753);
    public static readonly string Bolt = Of(0xE945);
    public static readonly string Info = Of(0xE946);
    public static readonly string Drop = Of(0xEB42);
    public static readonly string Battery = Of(0xE850);
    public static readonly string Download = Of(0xE896);
    public static readonly string Message = Of(0xE8BD);

    private static string Of(int codePoint) => char.ConvertFromUtf32(codePoint);
}
