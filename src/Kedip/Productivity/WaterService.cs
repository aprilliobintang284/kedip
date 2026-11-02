using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Kedip.Core;
using Microsoft.Win32;

namespace Kedip.Productivity;

/// <summary>
/// Daily water intake and reminders. State lives in water.json and resets at local midnight.
/// Reminders use one-shot timers: the next one is due <see cref="AppSettings.WaterIntervalMinutes"/> after the
/// latest of (last drink, last reminder, app start), clamped into [WaterStartHour, WaterEndHour), and only while
/// below the goal.
/// </summary>
public sealed class WaterService : IAppService
{
    private const int MaxMl = 5000;
    private const string Accent = "#0A84FF";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _midnightTimer;
    private readonly DispatcherTimer _reminderTimer;
    private readonly BackgroundFileWriter _writer = new(AppPaths.WaterFile);
    private readonly DateTime _startedAt = DateTime.Now;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    private DateTime? _lastDrink;
    private DateTime? _lastReminder;
    private int _unsavedDeltaMl;
    private int _unsavedDeltaGlasses;
    private bool _loaded;
    private bool _started;
    private bool _disposed;

    public WaterService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _midnightTimer = new DispatcherTimer(DispatcherPriority.Background);
        _midnightTimer.Tick += (_, _) => OnMidnight();
        _reminderTimer = new DispatcherTimer(DispatcherPriority.Background);
        _reminderTimer.Tick += (_, _) => OnReminderDue();
    }

    public int TodayMl { get; private set; }

    public int GoalMl => _settings.Current.WaterGoalMl;

    public int GlassesToday { get; private set; }

    /// <summary>Size of one glass in ml (from settings, at least 1).</summary>
    public int GlassMl => Math.Max(1, _settings.Current.GlassMl);

    /// <summary>0..1 of today's goal.</summary>
    public double Progress => GoalMl <= 0 ? 1 : Math.Clamp((double)TodayMl / GoalMl, 0, 1);

    /// <summary>When the next reminder will show, or null when none is scheduled.</summary>
    public DateTime? NextReminder { get; private set; }

    /// <summary>Raised on the UI thread when intake changes (or the day rolls over).</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnSettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        ScheduleMidnight();
        Task.Run(ReadFile).ContinueWith(
            t => _dispatcher.InvokeAsync(() => ApplyLoaded(t.Result)), TaskScheduler.Default);
    }

    public void AddGlass()
    {
        RollOverIfNewDay();
        int before = TodayMl;
        TodayMl = Math.Min(MaxMl, TodayMl + GlassMl);
        if (TodayMl == before)
        {
            return;
        }

        GlassesToday++;
        _lastDrink = DateTime.Now;
        Track(TodayMl - before, +1);
    }

    public void UndoGlass()
    {
        RollOverIfNewDay();
        if (TodayMl <= 0 && GlassesToday <= 0)
        {
            return;
        }

        int before = TodayMl;
        TodayMl = Math.Max(0, TodayMl - GlassMl);
        int glassesBefore = GlassesToday;
        GlassesToday = Math.Max(0, GlassesToday - 1);
        Track(TodayMl - before, GlassesToday - glassesBefore);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _midnightTimer.Stop();
        _reminderTimer.Stop();
        if (_started)
        {
            _settings.Changed -= OnSettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.TimeChanged -= OnTimeChanged;
        }

        _writer.Flush(TimeSpan.FromSeconds(2));
    }

    private void Track(int deltaMl, int deltaGlasses)
    {
        if (!_loaded)
        {
            // Merged with the stored values when the initial load finishes.
            _unsavedDeltaMl += deltaMl;
            _unsavedDeltaGlasses += deltaGlasses;
        }
        else
        {
            Save();
        }

        ScheduleReminder();
        RaiseChanged();
    }

    // Thread pool.
    private static WaterFileData? ReadFile()
    {
        try
        {
            return File.Exists(AppPaths.WaterFile)
                ? JsonSerializer.Deserialize<WaterFileData>(File.ReadAllText(AppPaths.WaterFile), JsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error("water.json is unreadable; starting from zero", ex);
            return null;
        }
    }

    // UI thread.
    private void ApplyLoaded(WaterFileData? data)
    {
        if (_disposed)
        {
            return;
        }

        _loaded = true;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (data is not null
            && DateOnly.TryParseExact(data.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && date == today)
        {
            TodayMl = Math.Clamp(data.Ml + _unsavedDeltaMl, 0, MaxMl);
            GlassesToday = Math.Max(0, data.Glasses + _unsavedDeltaGlasses);
        }

        if (data?.LastDrink is { } lastDrink && (_lastDrink is null || lastDrink > _lastDrink))
        {
            _lastDrink = lastDrink;
        }

        _date = today;
        if (_unsavedDeltaMl != 0 || _unsavedDeltaGlasses != 0)
        {
            Save();
        }

        _unsavedDeltaMl = 0;
        _unsavedDeltaGlasses = 0;
        ScheduleReminder();
        RaiseChanged();
    }

    private void Save()
    {
        var data = new WaterFileData
        {
            Date = _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Ml = TodayMl,
            Glasses = GlassesToday,
            LastDrink = _lastDrink,
        };
        _writer.Write(JsonSerializer.Serialize(data, JsonOptions));
    }

    /// <summary>Resets the counters when the local date changed (midnight, sleep across midnight, clock change).</summary>
    private bool RollOverIfNewDay()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _date)
        {
            return false;
        }

        _date = today;
        TodayMl = 0;
        GlassesToday = 0;
        _lastReminder = null;
        if (_loaded)
        {
            Save();
        }

        return true;
    }

    private void OnMidnight()
    {
        _midnightTimer.Stop();
        if (RollOverIfNewDay())
        {
            RaiseChanged();
        }

        ScheduleMidnight();
        ScheduleReminder();
    }

    private void ScheduleMidnight()
    {
        _midnightTimer.Stop();
        TimeSpan untilMidnight = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(2);
        _midnightTimer.Interval = untilMidnight > TimeSpan.Zero ? untilMidnight : TimeSpan.FromSeconds(2);
        _midnightTimer.Start();
    }

    private void ScheduleReminder()
    {
        _reminderTimer.Stop();
        NextReminder = ComputeNextReminder(DateTime.Now);
        if (NextReminder is { } due)
        {
            TimeSpan wait = due - DateTime.Now;
            _reminderTimer.Interval = wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
            _reminderTimer.Start();
        }
    }

    private DateTime? ComputeNextReminder(DateTime now)
    {
        var s = _settings.Current;
        if (!s.WaterEnabled || !_loaded || TodayMl >= GoalMl)
        {
            return null;
        }

        return NextReminderTime(now, s.WaterStartHour, s.WaterEndHour, s.WaterIntervalMinutes,
            Latest(_lastDrink, _lastReminder, _startedAt));
    }

    /// <summary>
    /// Pure scheduling rule: <paramref name="anchor"/> + interval, no earlier than the start of the active window,
    /// at least 30 s from now (an overdue reminder after sleep/startup comes shortly, not instantly), and before the
    /// window ends. Outside today's window the first reminder is tomorrow's window start.
    /// </summary>
    internal static DateTime? NextReminderTime(DateTime now, int startHour, int endHour, int intervalMinutes, DateTime anchor)
    {
        int start = Math.Clamp(startHour, 0, 23);
        int end = Math.Clamp(endHour, start + 1, 24);
        TimeSpan interval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 5, 24 * 60));

        for (int dayOffset = 0; dayOffset <= 1; dayOffset++)
        {
            DateTime day = now.Date.AddDays(dayOffset);
            DateTime windowStart = day.AddHours(start);
            DateTime windowEnd = day.AddHours(end);

            DateTime due = anchor + interval;
            if (due < windowStart)
            {
                due = windowStart;
            }

            DateTime earliest = now + TimeSpan.FromSeconds(30);
            if (due < earliest)
            {
                due = earliest;
            }

            if (due < windowEnd)
            {
                return due;
            }
        }

        return null;
    }

    private void OnReminderDue()
    {
        _reminderTimer.Stop();
        RollOverIfNewDay();
        var s = _settings.Current;
        DateTime now = DateTime.Now;
        int start = Math.Clamp(s.WaterStartHour, 0, 23);
        bool inWindow = now.Hour >= start && now.Hour < Math.Clamp(s.WaterEndHour, start + 1, 24);
        if (s.WaterEnabled && _loaded && TodayMl < GoalMl && inWindow)
        {
            _lastReminder = now;
            try
            {
                _peeks.Show(new PeekRequest(PeekKind.Water, "Waktunya minum",
                    $"{IdFormat.Int(TodayMl)} / {IdFormat.Int(GoalMl)} ml", Glyphs.Drop, Priority: 3, AccentHex: Accent));
            }
            catch (Exception ex)
            {
                Log.Error("Water peek failed", ex);
            }
        }

        ScheduleReminder();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ScheduleReminder();
        RaiseChanged();
    }

    // SystemEvents handlers run on the SystemEvents thread.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Application.Current?.Dispatcher.InvokeAsync(OnClockJump, DispatcherPriority.Background);
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(OnClockJump, DispatcherPriority.Background);

    private void OnClockJump()
    {
        if (_disposed)
        {
            return;
        }

        if (RollOverIfNewDay())
        {
            RaiseChanged();
        }

        ScheduleMidnight();
        ScheduleReminder();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static DateTime Latest(DateTime? a, DateTime? b, DateTime c)
    {
        DateTime latest = c;
        if (a > latest)
        {
            latest = a.Value;
        }

        if (b > latest)
        {
            latest = b.Value;
        }

        return latest;
    }

    private sealed class WaterFileData
    {
        public string Date { get; set; } = "";
        public int Ml { get; set; }
        public int Glasses { get; set; }
        public DateTime? LastDrink { get; set; }
    }
}
