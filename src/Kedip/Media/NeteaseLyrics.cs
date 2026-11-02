using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kedip.Core;

namespace Kedip.Media;

/// <summary>
/// Fallback source for synced lyrics: NetEase Cloud Music's public web endpoints (no key needed).
/// Only used when LRCLIB has no synced lyrics or cannot be reached. Matches on cleaned title, artist and duration.
/// </summary>
internal static partial class NeteaseLyrics
{
    private const string Base = "https://music.163.com/api/";
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(4);
    private static readonly Lazy<HttpClient> Http = new(CreateClient);

    // Credit lines NetEase prepends to many LRCs ("作词 : …", "Composer : …").
    private static readonly string[] CreditWords =
        ["作词", "作曲", "编曲", "制作", "混音", "母带", "和声", "吉他", "贝斯", "鼓", "监制", "出品", "词", "曲",
         "lyricist", "composer", "arranger", "producer", "mixing", "mastering", "written by"];

    /// <summary>Synced LRC text for the track, or null when nothing trustworthy was found.</summary>
    public static async Task<string?> FindSyncedAsync(LyricsRequest request, CancellationToken ct)
    {
        int seconds = (int)Math.Round(request.Duration.TotalSeconds);
        var tried = new HashSet<long>();
        foreach (var candidate in TrackTitleCleaner.GetCandidates(request.Title, request.Artist, request.Album, request.SourceAppId).Take(3))
        {
            string query = (candidate.Title + " " + candidate.Artist).Trim();
            if (query.Length == 0)
            {
                continue;
            }

            foreach (long id in await SearchAsync(query, candidate, seconds, ct).ConfigureAwait(false))
            {
                if (!tried.Add(id))
                {
                    continue;
                }

                string? lrc = await GetLyricAsync(id, ct).ConfigureAwait(false);
                if (lrc is not null)
                {
                    return lrc;
                }
            }
        }

        return null;
    }

    /// <summary>Song ids that match the candidate, best first (at most two).</summary>
    private static async Task<IReadOnlyList<long>> SearchAsync(string query, TrackCandidate candidate, int seconds, CancellationToken ct)
    {
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = query,
                ["type"] = "1",
                ["limit"] = "10",
                ["offset"] = "0",
            });
            using var response = await Http.Value.PostAsync(Base + "search/get", form, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"NetEase search returned {(int)response.StatusCode}");
                return [];
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            string wantTitle = Normalize(candidate.Title);
            string wantArtist = Normalize(candidate.Artist);
            var scored = new List<(long Id, int Score)>();
            foreach (var song in songs.EnumerateArray())
            {
                long id = song.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long v) ? v : 0;
                string name = Normalize(song.TryGetProperty("name", out var n) ? n.GetString() : null);
                if (id == 0 || name.Length == 0)
                {
                    continue;
                }

                int score;
                if (name == wantTitle)
                {
                    score = 3;
                }
                else if (wantTitle.Length >= 4 && (name.StartsWith(wantTitle, StringComparison.Ordinal) || wantTitle.StartsWith(name, StringComparison.Ordinal)))
                {
                    score = 1;
                }
                else
                {
                    continue;
                }

                bool artistMatch = wantArtist.Length == 0;
                if (song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in artists.EnumerateArray())
                    {
                        string artist = Normalize(a.TryGetProperty("name", out var an) ? an.GetString() : null);
                        if (artist.Length > 0 && (wantArtist.Contains(artist, StringComparison.Ordinal) || artist.Contains(wantArtist, StringComparison.Ordinal)))
                        {
                            artistMatch = true;
                        }
                    }
                }

                long ms = song.TryGetProperty("duration", out var d) && d.TryGetInt64(out long dv) ? dv : 0;
                bool durationKnown = seconds > 0 && ms > 0;
                bool durationClose = durationKnown && Math.Abs(ms / 1000.0 - seconds) <= DurationTolerance.TotalSeconds;

                // A different length (live, remix, sped up) would put every line at the wrong time; without a known
                // length the artist has to match instead.
                if (durationKnown ? !durationClose : !artistMatch)
                {
                    continue;
                }

                score += (artistMatch ? 2 : 0) + (durationClose ? 2 : 0);
                scored.Add((id, score));
            }

            return scored.OrderByDescending(x => x.Score).Take(2).Select(x => x.Id).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"NetEase search failed: {ex.Message}");
            return [];
        }
    }

    private static async Task<string?> GetLyricAsync(long id, CancellationToken ct)
    {
        try
        {
            using var response = await Http.Value.GetAsync($"{Base}song/lyric?id={id}&lv=1&kv=1&tv=-1", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("lrc", out var lrc) || !lrc.TryGetProperty("lyric", out var text))
            {
                return null;
            }

            return CleanLrc(text.GetString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"NetEase lyric failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops credit lines; returns null when fewer than four timed lyric lines remain (instrumental notes etc.).</summary>
    internal static string? CleanLrc(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return null;
        }

        var kept = new StringBuilder();
        int lines = 0;
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var m = TimedLine().Match(line);
            if (!m.Success)
            {
                continue;
            }

            string body = m.Groups["text"].Value.Trim();
            if (IsCredit(body) || body.Contains("纯音乐", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Append(line).Append('\n');
            if (body.Length > 0)
            {
                lines++;
            }
        }

        return lines >= 4 ? kept.ToString() : null;
    }

    private static bool IsCredit(string body)
    {
        int colon = body.IndexOfAny([':', '：']);
        if (colon <= 0 || colon > 24)
        {
            return false;
        }

        string head = body[..colon].Trim().ToLowerInvariant();
        return CreditWords.Any(w => head.Contains(w, StringComparison.Ordinal));
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return "";
        }

        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\](?<text>.*)$")]
    private static partial Regex TimedLine();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Kedip/0.1");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }
}
