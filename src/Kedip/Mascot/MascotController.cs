using System.Runtime.InteropServices;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Interop;

namespace Kedip.Mascot;

public enum MascotMood { Hidden, Awake, Drowsy, Asleep }

/// <summary>
/// Decides when the eyes are shown and how they feel. See DESIGN.md "Mascot".
/// OWNER: B.
/// </summary>
/// <remarks>
/// One adaptive timer drives the decisions (<see cref="MascotBrain"/>): hidden in an app it sleeps until the idle
/// threshold could be reached; visible it polls GetLastInputInfo at 4 Hz. A second 20 Hz timer samples the cursor
/// only while the eyes are visible, awake, following and the cursor actually moves (or the gaze eases back).
/// </remarks>
public sealed class MascotController : IAppService
{
    private const int LookIntervalMs = 50;
    private const double LookSmoothing = 0.45;
    private const double LookStep = 0.01;
    private const int LookStillTicksBeforeStop = 40;
    private const int ForegroundRefreshMs = 2000;

    private readonly SettingsStore _settings;
    private readonly ForegroundWatcher _foreground;
    private readonly IdleWatcher _idle;
    private readonly MascotBrain _brain = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _lookTimer;
    private bool _started;
    private bool _disposed;
    private bool _evaluating;
    private bool _evaluateAgain;
    private long _lastForegroundRefresh;

    private GazeAnchor? _anchor;
    private double _smoothX;
    private double _smoothY;
    private int _cursorX = int.MinValue;
    private int _cursorY = int.MinValue;
    private int _stillTicks;
    private uint _lookInputTick;

    public MascotController(SettingsStore settings, ForegroundWatcher foreground, IdleWatcher idle)
    {
        _settings = settings;
        _foreground = foreground;
        _idle = idle;
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += OnTimerTick;
        _lookTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(LookIntervalMs),
        };
        _lookTimer.Tick += OnLookTick;
    }

    public MascotMood Mood { get; private set; } = MascotMood.Hidden;

    public bool IsVisible => Mood != MascotMood.Hidden;

    /// <summary>Gaze direction, −1..1 (left/up negative), towards the cursor.</summary>
    public double LookX { get; private set; }

    public double LookY { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Mood"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread when <see cref="LookX"/>/<see cref="LookY"/> change.</summary>
    public event EventHandler? LookChanged;

    /// <summary>Raised on the UI thread when the user returns while the eyes are visible (play the "hello" animation).</summary>
    public event EventHandler? Woke;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _lastForegroundRefresh = Environment.TickCount64;
        _foreground.Changed += OnForegroundChanged;
        _settings.Changed += OnSettingsChanged;
        Evaluate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _foreground.Changed -= OnForegroundChanged;
        _settings.Changed -= OnSettingsChanged;
        _timer.Stop();
        _lookTimer.Stop();
    }

    private void OnForegroundChanged(object? sender, EventArgs e) => Evaluate();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _anchor = null; // position / notch size may have changed
        Evaluate();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        long now = Environment.TickCount64;
        if (now - _lastForegroundRefresh >= ForegroundRefreshMs)
        {
            // Cheap safety net in case a WinEvent was missed; raises Changed (→ Evaluate) only on a real change.
            _lastForegroundRefresh = now;
            _foreground.Refresh();
        }

        Evaluate();
    }

    private void Evaluate()
    {
        if (!_started || _disposed)
        {
            return;
        }

        if (_evaluating)
        {
            // Re-entered from one of our own event handlers: run again once the current pass is done.
            _evaluateAgain = true;
            return;
        }

        _evaluating = true;
        try
        {
            do
            {
                _evaluateAgain = false;
                EvaluateCore();
            }
            while (_evaluateAgain && !_disposed);
        }
        catch (Exception ex)
        {
            Log.Error("Mascot evaluation failed", ex);
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(5);
            _timer.Start();
        }
        finally
        {
            _evaluating = false;
        }
    }

    private void EvaluateCore()
    {
        var s = _settings.Current;
        uint lastInput = _idle.LastInputTick;
        var snapshot = new MascotSnapshot(
            Now: TickMath.Now,
            LastInput: lastInput,
            EyesEnabled: s.IdleStyle == IdleStyle.Eyes,
            Suppressed: s.HideInFullscreen && _foreground.IsFullscreenAppForeground,
            Desktop: s.EyesOnDesktop && _foreground.IsDesktopForeground,
            EyesIdleMs: SecondsToMs(s.EyesIdleSeconds),
            DrowsyMs: MinutesToMs(s.DrowsyAfterMinutes),
            SleepMs: MinutesToMs(s.SleepAfterMinutes));

        var step = _brain.Update(snapshot);
        Schedule(step.NextCheckMs);

        bool moodChanged = step.Mood != Mood;
        Mood = step.Mood;
        UpdateGaze(lastInput, restart: moodChanged || step.Woke);

        if (moodChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (step.Woke)
        {
            Woke?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Schedule(int delayMs)
    {
        if (delayMs < 0)
        {
            _timer.Stop();
            return;
        }

        var interval = TimeSpan.FromMilliseconds(delayMs);
        if (_timer.IsEnabled && _timer.Interval == interval)
        {
            return; // steady polling: keep the running period
        }

        _timer.Stop();
        _timer.Interval = interval;
        _timer.Start();
    }

    private static uint SecondsToMs(int seconds) => (uint)Math.Clamp(seconds * 1000L, 1000L, int.MaxValue);

    private static uint MinutesToMs(int minutes) => minutes <= 0 ? 0u : (uint)Math.Min(minutes * 60_000L, int.MaxValue);

    // ---- Gaze ----

    private bool WantsFollow => Mood == MascotMood.Awake && _settings.Current.EyesFollowCursor;

    private void UpdateGaze(uint lastInput, bool restart)
    {
        bool inputMoved = lastInput != _lookInputTick;
        _lookInputTick = lastInput;

        if (Mood == MascotMood.Hidden)
        {
            // Nobody sees the eyes: drop the gaze at once, no easing frames.
            _lookTimer.Stop();
            _smoothX = 0;
            _smoothY = 0;
            PublishLook(0, 0, exact: true);
            return;
        }

        if (WantsFollow)
        {
            if (!_lookTimer.IsEnabled && (inputMoved || restart))
            {
                _stillTicks = 0;
                _cursorX = int.MinValue;
                _anchor = null; // re-read the monitor each burst: resolution / DPI may have changed meanwhile
                _lookTimer.Start();
                OnLookTick(null, EventArgs.Empty);
            }
        }
        else if (!_lookTimer.IsEnabled && (_smoothX != 0 || _smoothY != 0 || LookX != 0 || LookY != 0))
        {
            // Drowsy, asleep or following turned off: ease the gaze back to the centre, then stop.
            _lookTimer.Start();
        }
    }

    private void OnLookTick(object? sender, EventArgs e)
    {
        try
        {
            bool follow = WantsFollow && !_disposed;
            double targetX = 0;
            double targetY = 0;
            bool cursorMoved = false;
            if (follow)
            {
                if (NativeMethods.GetCursorPos(out var p))
                {
                    _anchor ??= ComputeAnchor(_settings.Current);
                    (targetX, targetY) = MascotGaze.Target(p.X, p.Y, _anchor.Value);
                    cursorMoved = p.X != _cursorX || p.Y != _cursorY;
                    _cursorX = p.X;
                    _cursorY = p.Y;
                }
                else
                {
                    // Secure desktop / lock screen: hold the current gaze.
                    targetX = _smoothX;
                    targetY = _smoothY;
                }
            }

            _smoothX = MascotGaze.Smooth(_smoothX, targetX, LookSmoothing);
            _smoothY = MascotGaze.Smooth(_smoothY, targetY, LookSmoothing);
            bool settled = _smoothX == targetX && _smoothY == targetY;
            PublishLook(_smoothX, _smoothY, exact: settled);

            if (!follow)
            {
                if (settled)
                {
                    _lookTimer.Stop();
                }

                return;
            }

            _stillTicks = cursorMoved || !settled ? 0 : _stillTicks + 1;
            if (_stillTicks >= LookStillTicksBeforeStop)
            {
                // Cursor at rest: stop sampling; the 4 Hz input poll restarts us when anything moves.
                _lookTimer.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mascot gaze update failed", ex);
            _lookTimer.Stop();
        }
    }

    private void PublishLook(double x, double y, bool exact)
    {
        bool big = Math.Abs(x - LookX) > LookStep || Math.Abs(y - LookY) > LookStep;
        bool final = exact && (x != LookX || y != LookY);
        if (!big && !final)
        {
            return;
        }

        LookX = x;
        LookY = y;
        LookChanged?.Invoke(this, EventArgs.Empty);
    }

    private static GazeAnchor ComputeAnchor(AppSettings s)
    {
        var primary = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        double left = 0;
        double top = 0;
        double right = System.Windows.SystemParameters.PrimaryScreenWidth;
        double scale = 1;
        if (primary != IntPtr.Zero && NativeMethods.GetMonitorInfo(primary, ref info))
        {
            left = info.rcMonitor.Left;
            top = info.rcMonitor.Top;
            right = info.rcMonitor.Right;
            if (Native.GetDpiForMonitor(primary, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }
        }

        var (width, height) = s.GetNotchSize();
        return MascotGaze.Anchor(left, top, right, scale, s.Position, width, height);
    }

    private static class Native
    {
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
