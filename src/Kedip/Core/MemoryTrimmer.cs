using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Kedip.Core;

/// <summary>
/// Gives memory back to Windows after the app was busy (settings window, expanded panels): a compacting GC that
/// decommits free heap, then a working-set trim. Rate-limited, and only runs when the UI thread is idle.
/// </summary>
public static class MemoryTrimmer
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(45);
    private static DispatcherTimer? _timer;
    private static long _due;
    private static long _lastRun = long.MinValue / 2;

    /// <summary>Trims once, <paramref name="delay"/> from now (an earlier pending request wins). UI thread.</summary>
    public static void Request(TimeSpan delay)
    {
        long due = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        if (_timer is { IsEnabled: true } && _due <= due)
        {
            return;
        }

        _timer ??= CreateTimer();
        Schedule(due);
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        timer.Tick += (_, _) => Run();
        return timer;
    }

    private static void Schedule(long due)
    {
        _due = due;
        _timer!.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, due - Environment.TickCount64));
        _timer.Start();
    }

    private static void Run()
    {
        _timer?.Stop();
        long now = Environment.TickCount64;
        long earliest = _lastRun + (long)MinInterval.TotalMilliseconds;
        if (now < earliest)
        {
            Schedule(earliest);
            return;
        }

        _lastRun = now;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception ex)
        {
            Log.Warn($"Memory trim failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
