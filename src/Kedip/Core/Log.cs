using System.IO;

namespace Kedip.Core;

/// <summary>Minimal thread-safe file log (%APPDATA%\Kedip\kedip.log, rotated at 1 MB).</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(file.FullName, AppPaths.LogFile + ".1", overwrite: true);
                }

                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
