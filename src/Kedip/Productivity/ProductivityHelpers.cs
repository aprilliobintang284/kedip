using System.Globalization;
using System.IO;
using Kedip.Core;

namespace Kedip.Productivity;

/// <summary>Segoe Fluent Icons glyphs used by the productivity features (kept ASCII in source).</summary>
internal static class Glyphs
{
    public static readonly string Play = G(0xE768);
    public static readonly string Pause = G(0xE769);
    public static readonly string PlaySolid = G(0xF5B0);
    public static readonly string PauseSolid = G(0xF8AE);
    public static readonly string Reset = G(0xE777);
    public static readonly string Skip = G(0xE893);
    public static readonly string Undo = G(0xE7A7);
    public static readonly string Stopwatch = G(0xE916);
    public static readonly string Cup = G(0xEC32);
    public static readonly string Drop = G(0xEB42);
    public static readonly string Folder = G(0xE8B7);
    public static readonly string Document = G(0xE8A5);
    public static readonly string Close = G(0xE711);
    public static readonly string Box = G(0xE7B8);

    private static string G(int code) => ((char)code).ToString();
}

/// <summary>Indonesian number formatting ("1.250"), independent of the OS culture.</summary>
internal static class IdFormat
{
    private static readonly NumberFormatInfo Numbers = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = [3],
    };

    public static string Int(int value) => value.ToString("#,0", Numbers);

    /// <summary>"mm:ss", or "h:mm:ss" from one hour up.</summary>
    public static string Clock(TimeSpan value)
    {
        int total = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }
}

/// <summary>
/// Writes a small file atomically on the thread pool. Writes are serialized and coalesced:
/// only the newest pending content is written.
/// </summary>
internal sealed class BackgroundFileWriter(string path)
{
    private readonly Lock _gate = new();
    private string? _pending;
    private Task _running = Task.CompletedTask;

    public void Write(string content)
    {
        lock (_gate)
        {
            bool idle = _pending is null;
            _pending = content;
            if (idle)
            {
                _running = _running.ContinueWith(_ => Drain(), TaskScheduler.Default);
            }
        }
    }

    /// <summary>Waits (bounded) for pending writes; used at shutdown.</summary>
    public void Flush(TimeSpan timeout)
    {
        Task running;
        lock (_gate)
        {
            running = _running;
        }

        try
        {
            running.Wait(timeout);
        }
        catch (AggregateException)
        {
        }
    }

    private void Drain()
    {
        string? content;
        lock (_gate)
        {
            content = _pending;
            _pending = null;
        }

        if (content is null)
        {
            return;
        }

        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write {Path.GetFileName(path)}", ex);
        }
    }
}
