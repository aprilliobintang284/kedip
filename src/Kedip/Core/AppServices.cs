using Kedip.Mascot;
using Kedip.Media;
using Kedip.Power;
using Kedip.Prayer;
using Kedip.Productivity;

namespace Kedip.Core;

/// <summary>A long-lived background service. Created and started on the UI thread.</summary>
public interface IAppService : IDisposable
{
    void Start();
}

/// <summary>Composition root handed to the shell, panels, tray and settings window.</summary>
public sealed class AppServices : IDisposable
{
    public required SettingsStore Settings { get; init; }
    public required PeekBus Peeks { get; init; }
    public required ForegroundWatcher Foreground { get; init; }
    public required IdleWatcher Idle { get; init; }
    public required MascotController Mascot { get; init; }
    public required MediaService Media { get; init; }
    public required LyricsService Lyrics { get; init; }
    public required PrayerService Prayer { get; init; }
    public required PowerService Power { get; init; }
    public required PomodoroService Pomodoro { get; init; }
    public required WaterService Water { get; init; }
    public required ShelfService Shelf { get; init; }

    private IEnumerable<IAppService> All =>
        [Foreground, Mascot, Media, Lyrics, Prayer, Power, Pomodoro, Water, Shelf];

    /// <summary>Starts every service; one failing service is logged and does not stop the others.</summary>
    public void StartAll()
    {
        foreach (var service in All)
        {
            try
            {
                service.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"{service.GetType().Name} failed to start", ex);
            }
        }
    }

    public void Dispose()
    {
        foreach (var service in All.Reverse())
        {
            try
            {
                service.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"{service.GetType().Name} failed to dispose", ex);
            }
        }
    }
}
