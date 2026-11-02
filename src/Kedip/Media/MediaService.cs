using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Kedip.Core;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Kedip.Media;

public sealed class MediaInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";

    /// <summary>Source app user model id (e.g. Spotify.exe, chrome.exe).</summary>
    public string SourceAppId { get; init; } = "";

    /// <summary>Frozen album art, or null.</summary>
    public ImageSource? Thumbnail { get; init; }

    public TimeSpan Duration { get; init; }
    public bool IsPlaying { get; init; }

    public bool CanPlayPause { get; init; } = true;
    public bool CanGoNext { get; init; } = true;
    public bool CanGoPrevious { get; init; } = true;

    /// <summary>True when the source app accepts seeking and reports a duration.</summary>
    public bool CanSeek { get; init; }

    /// <summary>Friendly name of the source app ("Spotify", "Chrome", ...).</summary>
    public string SourceAppName => MediaAppNames.Get(SourceAppId);
}

/// <summary>
/// Current Windows media session (Spotify, browsers, ...) via GlobalSystemMediaTransportControlsSessionManager.
/// WinRT callbacks arrive on worker threads; they only mark state dirty and a single UI-thread pump re-reads the
/// session off the UI thread, so updates are applied in order and every public event is raised on the UI thread.
/// </summary>
public sealed class MediaService : IAppService
{
    [Flags]
    private enum Dirty
    {
        None = 0,
        Media = 1,
        Playback = 2,
        Timeline = 4,
        Session = 8,
        AllProperties = Media | Playback | Timeline,
    }

    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan EmptyMetadataGrace = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DriftThreshold = TimeSpan.FromMilliseconds(250);

    // Browsers report hover previews (YouTube thumbnails that start playing under the cursor) as the current media.
    // A different title while a browser is already playing is held back until it stays put this long.
    private static readonly TimeSpan BrowserSettleDelay = TimeSpan.FromMilliseconds(2500);
    private static readonly string[] BrowserNames = ["Chrome", "Edge", "Firefox", "Opera", "Brave"];

    private Dispatcher? _dispatcher;
    private SessionManager? _manager;
    private Session? _session;
    private bool _isFallback;
    private int _sessionGeneration;
    private Windows.Foundation.TypedEventHandler<Session, MediaPropertiesChangedEventArgs>? _onMedia;
    private Windows.Foundation.TypedEventHandler<Session, PlaybackInfoChangedEventArgs>? _onPlayback;
    private Windows.Foundation.TypedEventHandler<Session, TimelinePropertiesChangedEventArgs>? _onTimeline;

    private Dirty _dirty;
    private bool _pumping;
    private bool _started;
    private bool _disposed;
    private DispatcherTimer? _emptyGraceTimer;
    private bool _emptyGraceExpired;

    // Last applied session state (UI thread only).
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _sourceAppId = "";
    private bool _hasMetadata;
    private Status _status = Status.Closed;
    private double _rate = 1;
    private bool _canPlayPause = true;
    private bool _canNext = true;
    private bool _canPrevious = true;
    private bool _canSeekControl;
    private TimeSpan _start;
    private TimeSpan _duration;
    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorTime = DateTimeOffset.UtcNow;
    private (TimeSpan Position, DateTimeOffset Updated, TimeSpan End)? _lastRawTimeline;

    // Album art.
    private ImageSource? _thumbnail;
    private string _thumbnailHash = "";
    private string _trackKey = "";
    private int _thumbnailRequest;

    private DispatcherTimer? _settleTimer;
    private Snapshot? _pending;
    private Session? _pendingSession;
    private bool _committingPending;

    /// <summary>Current track, or null when nothing is playing/paused.</summary>
    public MediaInfo? Current { get; private set; }

    /// <summary>Live playback position (extrapolated while playing), clamped to the duration.</summary>
    public TimeSpan Position
    {
        get
        {
            if (Current is null)
            {
                return TimeSpan.Zero;
            }

            var position = ExtrapolatedPosition(DateTimeOffset.UtcNow);
            if (position < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return _duration > TimeSpan.Zero && position > _duration ? _duration : position;
        }
    }

    /// <summary>Playback speed reported by the source app (1 = normal).</summary>
    public double PlaybackRate => _rate;

    /// <summary>Raised on the UI thread when the track (or its metadata/art) changes, including to/from null.</summary>
    public event EventHandler? TrackChanged;

    /// <summary>Raised on the UI thread when play/pause state or the timeline changes.</summary>
    public event EventHandler? PlaybackChanged;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _ = InitializeAsync();
    }

    public async Task PlayPauseAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            if (!await session.TryTogglePlayPauseAsync())
            {
                _ = _status == Status.Playing ? await session.TryPauseAsync() : await session.TryPlayAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Media play/pause failed: {ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            await session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Media next failed: {ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            await session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Media previous failed: {ex.Message}");
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_session is not { } session || Current is null)
        {
            return;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (_duration > TimeSpan.Zero && position > _duration)
        {
            position = _duration;
        }

        try
        {
            if (await session.TryChangePlaybackPositionAsync((_start + position).Ticks) && _dispatcher is { } dispatcher)
            {
                // Apply locally right away so progress and lyrics follow the seek before the app reports it.
                await dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || !ReferenceEquals(session, _session))
                    {
                        return;
                    }

                    _anchorPosition = position;
                    _anchorTime = DateTimeOffset.UtcNow;
                    RaisePlaybackChanged();
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Media seek failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _emptyGraceTimer?.Stop();
        CancelSettle();
        UnsubscribeSession();
        if (_manager is { } manager)
        {
            try
            {
                manager.CurrentSessionChanged -= OnManagerCurrentSessionChanged;
                manager.SessionsChanged -= OnManagerSessionsChanged;
            }
            catch (Exception ex)
            {
                Log.Warn($"Media manager unsubscribe failed: {ex.Message}");
            }

            _manager = null;
        }

        TrackChanged = null;
        PlaybackChanged = null;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var manager = await SessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            await _dispatcher!.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _manager = manager;
                manager.CurrentSessionChanged += OnManagerCurrentSessionChanged;
                manager.SessionsChanged += OnManagerSessionsChanged;
                MarkDirty(Dirty.Session);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Windows media session manager is unavailable", ex);
        }
    }

    // ---- WinRT callbacks (worker threads) ----

    private void OnManagerCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) =>
        Post(generation: null, Dirty.Session);

    private void OnManagerSessionsChanged(SessionManager sender, SessionsChangedEventArgs args) =>
        Post(generation: null, Dirty.Session);

    private void Post(int? generation, Dirty what)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _disposed)
        {
            return;
        }

        try
        {
            dispatcher.InvokeAsync(() =>
            {
                if (_disposed || (generation is { } g && g != _sessionGeneration))
                {
                    return;
                }

                if ((what & Dirty.Playback) != 0 && _isFallback)
                {
                    // A fallback session that stops playing may hand over to another playing session.
                    what |= Dirty.Session;
                }

                MarkDirty(what);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Media event dispatch failed: {ex.Message}");
        }
    }

    // ---- UI-thread pump ----

    /// <summary>UI thread only.</summary>
    private void MarkDirty(Dirty what)
    {
        _dirty |= what;
        if (!_pumping && _dispatcher is { } dispatcher)
        {
            // Always start the pump inside a dispatcher operation so every await in it resumes on the UI thread.
            _pumping = true;
            dispatcher.InvokeAsync(() => _ = PumpAsync());
        }
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            // Apps tend to fire several events at once (metadata + playback + timeline); handle them in one pass.
            await Task.Delay(CoalesceDelay);
            while (_dirty != Dirty.None && !_disposed)
            {
                var what = _dirty;
                _dirty = Dirty.None;

                if ((what & Dirty.Session) != 0 && _manager is { } manager)
                {
                    string previousApp = _sourceAppId;
                    var (next, fallback) = await Task.Run(() => ChooseSession(manager, previousApp));
                    if (_disposed)
                    {
                        return;
                    }

                    _isFallback = fallback;
                    if (!ReferenceEquals(next, _session))
                    {
                        UnsubscribeSession();
                        _session = next;
                        SubscribeSession(next);
                        _lastRawTimeline = null;
                        _emptyGraceExpired = false;
                        _emptyGraceTimer?.Stop();
                        CancelSettle();
                        what |= Dirty.AllProperties;
                    }
                }

                var session = _session;
                if (session is null)
                {
                    ApplyNoSession();
                    continue;
                }

                if ((what & Dirty.AllProperties) == 0)
                {
                    continue;
                }

                var snapshot = await Task.Run(() => ReadSnapshotAsync(session, what));
                if (_disposed)
                {
                    return;
                }

                if (!ReferenceEquals(session, _session))
                {
                    continue;
                }

                Apply(snapshot);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Media state update failed", ex);
        }
        finally
        {
            _pumping = false;
        }

        // Something may have arrived while the last pass was finishing.
        if (_dirty != Dirty.None && !_disposed)
        {
            MarkDirty(Dirty.None);
        }
    }

    private static (Session? Session, bool Fallback) ChooseSession(SessionManager manager, string previousApp)
    {
        try
        {
            var current = manager.GetCurrentSession();
            if (current is not null)
            {
                return (current, false);
            }

            Session? keep = null;
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    if (session.GetPlaybackInfo()?.PlaybackStatus == Status.Playing)
                    {
                        return (session, true);
                    }

                    // Keep a paused fallback session so pausing does not make the music disappear.
                    if (keep is null && previousApp.Length > 0 &&
                        string.Equals(session.SourceAppUserModelId, previousApp, StringComparison.OrdinalIgnoreCase))
                    {
                        keep = session;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Media session probe failed: {ex.Message}");
                }
            }

            return (keep, keep is not null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media session lookup failed: {ex.Message}");
            return (null, false);
        }
    }

    private static string SafeSourceApp(Session session)
    {
        try
        {
            return session.SourceAppUserModelId ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private sealed class Snapshot
    {
        public string SourceApp = "";
        public bool HasMedia;
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public Windows.Storage.Streams.IRandomAccessStreamReference? Thumbnail;

        public bool HasPlayback;
        public Status Status;
        public double Rate = 1;
        public bool CanPlayPause = true;
        public bool CanNext = true;
        public bool CanPrevious = true;
        public bool CanSeek;

        public bool HasTimeline;
        public TimeSpan Start;
        public TimeSpan End;
        public TimeSpan Position;
        public DateTimeOffset LastUpdated;
    }

    private static async Task<Snapshot> ReadSnapshotAsync(Session session, Dirty what)
    {
        var s = new Snapshot { SourceApp = SafeSourceApp(session) };
        if ((what & Dirty.Media) != 0)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props is not null)
                {
                    s.HasMedia = true;
                    s.Title = Clean(props.Title);
                    s.Artist = Clean(props.Artist);
                    if (s.Artist.Length == 0)
                    {
                        s.Artist = Clean(props.AlbumArtist);
                    }

                    s.Album = Clean(props.AlbumTitle);
                    s.Thumbnail = props.Thumbnail;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Media properties unavailable: {ex.Message}");
            }
        }

        if ((what & Dirty.Playback) != 0)
        {
            try
            {
                var info = session.GetPlaybackInfo();
                if (info is not null)
                {
                    s.HasPlayback = true;
                    s.Status = info.PlaybackStatus;
                    s.Rate = info.PlaybackRate is { } rate && rate > 0 && double.IsFinite(rate) ? rate : 1;
                    if (info.Controls is { } c)
                    {
                        s.CanPlayPause = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
                        s.CanNext = c.IsNextEnabled;
                        s.CanPrevious = c.IsPreviousEnabled;
                        s.CanSeek = c.IsPlaybackPositionEnabled;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Media playback info unavailable: {ex.Message}");
            }
        }

        if ((what & Dirty.Timeline) != 0)
        {
            try
            {
                var t = session.GetTimelineProperties();
                if (t is not null)
                {
                    s.HasTimeline = true;
                    s.Start = t.StartTime;
                    s.End = t.EndTime;
                    s.Position = t.Position;
                    s.LastUpdated = t.LastUpdatedTime;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Media timeline unavailable: {ex.Message}");
            }
        }

        return s;
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private void Apply(Snapshot s)
    {
        string sourceApp = s.SourceApp;
        var now = DateTimeOffset.UtcNow;
        bool trackChanged = false;
        bool playbackChanged = false;

        if (s.HasMedia && ShouldSettle(s))
        {
            // Possibly a hover preview: keep showing the current track (and its timeline) until the new one settles.
            HoldForSettle(s);
            s.HasMedia = false;
            s.HasTimeline = false;
        }
        else if (s.HasMedia)
        {
            CancelSettle();
        }
        else if (s.HasTimeline && _pending is { } held)
        {
            // Timeline updates that arrive while a new title is settling belong to that title.
            CopyTimeline(s, held);
            s.HasTimeline = false;
        }

        if (!string.Equals(sourceApp, _sourceAppId, StringComparison.Ordinal))
        {
            _sourceAppId = sourceApp;
            trackChanged = true;
        }

        if (s.HasPlayback)
        {
            if (s.Status != _status || Math.Abs(s.Rate - _rate) > 0.001)
            {
                // Re-anchor so the extrapolated position neither jumps nor drifts across play/pause.
                _anchorPosition = ExtrapolatedPosition(now);
                _anchorTime = now;
                _status = s.Status;
                _rate = s.Rate;
                playbackChanged = true;
            }

            if (s.CanPlayPause != _canPlayPause || s.CanNext != _canNext || s.CanPrevious != _canPrevious || s.CanSeek != _canSeekControl)
            {
                _canPlayPause = s.CanPlayPause;
                _canNext = s.CanNext;
                _canPrevious = s.CanPrevious;
                _canSeekControl = s.CanSeek;
                playbackChanged = true;
            }
        }

        if (s.HasTimeline)
        {
            var raw = (s.Position, s.LastUpdated, s.End);
            if (_lastRawTimeline != raw)
            {
                _lastRawTimeline = raw;
                var duration = s.End > s.Start ? s.End - s.Start : TimeSpan.Zero;
                if ((duration - _duration).Duration() >= TimeSpan.FromSeconds(1))
                {
                    trackChanged = true;
                }

                _duration = duration;
                _start = s.Start;
                var predicted = ExtrapolatedPosition(now);
                _anchorPosition = s.Position - s.Start;
                _anchorTime = IsSaneTimestamp(s.LastUpdated, now) ? s.LastUpdated : now;
                if ((ExtrapolatedPosition(now) - predicted).Duration() > DriftThreshold)
                {
                    playbackChanged = true;
                }
            }
        }

        if (s.HasMedia)
        {
            bool empty = s.Title.Length == 0 && s.Artist.Length == 0;
            if (empty)
            {
                if (_hasMetadata && !_emptyGraceExpired && _status != Status.Closed)
                {
                    // Apps briefly clear metadata between tracks; keep the old track for a moment to avoid flicker.
                    StartEmptyGrace();
                }
                else if (_hasMetadata)
                {
                    _hasMetadata = false;
                    trackChanged = true;
                }
            }
            else
            {
                _emptyGraceTimer?.Stop();
                _emptyGraceExpired = false;
                if (!_hasMetadata || s.Title != _title || s.Artist != _artist || s.Album != _album)
                {
                    _title = s.Title;
                    _artist = s.Artist;
                    _album = s.Album;
                    _hasMetadata = true;
                    trackChanged = true;
                }

                string key = $"{_sourceAppId}|{_title}|{_artist}|{_album}";
                if (key != _trackKey)
                {
                    _trackKey = key;
                    _thumbnail = null;
                    _thumbnailHash = "";
                    _thumbnailRequest++;
                }

                if (s.Thumbnail is { } reference)
                {
                    // Reload even for the same track: some apps first send the previous track's art.
                    LoadThumbnail(reference, key);
                }
            }
        }

        bool visible = _hasMetadata && _status != Status.Closed;
        bool wasVisible = Current is not null;
        if (!visible)
        {
            if (wasVisible)
            {
                Current = null;
                RaiseTrackChanged();
            }

            return;
        }

        if (trackChanged || playbackChanged || !wasVisible)
        {
            Current = BuildInfo();
        }

        if (trackChanged || !wasVisible)
        {
            RaiseTrackChanged();
        }

        if (playbackChanged || !wasVisible)
        {
            RaisePlaybackChanged();
        }
    }

    private bool ShouldSettle(Snapshot s) =>
        !_committingPending
        && _hasMetadata
        && _status == Status.Playing
        && (s.Title.Length > 0 || s.Artist.Length > 0)
        && (s.Title != _title || s.Artist != _artist)
        && Array.IndexOf(BrowserNames, MediaAppNames.Get(s.SourceApp)) >= 0;

    private void HoldForSettle(Snapshot s)
    {
        bool sameAsHeld = _pending is { } held && held.Title == s.Title && held.Artist == s.Artist;
        var next = new Snapshot
        {
            SourceApp = s.SourceApp,
            HasMedia = true,
            Title = s.Title,
            Artist = s.Artist,
            Album = s.Album,
            Thumbnail = s.Thumbnail,
        };
        if (s.HasTimeline)
        {
            CopyTimeline(s, next);
        }
        else if (sameAsHeld && _pending!.HasTimeline)
        {
            CopyTimeline(_pending, next);
        }

        _pending = next;
        _pendingSession = _session;
        if (sameAsHeld)
        {
            return;
        }

        if (_settleTimer is null)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = BrowserSettleDelay };
            _settleTimer.Tick += OnSettleTick;
        }

        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void OnSettleTick(object? sender, EventArgs e)
    {
        _settleTimer?.Stop();
        var held = _pending;
        _pending = null;
        if (held is null || _disposed || !ReferenceEquals(_pendingSession, _session))
        {
            return;
        }

        _committingPending = true;
        try
        {
            Apply(held);
        }
        catch (Exception ex)
        {
            Log.Error("Applying settled media failed", ex);
        }
        finally
        {
            _committingPending = false;
        }
    }

    private void CancelSettle()
    {
        _settleTimer?.Stop();
        _pending = null;
    }

    private static void CopyTimeline(Snapshot from, Snapshot to)
    {
        to.HasTimeline = true;
        to.Start = from.Start;
        to.End = from.End;
        to.Position = from.Position;
        to.LastUpdated = from.LastUpdated;
    }

    private void ApplyNoSession()
    {
        CancelSettle();
        _sourceAppId = "";
        _hasMetadata = false;
        _status = Status.Closed;
        _title = _artist = _album = "";
        _duration = TimeSpan.Zero;
        _anchorPosition = TimeSpan.Zero;
        _lastRawTimeline = null;
        _trackKey = "";
        _thumbnail = null;
        _thumbnailHash = "";
        _thumbnailRequest++;
        _emptyGraceTimer?.Stop();
        if (Current is not null)
        {
            Current = null;
            RaiseTrackChanged();
        }
    }

    private MediaInfo BuildInfo() => new()
    {
        Title = _title,
        Artist = _artist,
        Album = _album,
        SourceAppId = _sourceAppId,
        Thumbnail = _thumbnail,
        Duration = _duration,
        IsPlaying = _status == Status.Playing,
        CanPlayPause = _canPlayPause,
        CanGoNext = _canNext,
        CanGoPrevious = _canPrevious,
        CanSeek = _canSeekControl && _duration > TimeSpan.Zero,
    };

    private TimeSpan ExtrapolatedPosition(DateTimeOffset now)
    {
        var position = _anchorPosition;
        if (_status == Status.Playing)
        {
            var elapsed = now - _anchorTime;
            if (elapsed > TimeSpan.Zero)
            {
                position += elapsed * _rate;
            }
        }

        return position;
    }

    // Some apps leave LastUpdatedTime at its default or report clocks from the future.
    private static bool IsSaneTimestamp(DateTimeOffset value, DateTimeOffset now) =>
        value.Year > 2000 && value <= now + TimeSpan.FromSeconds(2);

    private void StartEmptyGrace()
    {
        if (_emptyGraceTimer is null)
        {
            _emptyGraceTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = EmptyMetadataGrace };
            _emptyGraceTimer.Tick += (_, _) =>
            {
                _emptyGraceTimer.Stop();
                _emptyGraceExpired = true;
                MarkDirty(Dirty.Media);
            };
        }

        if (!_emptyGraceTimer.IsEnabled)
        {
            _emptyGraceTimer.Start();
        }
    }

    private async void LoadThumbnail(Windows.Storage.Streams.IRandomAccessStreamReference reference, string key)
    {
        int request = ++_thumbnailRequest;
        try
        {
            string knownHash = _thumbnailHash;
            var decoded = await Task.Run(() => MediaArtDecoder.DecodeAsync(reference, knownHash));
            if (_disposed || request != _thumbnailRequest || key != _trackKey || decoded is null)
            {
                return;
            }

            if (decoded.Hash == _thumbnailHash && _thumbnail is not null)
            {
                return;
            }

            _thumbnail = decoded.ToImageSource();
            _thumbnailHash = decoded.Hash;
            if (Current is not null)
            {
                Current = BuildInfo();
                RaiseTrackChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Album art load failed: {ex.Message}");
        }
    }

    private void SubscribeSession(Session? session)
    {
        int generation = ++_sessionGeneration;
        if (session is null)
        {
            return;
        }

        _onMedia = (_, _) => Post(generation, Dirty.Media);
        _onPlayback = (_, _) => Post(generation, Dirty.Playback);
        _onTimeline = (_, _) => Post(generation, Dirty.Timeline);
        try
        {
            session.MediaPropertiesChanged += _onMedia;
            session.PlaybackInfoChanged += _onPlayback;
            session.TimelinePropertiesChanged += _onTimeline;
        }
        catch (Exception ex)
        {
            Log.Warn($"Media session subscribe failed: {ex.Message}");
        }
    }

    private void UnsubscribeSession()
    {
        _sessionGeneration++;
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            if (_onMedia is not null)
            {
                session.MediaPropertiesChanged -= _onMedia;
            }

            if (_onPlayback is not null)
            {
                session.PlaybackInfoChanged -= _onPlayback;
            }

            if (_onTimeline is not null)
            {
                session.TimelinePropertiesChanged -= _onTimeline;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Media session unsubscribe failed: {ex.Message}");
        }

        _onMedia = null;
        _onPlayback = null;
        _onTimeline = null;
    }

    private void RaiseTrackChanged()
    {
        try
        {
            TrackChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("TrackChanged handler failed", ex);
        }
    }

    private void RaisePlaybackChanged()
    {
        try
        {
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("PlaybackChanged handler failed", ex);
        }
    }
}
