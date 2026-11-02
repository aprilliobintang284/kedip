using Kedip.Core;

namespace Kedip.Media;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Synced lyrics for the current track from LRCLIB, with a disk cache.
/// OWNER: C. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class LyricsService : IAppService
{
    private readonly SettingsStore _settings;
    private readonly MediaService _media;

    public LyricsService(SettingsStore settings, MediaService media)
    {
        _settings = settings;
        _media = media;
    }

    /// <summary>Synced lines of the current track, or null when none are available.</summary>
    public IReadOnlyList<LyricLine>? Lines { get; private set; }

    /// <summary>Index into <see cref="Lines"/> for the current playback position, or −1.</summary>
    public int CurrentIndex { get; private set; } = -1;

    public LyricLine? CurrentLine => Lines is { } lines && CurrentIndex >= 0 && CurrentIndex < lines.Count ? lines[CurrentIndex] : null;

    /// <summary>Raised on the UI thread when <see cref="Lines"/> changes.</summary>
    public event EventHandler? LyricsChanged;

    /// <summary>Raised on the UI thread when <see cref="CurrentIndex"/> changes.</summary>
    public event EventHandler? LineChanged;

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}
