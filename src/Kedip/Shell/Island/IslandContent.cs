using System.Windows.Media;
using Kedip.Core;

namespace Kedip.Shell.Island;

internal enum IslandMode { Collapsed, Peek, Expanded }

internal enum IslandTab { Music, Prayer, Timer, Shelf }

internal enum CentreContent { Clock, Eyes, Lyric }

/// <summary>
/// The collapsed island over a window whose top sits under the notch: regular (None), a thin Line, the beat Wave pill,
/// the regular Mini notch, or the mini notch with the wave in its centre (MiniWave).
/// </summary>
internal enum TuckLook { None, Line, Wave, Mini, MiniWave }

internal enum LeftWing { None, Art, Bolt }

internal enum RightWing { None, Equalizer, Battery }

/// <summary>What the collapsed island shows, derived from the services (pure snapshot, compared to skip no-op updates).</summary>
internal readonly record struct CollapsedContent(
    CentreContent Centre,
    LeftWing Left,
    RightWing Right,
    string? Lyric,
    ImageSource? Art,
    int BatteryPercent)
{
    public bool HasWings => Left != LeftWing.None || Right != RightWing.None;

    public static CollapsedContent From(AppServices services)
    {
        var settings = services.Settings.Current;
        var media = settings.MusicEnabled ? services.Media.Current : null;
        bool playing = media?.IsPlaying == true;
        var power = services.Power;
        bool charging = power.HasBattery && power.IsCharging;

        var centre = CentreContent.Clock;
        string? lyric = null;
        if (playing && settings.LyricsEnabled && settings.LyricsInNotch && services.Lyrics.CurrentLine is { } line)
        {
            centre = CentreContent.Lyric;
            lyric = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text.Trim();
        }
        else if (services.Mascot.IsVisible)
        {
            centre = CentreContent.Eyes;
        }

        var left = playing ? LeftWing.Art : charging ? LeftWing.Bolt : LeftWing.None;
        var right = playing ? RightWing.Equalizer : charging ? RightWing.Battery : RightWing.None;
        return new CollapsedContent(centre, left, right, lyric, playing ? media!.Thumbnail : null, power.Percent);
    }
}
