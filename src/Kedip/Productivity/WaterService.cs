using Kedip.Core;

namespace Kedip.Productivity;

/// <summary>
/// Daily water intake and reminders.
/// OWNER: E. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class WaterService : IAppService
{
    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;

    public WaterService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
    }

    public int TodayMl { get; private set; }

    public int GoalMl => _settings.Current.WaterGoalMl;

    public int GlassesToday { get; private set; }

    /// <summary>Raised on the UI thread when intake changes (or the day rolls over).</summary>
    public event EventHandler? Changed;

    public void Start()
    {
    }

    public void AddGlass()
    {
    }

    public void UndoGlass()
    {
    }

    public void Dispose()
    {
    }
}
