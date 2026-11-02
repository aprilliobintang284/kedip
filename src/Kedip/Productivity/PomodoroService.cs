using Kedip.Core;

namespace Kedip.Productivity;

public enum PomodoroPhase { Idle, Focus, ShortBreak, LongBreak }

/// <summary>
/// Focus timer. Ticks at 1 Hz only while running.
/// OWNER: E. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class PomodoroService : IAppService
{
    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;

    public PomodoroService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
    }

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Idle;
    public bool IsRunning { get; private set; }
    public TimeSpan Remaining { get; private set; }
    public TimeSpan PhaseLength { get; private set; }
    public int CompletedFocusSessions { get; private set; }

    /// <summary>Raised on the UI thread on every tick and state change.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
    }

    /// <summary>Starts a focus session (from Idle) or resumes a paused phase.</summary>
    public void StartOrResume()
    {
    }

    public void Pause()
    {
    }

    public void Reset()
    {
    }

    public void Skip()
    {
    }

    public void Dispose()
    {
    }
}
