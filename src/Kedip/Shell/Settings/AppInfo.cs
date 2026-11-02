using System.Diagnostics;
using Kedip.Core;

namespace Kedip.Shell.Settings;

internal static class AppInfo
{
    /// <summary>Assembly version as "major.minor.build".</summary>
    public static string Version { get; } = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.1.0";

    /// <summary>Opens %APPDATA%\Kedip in Explorer without blocking the UI thread.</summary>
    public static void OpenDataFolder()
    {
        string dir = AppPaths.DataDir;
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Could not open the data folder", ex);
            }
        });
    }
}
