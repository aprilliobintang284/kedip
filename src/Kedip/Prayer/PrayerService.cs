using Kedip.Core;

namespace Kedip.Prayer;

/// <summary>
/// Today's prayer schedule, the next prayer, and reminders. See DESIGN.md "Prayer".
/// OWNER: D. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class PrayerService : IAppService
{
    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;

    public PrayerService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
    }

    public IReadOnlyList<PrayerTime> Today { get; private set; } = [];

    /// <summary>Next upcoming prayer (may be tomorrow's Subuh), or null when disabled.</summary>
    public PrayerTime? Next { get; private set; }

    /// <summary>Raised on the UI thread when the schedule is recomputed or <see cref="Next"/> changes.</summary>
    public event EventHandler? Updated;

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}
