using System.Text.RegularExpressions;

namespace Kedip.Media;

/// <summary>An artist/title pair to look up. <see cref="IsRaw"/> = taken verbatim from the media session.</summary>
internal readonly record struct TrackCandidate(string Artist, string Title, bool IsRaw);

/// <summary>
/// Turns noisy media-session metadata (browser/YouTube titles, VEVO/"- Topic" channels, "feat." credits)
/// into artist/title pairs suitable for a lyrics lookup. Pure logic, no I/O.
/// </summary>
internal static partial class TrackTitleCleaner
{
    private static readonly string[] BrowserHints =
        ["chrome", "msedge", "edge", "firefox", "opera", "brave", "vivaldi", "arc", "browser", "yandex", "whale", "thorium", "floorp", "librewolf", "zen"];

    // Words that mark a bracketed or dash-separated segment as video/upload noise rather than part of the song title.
    private const string NoiseWords =
        @"official|video|audio|lyrics?|lirik|visuali[sz]er|m/?v|hd|hq|4k|8k|1080p|720p|remaster(?:ed)?|explicit|clean|" +
        @"clip|klip|karaoke|teaser|trailer|color\s*coded|full\s+album|vevo|premiere|out\s+now";

    /// <summary>
    /// Ordered, de-duplicated lookup candidates (best first). Music apps get the raw metadata first;
    /// browser-like sessions (no album) get an "Artist - Song" split of the title first.
    /// </summary>
    public static IReadOnlyList<TrackCandidate> GetCandidates(string title, string artist, string album, string sourceAppId)
    {
        var result = new List<TrackCandidate>(3);
        string rawTitle = Collapse(title);
        string rawArtist = Collapse(artist);
        if (rawTitle.Length == 0)
        {
            return result;
        }

        bool videoLike = string.IsNullOrWhiteSpace(album) || IsBrowser(sourceAppId);
        if (!videoLike)
        {
            Add(result, rawArtist, rawTitle, isRaw: true);
        }

        string channel = CleanArtist(rawArtist);
        string title1 = RemoveTrailingNoise(RemovePipeTail(RemoveBracketNoise(rawTitle)));
        if (videoLike)
        {
            if (TrySplitArtistSong(title1, out string splitArtist, out string song) ||
                TrySplitQuoted(title1, out splitArtist, out song))
            {
                string left = CleanArtist(splitArtist);
                string right = CleanTitle(song);

                // "Song - Artist" uploaded by the artist's own channel.
                if (channel.Length > 0 &&
                    string.Equals(CleanArtist(song), channel, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(left, channel, StringComparison.OrdinalIgnoreCase))
                {
                    Add(result, channel, CleanTitle(splitArtist), isRaw: false);
                }
                else
                {
                    Add(result, left, right, isRaw: false);
                }
            }
        }

        Add(result, channel, CleanTitle(title1), isRaw: false);
        return result;
    }

    /// <summary>Removes "(Official Video)", "[Lyrics]", "| ..." tails, trailing " - Remastered 2011" and "feat." credits.</summary>
    public static string CleanTitle(string title)
    {
        string t = RemoveTrailingNoise(RemovePipeTail(RemoveBracketNoise(Collapse(title))));
        t = Collapse(FeatTail().Replace(t, ""));
        return TrimSeparators(t);
    }

    /// <summary>Normalises a channel/artist name: drops " - Topic", "VEVO", "Official ...", "feat." and secondary artists.</summary>
    public static string CleanArtist(string artist)
    {
        string a = Collapse(artist);
        a = TopicSuffix().Replace(a, "");
        if (a.EndsWith("VEVO", StringComparison.Ordinal) && a.Length > 4)
        {
            a = a[..^4];
            if (!a.Contains(' '))
            {
                a = CamelSplit().Replace(a, " ");
            }
        }

        string beforeOfficial = a;
        a = OfficialSuffix().Replace(a, "");
        if (a.Length == 0)
        {
            a = beforeOfficial;
        }

        a = FeatTail().Replace(a, "");
        int comma = a.IndexOf(", ", StringComparison.Ordinal);
        if (comma > 0)
        {
            a = a[..comma];
        }

        a = TrimSeparators(a);
        return a.Length > 0 ? a : TrimSeparators(Collapse(artist));
    }

    internal static bool IsBrowser(string sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
        {
            return false;
        }

        foreach (string hint in BrowserHints)
        {
            if (sourceAppId.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySplitArtistSong(string title, out string artist, out string song)
    {
        var m = DashSplit().Match(title);
        if (m.Success)
        {
            artist = m.Groups["a"].Value.Trim();
            song = m.Groups["b"].Value.Trim();
            return artist.Length > 0 && song.Length > 0;
        }

        artist = song = "";
        return false;
    }

    /// <summary>Handles 'Artist "Song"' / 'Artist “Song”' titles.</summary>
    private static bool TrySplitQuoted(string title, out string artist, out string song)
    {
        var m = QuotedSplit().Match(title);
        if (m.Success)
        {
            artist = m.Groups["a"].Value.Trim();
            song = m.Groups["b"].Value.Trim();
            return artist.Length > 0 && song.Length > 0;
        }

        artist = song = "";
        return false;
    }

    private static string RemoveBracketNoise(string title) =>
        Collapse(Bracketed().Replace(title, m =>
        {
            string inner = m.Groups["inner"].Value;
            return NoiseWord().IsMatch(inner) || FeatStart().IsMatch(inner) ? " " : m.Value;
        }));

    private static string RemovePipeTail(string title)
    {
        int cut = title.IndexOfAny(['|', '｜']);
        if (cut < 0)
        {
            int slashes = title.IndexOf(" // ", StringComparison.Ordinal);
            cut = slashes;
        }

        return cut > 0 ? title[..cut].TrimEnd() : title;
    }

    /// <summary>Drops trailing " - Official Video" / " - Remastered 2011" style segments.</summary>
    private static string RemoveTrailingNoise(string title)
    {
        while (true)
        {
            var m = DashTail().Match(title);
            if (!m.Success || !NoiseSegment().IsMatch(m.Groups["seg"].Value))
            {
                return title;
            }

            title = title[..m.Index].TrimEnd();
        }
    }

    private static void Add(List<TrackCandidate> list, string artist, string title, bool isRaw)
    {
        if (title.Length == 0)
        {
            return;
        }

        foreach (var existing in list)
        {
            if (string.Equals(existing.Artist, artist, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add(new TrackCandidate(artist, title, isRaw));
    }

    private static string Collapse(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string TrimSeparators(string s) => s.Trim().Trim('-', '–', '—', ':', '|', '~', ',').Trim();

    [GeneratedRegex(@"\s*[\(\[【](?<inner>[^\)\]】]*)[\)\]】]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\b(?:" + NoiseWords + @")\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseWord();

    [GeneratedRegex(@"^\s*(?:feat\.?|ft\.?|featuring)\s", RegexOptions.IgnoreCase)]
    private static partial Regex FeatStart();

    // Stops at a spaced dash or a bracket so "A ft. B - Song" keeps " - Song".
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+(?:(?!\s[-–—]\s)[^\(\)\[\]])*", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTail();

    // A dash-separated tail made only of noise/filler words, e.g. "Official Music Video", "Remastered 2011".
    [GeneratedRegex(@"^(?:(?:" + NoiseWords + @"|music|version|lagu|the|new|full|song|with|and|in|by|&|\d{1,4})\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseSegment();

    [GeneratedRegex(@"\s+[-–—]\s+(?<seg>[^-–—]+)$")]
    private static partial Regex DashTail();

    [GeneratedRegex(@"^(?<a>.+?)\s+[-–—]\s+(?<b>.+)$")]
    private static partial Regex DashSplit();

    [GeneratedRegex(@"^(?<a>[^""“”]+?)\s*[""“](?<b>[^""“”]+)[""”]")]
    private static partial Regex QuotedSplit();

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicSuffix();

    [GeneratedRegex(@"\s*\b(?:Official(?:\s+(?:Artist|YouTube))?(?:\s+Channel)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex OfficialSuffix();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelSplit();
}
