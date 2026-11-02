using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Productivity;
using Microsoft.Win32;

namespace Kedip.Prayer;

/// <summary>
/// Today's prayer schedule, the next prayer, and reminders. See DESIGN.md "Prayer".
/// Times live on the clock of the configured UTC offset (<see cref="Now"/>), independent of the PC's time zone.
/// A single one-shot timer sleeps until the next prayer or the next local midnight, whichever comes first.
/// </summary>
public sealed class PrayerService : IAppService
{
    private const string AccentHex = "#34C759";
    private const string SunGlyph = "\uE706";
    private const string MoonGlyph = "\uE708";
    private const int PeekPriority = 5;

    private static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MidnightMargin = TimeSpan.FromSeconds(5);

    // Re-check the wall clock at least this often so tick-count drift or clock adjustments cannot delay a reminder much.
    private static readonly TimeSpan MaxTimerInterval = TimeSpan.FromMinutes(30);

    // A reminder that is this late (e.g. the PC was asleep) is skipped instead of shown.
    private static readonly TimeSpan ReminderGrace = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;
    private readonly DispatcherTimer _timer;
    private ScheduleKey _key;
    private DateOnly _date;
    private IReadOnlyList<PrayerTime>? _tomorrow;
    private DateTime _firedUpTo = DateTime.MinValue;
    private bool _started;
    private bool _disposed;

    public PrayerService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += OnTimerTick;
    }

    public IReadOnlyList<PrayerTime> Today { get; private set; } = [];

    /// <summary>Tomorrow's schedule (computed on demand, cached until the next recompute); empty when disabled.</summary>
    public IReadOnlyList<PrayerTime> Tomorrow =>
        !_settings.Current.PrayerEnabled || Today.Count == 0 ? [] : (_tomorrow ??= ComputeSafe(_date.AddDays(1), _settings.Current));

    /// <summary>Next upcoming prayer (may be tomorrow's Subuh), or null when disabled.</summary>
    public PrayerTime? Next { get; private set; }

    /// <summary>True when <see cref="Next"/> is tomorrow's Subuh (today's Isya has passed).</summary>
    public bool IsNextTomorrow => Next is not null && DateOnly.FromDateTime(Next.Time) != _date;

    /// <summary>Whether prayer times are enabled in settings.</summary>
    public bool IsEnabled => _settings.Current.PrayerEnabled;

    /// <summary>Display name of the configured location.</summary>
    public string CityName => FormatCity(_settings.Current);

    /// <summary>Current time on the prayer location's clock (UTC + configured offset), comparable with <see cref="PrayerTime.Time"/>.</summary>
    public DateTime Now => DateTime.SpecifyKind(DateTime.UtcNow.AddHours(ClampedTimeZone(_settings.Current)), DateTimeKind.Unspecified);

    /// <summary>Raised on the UI thread when the schedule is recomputed or <see cref="Next"/> changes.</summary>
    public event EventHandler? Updated;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnSettingsChanged;
        try
        {
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"PrayerService: system events unavailable ({ex.Message})");
        }

        Refresh(recompute: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _settings.Changed -= OnSettingsChanged;
        if (_started)
        {
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    /// <summary>Indonesian display name of a schedule entry ("Subuh", "Dzuhur", ...).</summary>
    public static string DisplayName(PrayerName name) => name.ToString();

    /// <summary>IconFont glyph for a schedule entry: sun for daytime entries, crescent otherwise.</summary>
    public static string GlyphFor(PrayerName name) =>
        name is PrayerName.Terbit or PrayerName.Dzuhur or PrayerName.Ashar ? SunGlyph : MoonGlyph;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (ScheduleKey.From(_settings.Current) != _key)
        {
            Refresh(recompute: true);
        }
    }

    // SystemEvents may call back on its own thread; always hop to the UI thread.
    private void OnSystemTimeChanged(object? sender, EventArgs e) => PostRefresh("time changed");

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            PostRefresh("resume");
        }
    }

    private void PostRefresh(string reason)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            Log.Info($"PrayerService: recompute ({reason})");
            Refresh(recompute: true);
        });
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed)
        {
            return;
        }

        try
        {
            var now = Now;
            if (Next is { } due && now >= due.Time.AddSeconds(-1))
            {
                _firedUpTo = due.Time;
                if (now - due.Time <= ReminderGrace)
                {
                    Remind(due);
                }
            }

            Refresh(recompute: DateOnly.FromDateTime(now) != _date);
        }
        catch (Exception ex)
        {
            Log.Error("PrayerService tick failed", ex);
            Schedule(Now);
        }
    }

    /// <summary>Recomputes (when needed) the schedule, re-evaluates <see cref="Next"/>, re-arms the timer and notifies.</summary>
    private void Refresh(bool recompute)
    {
        _timer.Stop();
        var settings = _settings.Current;
        var key = ScheduleKey.From(settings);
        bool keyChanged = key != _key;
        _key = key;

        if (!settings.PrayerEnabled)
        {
            bool hadData = Today.Count > 0 || Next is not null;
            Today = [];
            Next = null;
            _tomorrow = null;
            _date = default;
            if (hadData || keyChanged)
            {
                RaiseUpdated();
            }

            return;
        }

        var now = Now;
        var date = DateOnly.FromDateTime(now);
        bool changed = keyChanged;
        if (recompute || keyChanged || date != _date || Today.Count == 0)
        {
            Today = ComputeSafe(date, settings);
            _date = date;
            _tomorrow = null;
            changed = true;
        }

        // The clock (or offset) moved backwards past a reminder that already fired: forget it.
        if (_firedUpTo > now.AddMinutes(1))
        {
            _firedUpTo = DateTime.MinValue;
        }

        var next = FindNext(now > _firedUpTo ? now : _firedUpTo, settings);
        if (next != Next)
        {
            Next = next;
            changed = true;
        }

        Schedule(now);
        if (changed)
        {
            RaiseUpdated();
        }
    }

    private PrayerTime? FindNext(DateTime after, AppSettings settings)
    {
        foreach (var prayer in Today)
        {
            if (prayer.IsPrayer && prayer.Time > after)
            {
                return prayer;
            }
        }

        if (Today.Count == 0)
        {
            return null;
        }

        _tomorrow ??= ComputeSafe(_date.AddDays(1), settings);
        var subuh = _tomorrow.FirstOrDefault(p => p.Name == PrayerName.Subuh);
        return subuh is not null && subuh.Time > after ? subuh : null;
    }

    private void Schedule(DateTime now)
    {
        _timer.Stop();
        if (_disposed || !_settings.Current.PrayerEnabled)
        {
            return;
        }

        var nextMidnight = _date == default
            ? now.Date.AddDays(1)
            : _date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var target = nextMidnight + MidnightMargin;
        if (Next is { } next && next.Time < target)
        {
            target = next.Time;
        }

        var wait = target - now;
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }
        else if (wait > MaxTimerInterval)
        {
            wait = MaxTimerInterval;
        }

        _timer.Interval = wait;
        _timer.Start();
    }

    private void Remind(PrayerTime due)
    {
        var settings = _settings.Current;
        if (!settings.PrayerReminder)
        {
            return;
        }

        string name = DisplayName(due.Name);
        string glyph = GlyphFor(due.Name);
        string subtitle = $"{FormatCity(settings)} · {due.Time.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        try
        {
            _peeks.Show(new PeekRequest(
                PeekKind.Prayer,
                $"Waktu {name}",
                subtitle,
                glyph,
                PeekDuration,
                PeekPriority,
                AccentHex));
        }
        catch (Exception ex)
        {
            Log.Error("Prayer peek failed", ex);
        }

        if (settings.PrayerSound)
        {
            try
            {
                Chime.Play();
            }
            catch (Exception ex)
            {
                Log.Error("Prayer chime failed", ex);
            }
        }
    }

    private void RaiseUpdated()
    {
        try
        {
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("PrayerService.Updated handler failed", ex);
        }
    }

    private static IReadOnlyList<PrayerTime> ComputeSafe(DateOnly date, AppSettings settings)
    {
        try
        {
            return PrayerCalculator.Compute(
                date, settings.Latitude, settings.Longitude, ClampedTimeZone(settings), settings.IhtiyatMinutes, HorizonFor(settings));
        }
        catch (Exception ex)
        {
            Log.Error($"Prayer times could not be computed for {date:yyyy-MM-dd}", ex);
            return [];
        }
    }

    /// <summary>The preset's Kemenag horizon when the configured location is (still) that preset city.</summary>
    private static double HorizonFor(AppSettings settings)
    {
        var city = Cities.Find(settings.PrayerCity);
        bool samePlace = city is not null
            && Math.Abs(city.Latitude - settings.Latitude) < 0.05
            && Math.Abs(city.Longitude - settings.Longitude) < 0.05;
        return samePlace ? city!.HorizonAltitude : PrayerCalculator.HorizonAngle;
    }

    private static double ClampedTimeZone(AppSettings settings) =>
        double.IsFinite(settings.TimeZoneHours) ? Math.Clamp(settings.TimeZoneHours, -14, 14) : 7;

    private static string FormatCity(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.PrayerCity)
            ? string.Create(CultureInfo.InvariantCulture, $"{settings.Latitude:0.###}, {settings.Longitude:0.###}")
            : settings.PrayerCity.Trim();

    /// <summary>The settings that affect the schedule or its display.</summary>
    private readonly record struct ScheduleKey(bool Enabled, string City, double Latitude, double Longitude, double TimeZone, int Ihtiyat)
    {
        public static ScheduleKey From(AppSettings s) =>
            new(s.PrayerEnabled, s.PrayerCity ?? "", s.Latitude, s.Longitude, s.TimeZoneHours, s.IhtiyatMinutes);
    }
}
