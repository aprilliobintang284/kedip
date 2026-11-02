using Kedip.Interop;

namespace Kedip.Mascot;

/// <summary>
/// System-wide user input idle time (mouse/keyboard), via GetLastInputInfo.
/// OWNER: B.
/// </summary>
public sealed class IdleWatcher
{
    /// <summary>Tick count (Environment.TickCount, 32-bit) of the last input event.</summary>
    public uint LastInputTick
    {
        get
        {
            var info = new NativeMethods.LASTINPUTINFO { cbSize = 8 };
            return NativeMethods.GetLastInputInfo(ref info) ? info.dwTime : (uint)Environment.TickCount;
        }
    }

    public TimeSpan IdleTime => TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - LastInputTick));
}
