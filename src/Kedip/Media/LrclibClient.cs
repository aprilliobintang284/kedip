using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kedip.Core;

namespace Kedip.Media;

/// <summary>What the media session says about the track whose lyrics we want.</summary>
internal sealed record LyricsRequest(string Artist, string Title, string Album, TimeSpan Duration, string SourceAppId, bool UseFallback = true);

/// <summary>Outcome of a lookup. <see cref="Synced"/> is raw LRC text.</summary>
internal sealed record LyricsResult(string? Synced, string? Plain, bool Instrumental, bool FromCache);

internal sealed class LyricsCacheEntry
{
    public string? Synced { get; set; }
    public string? Plain { get; set; }
    public bool NotFound { get; set; }
    public bool Instrumental { get; set; }

    /// <summary>The fallback source (NetEase) was also asked.</summary>
    public bool TriedFallback { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}

internal sealed class LrclibRecord
{
    public long Id { get; set; }
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public string? AlbumName { get; set; }
    public double? Duration { get; set; }
    public bool Instrumental { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LyricsCacheEntry))]
[JsonSerializable(typeof(LrclibRecord))]
[JsonSerializable(typeof(List<LrclibRecord>))]
internal sealed partial class LyricsJsonContext : JsonSerializerContext;

/// <summary>
/// LRCLIB (https://lrclib.net) lookups with a per-track JSON cache. Thread-agnostic; call it off the UI thread.
/// </summary>
internal static class LrclibClient
{
    private const string ApiBase = "https://lrclib.net/api/";
    private const string UserAgent = "Kedip/0.1 (personal notch app)";
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(3);
    private static readonly Lazy<HttpClient> Http = new(CreateClient);

    /// <summary>
    /// Returns cached or freshly fetched lyrics, or null when LRCLIB could not be reached
    /// (network failures are not cached, so the next play retries).
    /// </summary>
    public static async Task<LyricsResult?> GetAsync(LyricsRequest request, string cacheDir, CancellationToken ct)
    {
        string cacheFile = Path.Combine(cacheDir, CacheKey(request) + ".json");
        var cached = await ReadCacheAsync(cacheFile, ct).ConfigureAwait(false);
        if (cached is not null && !ShouldRetry(cached) && !(request.UseFallback && cached.Synced is null && !cached.Instrumental && !cached.TriedFallback))
        {
            return new LyricsResult(cached.Synced, cached.Plain, cached.Instrumental, FromCache: true);
        }

        var fetched = await FetchAsync(request, ct).ConfigureAwait(false);
        bool triedFallback = false;
        if (request.UseFallback && fetched is not { Instrumental: true } && fetched?.Synced is null)
        {
            // LRCLIB has no synced lyrics (or is down): ask the second source.
            triedFallback = true;
            string? alt = await NeteaseLyrics.FindSyncedAsync(request, ct).ConfigureAwait(false);
            if (alt is not null)
            {
                Log.Info("Lyrics found via fallback source");
                fetched = new LyricsResult(alt, fetched?.Plain, false, FromCache: false);
            }
        }

        if (fetched is null)
        {
            return cached is null ? null : new LyricsResult(cached.Synced, cached.Plain, cached.Instrumental, FromCache: true);
        }

        var entry = new LyricsCacheEntry
        {
            Synced = fetched.Synced,
            Plain = fetched.Plain,
            Instrumental = fetched.Instrumental,
            NotFound = fetched.Synced is null && fetched.Plain is null && !fetched.Instrumental,
            TriedFallback = triedFallback,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        await WriteCacheAsync(cacheFile, entry, ct).ConfigureAwait(false);
        return fetched;
    }

    /// <summary>Hex SHA-1 of lowercase "artist|title|album|durationSecondsRounded".</summary>
    internal static string CacheKey(LyricsRequest request)
    {
        int seconds = (int)Math.Round(request.Duration.TotalSeconds);
        string key = $"{request.Artist.Trim()}|{request.Title.Trim()}|{request.Album.Trim()}|{seconds}".ToLowerInvariant();
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
    }

    // Anything without synced lyrics (not found, or plain only) is retried after a week; instrumentals are final.
    private static bool ShouldRetry(LyricsCacheEntry entry) =>
        entry.Synced is null && !entry.Instrumental && DateTimeOffset.UtcNow - entry.FetchedAt > RetryMissingAfter;

    /// <summary>Runs the lookup strategy. Returns null when LRCLIB could not confirm a result (network/server failure).</summary>
    internal static async Task<LyricsResult?> FetchAsync(LyricsRequest request, CancellationToken ct)
    {
        var candidates = TrackTitleCleaner.GetCandidates(request.Title, request.Artist, request.Album, request.SourceAppId);
        if (candidates.Count == 0)
        {
            return new LyricsResult(null, null, false, FromCache: false);
        }

        int seconds = (int)Math.Round(request.Duration.TotalSeconds);
        if (seconds is < 1 or > 3600)
        {
            seconds = 0;
        }

        string album = request.Album.Trim();
        string? plain = null;
        bool firstRequest = true;
        bool failed = false;
        bool searched = false;

        async Task PaceAsync()
        {
            if (!firstRequest)
            {
                await Task.Delay(RequestGap, ct).ConfigureAwait(false);
            }

            firstRequest = false;
        }

        // 1) Exact match on the best two candidates.
        foreach (var candidate in candidates.Take(2))
        {
            if (candidate.Artist.Length == 0)
            {
                continue;
            }

            await PaceAsync().ConfigureAwait(false);
            var query = new StringBuilder("get?")
                .Append("track_name=").Append(Uri.EscapeDataString(candidate.Title))
                .Append("&artist_name=").Append(Uri.EscapeDataString(candidate.Artist));
            if (album.Length > 0)
            {
                query.Append("&album_name=").Append(Uri.EscapeDataString(album));
            }

            if (seconds > 0)
            {
                query.Append("&duration=").Append(seconds);
            }

            var (ok, record) = await GetJsonAsync(query.ToString(), LyricsJsonContext.Default.LrclibRecord, ct).ConfigureAwait(false);
            if (!ok)
            {
                // LRCLIB answers 503 for some misses too; carry on with the next strategy.
                failed = true;
                continue;
            }

            if (record is not null)
            {
                if (record.Instrumental)
                {
                    return new LyricsResult(null, null, true, FromCache: false);
                }

                if (HasText(record.SyncedLyrics))
                {
                    return new LyricsResult(record.SyncedLyrics, record.PlainLyrics, false, FromCache: false);
                }

                plain ??= NullIfEmpty(record.PlainLyrics);
            }
        }

        // 2) Keyword search by title + artist; prefer a synced entry within ±3 s, else the closest synced one.
        foreach (var candidate in candidates.Take(2))
        {
            await PaceAsync().ConfigureAwait(false);
            var query = new StringBuilder("search?track_name=").Append(Uri.EscapeDataString(candidate.Title));
            if (candidate.Artist.Length > 0)
            {
                query.Append("&artist_name=").Append(Uri.EscapeDataString(candidate.Artist));
            }

            var (ok, list) = await GetJsonAsync(query.ToString(), LyricsJsonContext.Default.ListLrclibRecord, ct).ConfigureAwait(false);
            if (!ok)
            {
                // LRCLIB answers 503 for some misses too; carry on with the next strategy.
                failed = true;
                continue;
            }

            searched = true;
            if (list is { Count: > 0 })
            {
                var pick = PickBest(list, seconds, requireClose: false);
                if (pick is not null)
                {
                    return new LyricsResult(pick.SyncedLyrics, pick.PlainLyrics, false, FromCache: false);
                }

                plain ??= list.Select(r => NullIfEmpty(r.PlainLyrics)).FirstOrDefault(p => p is not null);
            }
        }

        // 3) Free-text search (handles "Song - Artist" and other odd titles), only trusted with a close duration.
        if (seconds > 0)
        {
            var best = candidates[0];
            string q = (best.Artist + " " + best.Title).Trim();
            await PaceAsync().ConfigureAwait(false);
            var (ok, list) = await GetJsonAsync("search?q=" + Uri.EscapeDataString(q), LyricsJsonContext.Default.ListLrclibRecord, ct)
                .ConfigureAwait(false);
            if (ok && list is { Count: > 0 } && PickBest(list, seconds, requireClose: true) is { } pick)
            {
                return new LyricsResult(pick.SyncedLyrics, pick.PlainLyrics, false, FromCache: false);
            }

            searched |= ok;
            failed |= !ok;
        }

        // Not confirmed missing (every search failed): report "unreachable" so nothing is cached and the next play retries.
        if (failed && !searched && plain is null)
        {
            return null;
        }

        return new LyricsResult(null, plain, false, FromCache: false);
    }

    /// <summary>Picks a synced entry: the one closest in duration (within ±3 s when <paramref name="requireClose"/>).</summary>
    internal static LrclibRecord? PickBest(IReadOnlyList<LrclibRecord> records, int durationSeconds, bool requireClose)
    {
        LrclibRecord? best = null;
        double bestDiff = double.MaxValue;
        foreach (var record in records)
        {
            if (record.Instrumental || !HasText(record.SyncedLyrics))
            {
                continue;
            }

            if (durationSeconds <= 0)
            {
                return record;
            }

            double diff = record.Duration is { } d ? Math.Abs(d - durationSeconds) : double.MaxValue / 2;
            if (diff < bestDiff)
            {
                best = record;
                bestDiff = diff;
            }
        }

        if (best is not null && requireClose && bestDiff > CloseDuration.TotalSeconds)
        {
            return null;
        }

        return best;
    }

    /// <summary>GET + JSON parse. ok = false on network/server failure; value = null on 404.</summary>
    private static async Task<(bool Ok, T? Value)> GetJsonAsync<T>(
        string relativeUrl, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        for (int attempt = 0; ; attempt++)
        {
            var (ok, value, retryAfter) = await GetJsonOnceAsync(relativeUrl, typeInfo, ct).ConfigureAwait(false);
            if (ok || retryAfter is not { } wait || attempt >= 1)
            {
                return (ok, value);
            }

            // Busy/rate-limited: honour a short Retry-After once, then give up until the next play.
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(bool Ok, T? Value, TimeSpan? RetryAfter)> GetJsonOnceAsync<T>(
        string relativeUrl, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        try
        {
            using var response = await Http.Value.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (true, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                string retry = response.Headers.RetryAfter?.ToString() ?? "";
                Log.Warn($"LRCLIB {relativeUrl.Split('?')[0]} returned {(int)response.StatusCode} {retry}".TrimEnd());
                TimeSpan? retryAfter = null;
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var delta = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    if (delta <= MaxRetryAfter)
                    {
                        retryAfter = delta < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : delta;
                    }
                }

                return (false, null, retryAfter);
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"LRCLIB returned unexpected content type {mediaType}");
                return (false, null, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false);
            return (true, value, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("LRCLIB request timed out");
            return (false, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            Log.Warn($"LRCLIB request failed: {ex.Message}");
            return (false, null, null);
        }
    }

    private static async Task<LyricsCacheEntry?> ReadCacheAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync(stream, LyricsJsonContext.Default.LyricsCacheEntry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lyrics cache unreadable ({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    private static async Task WriteCacheAsync(string path, LyricsCacheEntry entry, CancellationToken ct)
    {
        try
        {
            string tmp = path + ".tmp";
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, entry, LyricsJsonContext.Default.LyricsCacheEntry, ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write lyrics cache: {ex.Message}");
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);

    private static string? NullIfEmpty(string? s) => HasText(s) ? s : null;
}
