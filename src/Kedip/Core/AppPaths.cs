using System.IO;

namespace Kedip.Core;

/// <summary>Well-known file locations. Directories are created on first access.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kedip"));

    public static string CacheDir { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kedip"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string WaterFile => Path.Combine(DataDir, "water.json");
    public static string ShelfFile => Path.Combine(DataDir, "shelf.json");
    public static string LogFile => Path.Combine(DataDir, "kedip.log");
    public static string LyricsCacheDir => Ensure(Path.Combine(CacheDir, "lyrics"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
