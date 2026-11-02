using System.IO;
using System.Text;
using System.Windows.Threading;
using System.Xml.Linq;
using Kedip.Core;

namespace Kedip.Notifications;

public sealed record NotificationApp(string AppId, string Name);

/// <summary>
/// Mirrors Windows toast notifications (WhatsApp, Discord, …) into the notch. Reads the user's own notification
/// database read-only whenever it changes; nothing leaves the PC. Which apps show is chosen in the settings.
/// </summary>
public sealed class NotificationService : IAppService
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    // Windows writes the database's WAL through an open handle, so folder notifications arrive late or never.
    // A persistent read-only connection asks "PRAGMA data_version" this often; the table is only read after a commit.
    private static readonly TimeSpan Backstop = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(20);

    private readonly SettingsStore _settings;
    private readonly PeekBus _peeks;
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _dbApps = new(StringComparer.OrdinalIgnoreCase);
    private Dispatcher? _dispatcher;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _backstop;
    private long _lastArrival;
    private WinSqlite? _probe;   // used only while holding _gate
    private long _dataVersion = -1;
    private bool _disposed;

    public NotificationService(SettingsStore settings, PeekBus peeks)
    {
        _settings = settings;
        _peeks = peeks;
    }

    /// <summary>The notification database exists and could be read.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Raised on the UI thread when an app is seen for the first time.</summary>
    public event EventHandler? AppsChanged;

    public void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = PollAsync();
        };
        _backstop = new DispatcherTimer(DispatcherPriority.Background) { Interval = Backstop };
        _backstop.Tick += (_, _) => _ = PollAsync();
        _settings.Changed += OnSettingsChanged;
        ApplyEnabled();
    }

    /// <summary>Apps that have sent notifications (now or earlier), with friendly names. UI thread.</summary>
    public IReadOnlyList<NotificationApp> GetKnownApps()
    {
        var ids = new HashSet<string>(_settings.Current.NotificationSeenApps, StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(_dbApps);
        return ids.Select(id => new NotificationApp(id, AppIdentity.Get(id).Name))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        StopWatching();
        if (_gate.Wait(TimeSpan.FromSeconds(1)))
        {
            _probe?.Dispose();
            _probe = null;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyEnabled();

    private void ApplyEnabled()
    {
        bool wanted = _settings.Current.NotificationsEnabled && !_disposed;
        if (wanted == (_watcher is not null))
        {
            return;
        }

        if (!wanted)
        {
            StopWatching();
            return;
        }

        IsAvailable = File.Exists(DbPath);
        if (!IsAvailable)
        {
            Log.Warn("Notification database not found; notification mirroring is off");
            return;
        }

        // Only notifications arriving from now on are shown.
        _lastArrival = DateTime.UtcNow.ToFileTimeUtc();
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(DbPath)!, "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnDatabaseTouched;
            _watcher.Created += OnDatabaseTouched;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch the notification database", ex);
            _watcher?.Dispose();
            _watcher = null;
        }

        _backstop?.Start();
        Log.Info($"Notification mirroring on (watcher {(_watcher is null ? "off" : "on")})");
        _ = LoadDatabaseAppsAsync();
    }

    private void StopWatching()
    {
        _debounce?.Stop();
        _backstop?.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnDatabaseTouched;
            _watcher.Created -= OnDatabaseTouched;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // Thread-pool thread.
    private void OnDatabaseTouched(object sender, FileSystemEventArgs e) =>
        _dispatcher?.InvokeAsync(() =>
        {
            if (!_disposed && _watcher is not null && _debounce is not null)
            {
                _debounce.Stop();
                _debounce.Start();
            }
        });

    private async Task PollAsync()
    {
        if (_disposed || _watcher is null || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            long since = _lastArrival;
            var found = await Task.Run(() => ReadIfChanged(since)).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (found.Count > 0)
            {
                Log.Info($"Notifications: {found.Count} new");
            }

            foreach (var toast in found)
            {
                _lastArrival = Math.Max(_lastArrival, toast.Arrival);
                Show(toast);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading notifications failed", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadDatabaseAppsAsync()
    {
        try
        {
            var apps = await Task.Run(() =>
            {
                var list = new List<string>();
                using var db = WinSqlite.OpenReadOnly(DbPath);
                db?.Query(
                    "SELECT DISTINCT h.PrimaryId FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast'",
                    r => list.Add(r.Text(0)));
                return list;
            }).ConfigureAwait(true);
            _dbApps.UnionWith(apps.Where(a => a.Length > 0));
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing notification apps failed: {ex.Message}");
        }
    }

    private sealed record Toast(long Arrival, string AppId, string Title, string Body);

    // Thread pool, under _gate.
    private List<Toast> ReadIfChanged(long sinceFileTime)
    {
        _probe ??= WinSqlite.OpenReadOnly(DbPath);
        if (_probe is null)
        {
            return [];
        }

        long? version = _probe.ScalarInt64("PRAGMA data_version");
        if (version is null)
        {
            // The connection went bad (database replaced, service restarted): reopen next time.
            _probe.Dispose();
            _probe = null;
            _dataVersion = -1;
            return [];
        }

        if (version == _dataVersion)
        {
            return [];
        }

        _dataVersion = version.Value;
        return ReadSince(_probe, sinceFileTime);
    }

    private static List<Toast> ReadSince(WinSqlite db, long sinceFileTime)
    {
        var rows = new List<(long Arrival, string AppId, byte[] Payload)>();
        db.Query(
            "SELECT n.ArrivalTime, h.PrimaryId, n.Payload FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
            "WHERE n.Type = 'toast' AND n.ArrivalTime > ?1 ORDER BY n.ArrivalTime",
            r => rows.Add((r.Int64(0), r.Text(1), r.Blob(2))),
            sinceFileTime);

        long oldest = DateTime.UtcNow.Subtract(MaxAge).ToFileTimeUtc();
        var toasts = new List<Toast>(rows.Count);
        foreach (var (arrival, appId, payload) in rows)
        {
            if (arrival < oldest || appId.Length == 0 || ParsePayload(payload) is not { } text)
            {
                continue;
            }

            toasts.Add(new Toast(arrival, appId, text.Title, text.Body));
        }

        return toasts;
    }

    /// <summary>Title and body of a ToastGeneric payload, or null for progress/silent updates and unreadable XML.</summary>
    internal static (string Title, string Body)? ParsePayload(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            string xml = Encoding.UTF8.GetString(payload).TrimEnd('\0');
            var doc = XDocument.Parse(xml);
            if (doc.Root is not { } toast || toast.Name.LocalName != "toast")
            {
                return null;
            }

            var all = doc.Descendants().ToList();
            if (all.Any(e => e.Name.LocalName == "progress"))
            {
                return null;
            }

            var bindings = all.Where(e => e.Name.LocalName == "binding").ToList();
            var binding = bindings.FirstOrDefault(b => string.Equals((string?)b.Attribute("template"), "ToastGeneric", StringComparison.OrdinalIgnoreCase))
                ?? bindings.FirstOrDefault();
            var texts = binding?.Elements()
                .Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("placement") != "attribution")
                .Select(e => Collapse(e.Value))
                .Where(t => t.Length > 0)
                .ToList() ?? [];
            if (texts.Count == 0)
            {
                return null;
            }

            return (texts[0], string.Join(" · ", texts.Skip(1)));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private void Show(Toast toast)
    {
        var s = _settings.Current;
        RememberApp(toast.AppId);
        if (!s.NotificationsEnabled || s.NotificationMutedApps.Contains(toast.AppId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.UtcNow;
        string key = $"{toast.AppId}|{toast.Title}|{toast.Body}";
        if (_recent.TryGetValue(key, out var last) && now - last < DuplicateWindow)
        {
            return;
        }

        _recent[key] = now;
        if (_recent.Count > 64)
        {
            foreach (var stale in _recent.Where(p => now - p.Value > DuplicateWindow).Select(p => p.Key).ToList())
            {
                _recent.Remove(stale);
            }
        }

        var (name, icon) = AppIdentity.Get(toast.AppId);
        string title;
        string subtitle;
        if (s.NotificationShowContent)
        {
            title = toast.Title;
            subtitle = toast.Body.Length > 0 ? $"{name} · {toast.Body}" : name;
        }
        else
        {
            title = name;
            subtitle = "Ada notifikasi baru";
        }

        _peeks.Show(new PeekRequest(PeekKind.Notification, title, subtitle, Duration: TimeSpan.FromSeconds(5), Priority: 6, Image: icon));
    }

    private void RememberApp(string appId)
    {
        if (_settings.Current.NotificationSeenApps.Contains(appId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.Update(st => st.NotificationSeenApps.Add(appId));
        AppsChanged?.Invoke(this, EventArgs.Empty);
    }
}
