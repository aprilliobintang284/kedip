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
            return NativeMethods.GetLastInputInfo(ref info) ? info.dwTime : TickMath.Now;
        }
    }

    /// <summary>Milliseconds since the last input (wrap-safe; never negative).</summary>
    public uint IdleMilliseconds
    {
        get
        {
            // Read the input tick first: input arriving between the two reads must not look like a 49-day idle.
            uint last = LastInputTick;
            return TickMath.Elapsed(TickMath.Now, last);
        }
    }

    public TimeSpan IdleTime => TimeSpan.FromMilliseconds(IdleMilliseconds);
}
