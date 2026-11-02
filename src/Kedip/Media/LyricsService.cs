using System.Windows.Threading;
using Kedip.Core;

namespace Kedip.Media;

public enum LyricsStatus
{
    /// <summary>Lyrics (or music) are turned off in settings.</summary>
    Off,

    /// <summary>No track with a title is playing.</summary>
    NoTrack,

    /// <summary>A lookup is pending or running.</summary>
    Searching,

    /// <summary>Synced lines are available in <see cref="LyricsService.Lines"/>.</summary>
    Found,

    /// <summary>No synced lyrics for this track (not found, plain only, instrumental, or offline).</summary>
    NotFound,
}

/// <summary>
/// Synced lyrics for the current track from LRCLIB, with a disk cache.
/// The current line is tracked with a one-shot timer scheduled to the next line's time (no polling).
/// </summary>
public sealed class LyricsService : IAppService
{
    private static readonly TimeSpan LookupDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MinLineWait = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LineWaitSlack = TimeSpan.FromMilliseconds(15);
    private const int MaxDurationWaits = 3;

    // Never equal to a real key, so the next track check always re-evaluates.
    private const string ForceRefreshKey = "\0";

    private readonly SettingsStore _settings;
    private readonly MediaService _media;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _lineTimer;
    private CancellationTokenSource? _lookupCts;
    private string _trackKey = "";
    private int _durationWaits;
    private bool _enabled;
    private bool _started;

    public LyricsService(SettingsStore settings, MediaService media)
    {
        _settings = settings;
        _media = media;
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LookupDebounce };
        _debounceTimer.Tick += OnDebounceTick;
        _lineTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _lineTimer.Tick += OnLineTick;
    }

    /// <summary>What the lyrics area should show for the current track.</summary>
    public LyricsStatus Status { get; private set; } = LyricsStatus.Off;

    /// <summary>Synced lines of the current track, or null when none are available.</summary>
    public IReadOnlyList<LyricLine>? Lines { get; private set; }

    /// <summary>Index into <see cref="Lines"/> for the current playback position, or −1.</summary>
    public int CurrentIndex { get; private set; } = -1;

    public LyricLine? CurrentLine => Lines is { } lines && CurrentIndex >= 0 && CurrentIndex < lines.Count ? lines[CurrentIndex] : null;

    /// <summary>The line after <see cref="CurrentLine"/> (the first line before lyrics start), or null.</summary>
    public LyricLine? NextLine => Lines is { } lines && CurrentIndex + 1 < lines.Count ? lines[CurrentIndex + 1] : null;

    /// <summary>
    /// Raised on the UI thread when <see cref="Lines"/> or <see cref="Status"/> changes (always followed by <see cref="LineChanged"/>).
    /// </summary>
    public event EventHandler? LyricsChanged;

    /// <summary>Raised on the UI thread when <see cref="CurrentIndex"/> changes.</summary>
    public event EventHandler? LineChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _enabled = IsEnabled(_settings.Current);
        _media.TrackChanged += OnTrackChanged;
        _media.PlaybackChanged += OnPlaybackChanged;
        _settings.Changed += OnSettingsChanged;
        _trackKey = ForceRefreshKey;
        OnTrackChanged(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _media.TrackChanged -= OnTrackChanged;
        _media.PlaybackChanged -= OnPlaybackChanged;
        _settings.Changed -= OnSettingsChanged;
        _debounceTimer.Stop();
        _lineTimer.Stop();
        CancelLookup();
        LyricsChanged = null;
        LineChanged = null;
    }

    private static bool IsEnabled(AppSettings s) => s.LyricsEnabled && s.MusicEnabled;

    private static string TrackKey(MediaInfo info) =>
        $"{info.Artist.Trim()}|{info.Title.Trim()}|{info.Album.Trim()}".ToLowerInvariant();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        bool enabled = IsEnabled(_settings.Current);
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        _trackKey = ForceRefreshKey;
        OnTrackChanged(this, EventArgs.Empty);
    }

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        try
        {
            var info = _media.Current;
            string key = _enabled && info is not null && !string.IsNullOrWhiteSpace(info.Title) ? TrackKey(info) : "";
            if (key == _trackKey)
            {
                UpdateCurrentLine();
                return;
            }

            _trackKey = key;
            _debounceTimer.Stop();
            CancelLookup();
            var status = !_enabled ? LyricsStatus.Off
                : key.Length == 0 ? LyricsStatus.NoTrack
                : LyricsStatus.Searching;
            SetState(null, status);
            if (key.Length > 0)
            {
                _durationWaits = 0;
                _debounceTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Lyrics track change failed", ex);
        }
    }

    private void OnPlaybackChanged(object? sender, EventArgs e)
    {
        try
        {
            UpdateCurrentLine();
        }
        catch (Exception ex)
        {
            Log.Error("Lyrics playback update failed", ex);
        }
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        var info = _media.Current;
        if (info is null || !_enabled || TrackKey(info) != _trackKey)
        {
            return;
        }

        // The timeline (duration) often lands a moment after the metadata; it matters for matching, so wait a little.
        if (info.Duration <= TimeSpan.Zero && _durationWaits < MaxDurationWaits)
        {
            _durationWaits++;
            _debounceTimer.Start();
            return;
        }

        _ = LookupAsync(info, _trackKey);
    }

    private async Task LookupAsync(MediaInfo info, string key)
    {
        CancelLookup();
        var cts = new CancellationTokenSource();
        _lookupCts = cts;
        var request = new LyricsRequest(info.Artist, info.Title, info.Album, info.Duration, info.SourceAppId, _settings.Current.LyricsExtraSources);
        string cacheDir;
        try
        {
            cacheDir = AppPaths.LyricsCacheDir;
        }
        catch (Exception ex)
        {
            Log.Error("Lyrics cache directory unavailable", ex);
            return;
        }

        IReadOnlyList<LyricLine>? lines = null;
        try
        {
            lines = await Task.Run(async () =>
            {
                var result = await LrclibClient.GetAsync(request, cacheDir, cts.Token).ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                if (!result.FromCache)
                {
                    string outcome = result.Synced is not null ? "synced" : result.Instrumental ? "instrumental" : result.Plain is not null ? "plain only" : "not found";
                    Log.Info($"Lyrics lookup: {outcome}");
                }

                if (result.Synced is null)
                {
                    return null;
                }

                var parsed = LrcParser.Parse(result.Synced);
                return parsed.Count > 0 ? parsed : null;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Lyrics lookup failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_lookupCts, cts))
            {
                _lookupCts = null;
            }

            cts.Dispose();
        }

        if (!_started || !_enabled || key != _trackKey || _media.Current is null)
        {
            return;
        }

        SetState(lines, lines is null ? LyricsStatus.NotFound : LyricsStatus.Found);
    }

    private void CancelLookup()
    {
        var cts = _lookupCts;
        _lookupCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetState(IReadOnlyList<LyricLine>? lines, LyricsStatus status)
    {
        if (lines is null && Lines is null && status == Status)
        {
            UpdateCurrentLine();
            return;
        }

        Lines = lines;
        Status = status;
        CurrentIndex = -1;
        _lineTimer.Stop();
        Raise(LyricsChanged, nameof(LyricsChanged));
        UpdateCurrentLine(forceEvent: true);
    }

    private void OnLineTick(object? sender, EventArgs e)
    {
        _lineTimer.Stop();
        try
        {
            UpdateCurrentLine();
        }
        catch (Exception ex)
        {
            Log.Error("Lyrics line update failed", ex);
        }
    }

    /// <summary>Recomputes <see cref="CurrentIndex"/> and schedules the timer for the next line while playing.</summary>
    private void UpdateCurrentLine(bool forceEvent = false)
    {
        _lineTimer.Stop();
        int index = -1;
        var info = _media.Current;
        if (Lines is { Count: > 0 } lines && info is not null)
        {
            var position = _media.Position;
            index = FindLineIndex(lines, position);

            if (info.IsPlaying && index + 1 < lines.Count)
            {
                var nextTime = lines[index + 1].Time;
                bool beyondEnd = info.Duration > TimeSpan.Zero && nextTime > info.Duration;
                if (!beyondEnd)
                {
                    double rate = _media.PlaybackRate > 0 ? _media.PlaybackRate : 1;
                    var wait = (nextTime - position) / rate + LineWaitSlack;
                    _lineTimer.Interval = wait < MinLineWait ? MinLineWait : wait;
                    _lineTimer.Start();
                }
            }
        }

        if (index != CurrentIndex || forceEvent)
        {
            CurrentIndex = index;
            Raise(LineChanged, nameof(LineChanged));
        }
    }

    private void Raise(EventHandler? handler, string name)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error($"{name} handler failed", ex);
        }
    }

    /// <summary>Index of the last line whose time is ≤ <paramref name="position"/>, or −1.</summary>
    internal static int FindLineIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0;
        int hi = lines.Count - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}
