using System.Windows.Media;
using Kedip.Core;

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
}

/// <summary>
/// Current Windows media session (Spotify, browsers, ...) via GlobalSystemMediaTransportControlsSessionManager.
/// OWNER: C. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class MediaService : IAppService
{
    /// <summary>Current track, or null when nothing is playing/paused.</summary>
    public MediaInfo? Current { get; private set; }

    /// <summary>Live playback position (extrapolated while playing).</summary>
    public TimeSpan Position => TimeSpan.Zero;

    /// <summary>Raised on the UI thread when the track (or its metadata/art) changes, including to/from null.</summary>
    public event EventHandler? TrackChanged;

    /// <summary>Raised on the UI thread when play/pause state or the timeline changes.</summary>
    public event EventHandler? PlaybackChanged;

    public void Start()
    {
    }

    public Task PlayPauseAsync() => Task.CompletedTask;

    public Task NextAsync() => Task.CompletedTask;

    public Task PreviousAsync() => Task.CompletedTask;

    public Task SeekAsync(TimeSpan position) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
