using System.Windows.Threading;
using Kedip.Core;

namespace Kedip.Productivity;

public enum PomodoroPhase { Idle, Focus, ShortBreak, LongBreak }

/// <summary>
/// Focus timer. Ticks at 1 Hz only while running and tracks the phase end time (not a countdown),
/// so sleep or UI lag never makes it drift.
/// When a phase ends naturally it peeks (+ chime when <see cref="AppSettings.TimerSound"/>) and advances:
/// breaks start automatically (stepping away needs no click), while the next focus session waits paused
/// until the user presses start (they decide when they are back at the desk).
/// <see cref="Skip"/> moves to the next phase silently and keeps the running/paused state; a skipped focus
/// session is not counted. <see cref="Reset"/> returns to <see cref="PomodoroPhase.Idle"/> and clears the count.
/// </summary>
public sealed class PomodoroService : IAppService
{
    private const string AccentFocus = "#FF453A";
    private const string AccentBreak = "#34C759";

    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;
    private readonly DispatcherTimer _timer;
    private DateTime _endsAtUtc;
    private TimeSpan _pausedExact;

    public PomodoroService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => OnTick();
        PhaseLength = LengthOf(PomodoroPhase.Focus);
        Remaining = PhaseLength;
    }

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Idle;
    public bool IsRunning { get; private set; }

    /// <summary>Whole seconds left in the current phase (the focus length while idle).</summary>
    public TimeSpan Remaining { get; private set; }

    public TimeSpan PhaseLength { get; private set; }
    public int CompletedFocusSessions { get; private set; }

    /// <summary>Local time the running phase ends, or null while idle/paused.</summary>
    public DateTime? EndsAt => IsRunning ? _endsAtUtc.ToLocalTime() : null;

    /// <summary>1-based number of the current (or next) focus session within the cycle.</summary>
    public int CurrentSessionInCycle => CompletedFocusSessions % SessionsPerCycle + 1;

    /// <summary>Focus sessions per cycle (before a long break), from settings.</summary>
    public int SessionsPerCycle => Math.Clamp(_settings.Current.SessionsBeforeLongBreak, 1, 12);

    /// <summary>0..1 of the current phase elapsed (0 while idle).</summary>
    public double Progress => Phase == PomodoroPhase.Idle || PhaseLength <= TimeSpan.Zero
        ? 0
        : Math.Clamp(1 - Remaining.TotalSeconds / PhaseLength.TotalSeconds, 0, 1);

    /// <summary>Raised on the UI thread on every tick and state change.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Starts a focus session (from Idle) or resumes a paused phase.</summary>
    public void StartOrResume()
    {
        if (IsRunning)
        {
            return;
        }

        if (Phase == PomodoroPhase.Idle)
        {
            EnterPhase(PomodoroPhase.Focus);
        }

        if (Remaining <= TimeSpan.Zero)
        {
            Remaining = PhaseLength;
        }

        RunFromNow();
        RaiseChanged();
    }

    public void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        // Pausing in the last instant keeps one second rather than restarting the phase on resume.
        _pausedExact = _endsAtUtc - DateTime.UtcNow;
        if (_pausedExact < TimeSpan.FromSeconds(1))
        {
            _pausedExact = TimeSpan.FromSeconds(1);
        }

        Remaining = WholeSeconds(_pausedExact);
        StopTicking();
        RaiseChanged();
    }

    public void Reset()
    {
        StopTicking();
        CompletedFocusSessions = 0;
        Phase = PomodoroPhase.Idle;
        PhaseLength = LengthOf(PomodoroPhase.Focus);
        Remaining = PhaseLength;
        RaiseChanged();
    }

    public void Skip()
    {
        if (Phase == PomodoroPhase.Idle)
        {
            return;
        }

        bool wasRunning = IsRunning;
        StopTicking();
        EnterPhase(NextPhase(countFocus: false));
        if (wasRunning)
        {
            RunFromNow();
        }

        RaiseChanged();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        StopTicking();
    }

    private void OnTick()
    {
        if (!IsRunning)
        {
            _timer.Stop();
            return;
        }

        TimeSpan left = _endsAtUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            CompletePhase();
            return;
        }

        Remaining = WholeSeconds(left);
        ScheduleNextTick(left);
        RaiseChanged();
    }

    private void CompletePhase()
    {
        StopTicking();
        PomodoroPhase finished = Phase;
        PomodoroPhase next = NextPhase(countFocus: true);
        EnterPhase(next);

        bool autoStart = next != PomodoroPhase.Focus;
        if (autoStart)
        {
            RunFromNow();
        }

        RaiseChanged();
        AnnounceTransition(finished, next);
    }

    private void AnnounceTransition(PomodoroPhase finished, PomodoroPhase next)
    {
        int minutes = (int)Math.Round(PhaseLength.TotalMinutes);
        PeekRequest peek = next switch
        {
            PomodoroPhase.LongBreak => new PeekRequest(PeekKind.Timer, "Istirahat panjang",
                $"{SessionsPerCycle} sesi beres! Rehat {minutes} menit", Glyphs.Cup, Priority: 4, AccentHex: AccentBreak),
            PomodoroPhase.ShortBreak => new PeekRequest(PeekKind.Timer, "Waktunya istirahat",
                $"Rehat {minutes} menit dulu ya", Glyphs.Cup, Priority: 4, AccentHex: AccentBreak),
            _ => new PeekRequest(PeekKind.Timer, "Ayo fokus lagi",
                finished == PomodoroPhase.LongBreak ? "Siklus baru! Tekan mulai kalau siap" : $"Fokus {minutes} menit, tekan mulai kalau siap",
                Glyphs.Stopwatch, Priority: 4, AccentHex: AccentFocus),
        };

        try
        {
            _peeks.Show(peek);
        }
        catch (Exception ex)
        {
            Log.Error("Pomodoro peek failed", ex);
        }

        if (_settings.Current.TimerSound)
        {
            Chime.Play();
        }
    }

    private PomodoroPhase NextPhase(bool countFocus)
    {
        if (Phase != PomodoroPhase.Focus)
        {
            return PomodoroPhase.Focus;
        }

        if (!countFocus)
        {
            return PomodoroPhase.ShortBreak;
        }

        CompletedFocusSessions++;
        return CompletedFocusSessions % SessionsPerCycle == 0 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
    }

    private void EnterPhase(PomodoroPhase phase)
    {
        Phase = phase;
        PhaseLength = LengthOf(phase);
        Remaining = PhaseLength;
        _pausedExact = TimeSpan.Zero;
    }

    private void RunFromNow()
    {
        // Resume from the exact paused remainder so repeated pauses never gain time.
        TimeSpan left = _pausedExact > TimeSpan.Zero && WholeSeconds(_pausedExact) == Remaining ? _pausedExact : Remaining;
        _pausedExact = TimeSpan.Zero;
        IsRunning = true;
        _endsAtUtc = DateTime.UtcNow + left;
        ScheduleNextTick(left);
    }

    /// <summary>Wakes just after the displayed (ceiling) second changes, instead of a drifting fixed 1 s interval.</summary>
    private void ScheduleNextTick(TimeSpan left)
    {
        double seconds = left.TotalSeconds;
        double untilChange = seconds - (Math.Ceiling(seconds) - 1);
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(untilChange + 0.015, 0.05, 1.05));
        _timer.Start();
    }

    private void StopTicking()
    {
        IsRunning = false;
        _timer.Stop();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // A running or partly used phase keeps its length; an untouched phase picks up the new duration.
        if (!IsRunning && Remaining == PhaseLength)
        {
            PhaseLength = LengthOf(Phase == PomodoroPhase.Idle ? PomodoroPhase.Focus : Phase);
            Remaining = PhaseLength;
            _pausedExact = TimeSpan.Zero;
        }

        // Always notify: the cycle length (session dots) may have changed too.
        RaiseChanged();
    }

    private TimeSpan LengthOf(PomodoroPhase phase)
    {
        var s = _settings.Current;
        int minutes = phase switch
        {
            PomodoroPhase.ShortBreak => s.ShortBreakMinutes,
            PomodoroPhase.LongBreak => s.LongBreakMinutes,
            _ => s.FocusMinutes,
        };

        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 180));
    }

    private static TimeSpan WholeSeconds(TimeSpan value) =>
        TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling(value.TotalSeconds)));

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
