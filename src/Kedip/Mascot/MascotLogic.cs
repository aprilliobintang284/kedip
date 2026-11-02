using Kedip.Core;

namespace Kedip.Mascot;

/// <summary>32-bit millisecond tick arithmetic (the GetTickCount / GetLastInputInfo domain, wraps every ~49.7 days).</summary>
internal static class TickMath
{
    public static uint Now => unchecked((uint)Environment.TickCount);

    /// <summary>Milliseconds from <paramref name="then"/> to <paramref name="now"/>; 0 when <paramref name="then"/> is (slightly) newer.</summary>
    public static uint Elapsed(uint now, uint then)
    {
        uint delta = unchecked(now - then);
        return delta > int.MaxValue ? 0u : delta;
    }
}

/// <summary>Everything one mascot decision needs, sampled at a single instant. Durations are in ms; 0 disables drowsy/sleep.</summary>
internal readonly record struct MascotSnapshot(
    uint Now,
    uint LastInput,
    bool EyesEnabled,
    bool Suppressed,
    bool Desktop,
    uint EyesIdleMs,
    uint DrowsyMs,
    uint SleepMs);

/// <summary>Result of a decision: the mood, whether to play the "hello" animation, and when to look again (negative = no timer).</summary>
internal readonly record struct MascotStep(MascotMood Mood, bool Woke, int NextCheckMs);

/// <summary>
/// The mascot's state machine, free of Win32 and WPF so it can be tested in isolation.
/// Feed it snapshots (on timer ticks and whenever foreground/settings change); it remembers
/// where visibility came from, the last input it saw and the short post-wake "hello" window.
/// </summary>
internal sealed class MascotBrain
{
    /// <summary>Input poll period while the eyes are visible.</summary>
    public const int PollMs = 250;

    /// <summary>How long the eyes stay after waking up inside an app (the EyesControl hello takes ≈ 730 ms).</summary>
    public const int HelloMs = 760;

    /// <summary>Shortest wait while hidden.</summary>
    public const int MinWaitMs = 250;

    /// <summary>Safety re-check period while suppressed (fullscreen app with the notch hidden).</summary>
    public const int SuppressedCheckMs = 5000;

    private bool _primed;
    private uint _lastInput;
    private bool _fromDesktop;
    private bool _hello;
    private uint _helloStart;

    public MascotMood Mood { get; private set; } = MascotMood.Hidden;

    public MascotStep Update(in MascotSnapshot s)
    {
        bool newInput = _primed && s.LastInput != _lastInput;
        _primed = true;
        _lastInput = s.LastInput;
        uint idle = TickMath.Elapsed(s.Now, s.LastInput);

        if (!s.EyesEnabled)
        {
            _hello = false;
            return Set(MascotMood.Hidden, false, -1);
        }

        if (s.Suppressed)
        {
            _hello = false;
            return Set(MascotMood.Hidden, false, SuppressedCheckMs);
        }

        if (_hello)
        {
            if (!s.Desktop)
            {
                uint elapsed = TickMath.Elapsed(s.Now, _helloStart);
                if (elapsed < HelloMs)
                {
                    return Set(MascotMood.Awake, false, (int)Math.Max(15u, HelloMs - elapsed));
                }
            }

            // Hello is over (or the desktop took over): settle normally.
            _hello = false;
            return Steady(s, idle, woke: false);
        }

        bool woke = false;
        if (Mood != MascotMood.Hidden && newInput)
        {
            woke = Mood is MascotMood.Drowsy or MascotMood.Asleep || !_fromDesktop;
            if (woke && !s.Desktop)
            {
                _hello = true;
                _helloStart = s.Now;
                return Set(MascotMood.Awake, true, HelloMs);
            }
        }

        return Steady(s, idle, woke);
    }

    /// <summary>Mood implied by the idle time alone (visible case).</summary>
    public static MascotMood MoodForIdle(uint idleMs, uint drowsyMs, uint sleepMs)
    {
        if (sleepMs > 0 && idleMs >= sleepMs)
        {
            return MascotMood.Asleep;
        }

        return drowsyMs > 0 && idleMs >= drowsyMs ? MascotMood.Drowsy : MascotMood.Awake;
    }

    private MascotStep Steady(in MascotSnapshot s, uint idle, bool woke)
    {
        uint eyesIdle = Math.Max(s.EyesIdleMs, 1u);
        if (!s.Desktop && idle < eyesIdle)
        {
            // Sleep exactly until the idle threshold could be reached; input in between just pushes it out.
            uint wait = Math.Clamp(eyesIdle - idle, (uint)MinWaitMs, int.MaxValue);
            return Set(MascotMood.Hidden, false, (int)wait);
        }

        _fromDesktop = s.Desktop;
        return Set(MoodForIdle(idle, s.DrowsyMs, s.SleepMs), woke, PollMs);
    }

    private MascotStep Set(MascotMood mood, bool woke, int next)
    {
        Mood = mood;
        return new MascotStep(mood, woke, next);
    }
}

/// <summary>Where the notch sits on screen (physical px) and the monitor scale used to convert DIPs.</summary>
internal readonly record struct GazeAnchor(double X, double Y, double Scale);

/// <summary>Pure gaze math: where the eyes are, and where they should look for a cursor position.</summary>
internal static class MascotGaze
{
    public const double EdgeMarginDip = 24;

    // ≈ 700 × 450 px at 125 % scaling; expressed in DIPs so it feels the same at any DPI.
    public const double RangeXDip = 560;
    public const double RangeYDip = 360;

    /// <summary>Centre of the collapsed notch on the primary monitor, in physical pixels.</summary>
    public static GazeAnchor Anchor(
        double monitorLeft, double monitorTop, double monitorRight, double scale,
        NotchPosition position, double notchWidthDip, double notchHeightDip)
    {
        scale = scale > 0 ? scale : 1;
        double half = (EdgeMarginDip + notchWidthDip / 2) * scale;
        double x = position switch
        {
            NotchPosition.Left => monitorLeft + half,
            NotchPosition.Right => monitorRight - half,
            _ => (monitorLeft + monitorRight) / 2,
        };
        return new GazeAnchor(x, monitorTop + notchHeightDip / 2 * scale, scale);
    }

    /// <summary>Normalised gaze (−1..1 each axis) towards a cursor at physical pixel (<paramref name="cursorX"/>, <paramref name="cursorY"/>).</summary>
    public static (double X, double Y) Target(double cursorX, double cursorY, GazeAnchor anchor)
    {
        double dx = (cursorX - anchor.X) / anchor.Scale / RangeXDip;
        double dy = (cursorY - anchor.Y) / anchor.Scale / RangeYDip;
        return (Soft(dx), Soft(dy));
    }

    /// <summary>Clamps to −1..1 with an ease-out knee: lively near the centre, no hard stop at the edge.</summary>
    public static double Soft(double t)
    {
        if (double.IsNaN(t))
        {
            return 0;
        }

        t = Math.Clamp(t, -1, 1);
        return t * (2 - Math.Abs(t));
    }

    /// <summary>One exponential smoothing step; snaps when close enough so it settles exactly.</summary>
    public static double Smooth(double current, double target, double factor)
    {
        double next = current + (target - current) * factor;
        return Math.Abs(target - next) < 0.003 ? target : next;
    }
}
