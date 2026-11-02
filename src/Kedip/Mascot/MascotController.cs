using Kedip.Core;

namespace Kedip.Mascot;

public enum MascotMood { Hidden, Awake, Drowsy, Asleep }

/// <summary>
/// Decides when the eyes are shown and how they feel. See DESIGN.md "Mascot".
/// OWNER: B. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class MascotController : IAppService
{
    private readonly SettingsStore _settings;
    private readonly ForegroundWatcher _foreground;
    private readonly IdleWatcher _idle;

    public MascotController(SettingsStore settings, ForegroundWatcher foreground, IdleWatcher idle)
    {
        _settings = settings;
        _foreground = foreground;
        _idle = idle;
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
    }

    public void Dispose()
    {
    }
}
