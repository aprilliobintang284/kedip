namespace Kedip.Media;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Parses LRC text ("[mm:ss.xx] line") into time-ordered lines.
/// Supports "[mm:ss]", "[mm:ss.x..xxx]", "[mm:ss:xx]", several timestamps per line, "[offset:±ms]",
/// skips metadata tags ([ar:], [ti:], ...) and keeps empty lines as instrumental gaps (Text "").
/// </summary>
public static class LrcParser
{
    public static IReadOnlyList<LyricLine> Parse(string lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return [];
        }

        var parsed = new List<LyricLine>();
        var times = new List<TimeSpan>(4);
        long offsetMs = 0;

        foreach (var raw in lrc.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '[')
            {
                continue;
            }

            times.Clear();
            int textStart = ReadTimeTags(line, times, ref offsetMs);
            if (times.Count == 0)
            {
                continue;
            }

            string text = CleanText(line[textStart..]);
            foreach (var time in times)
            {
                parsed.Add(new LyricLine(time, text));
            }
        }

        if (parsed.Count == 0)
        {
            return [];
        }

        var offset = TimeSpan.FromMilliseconds(offsetMs);
        var result = new List<LyricLine>(parsed.Count);
        foreach (var line in parsed.OrderBy(l => l.Time))
        {
            // A positive offset shows lyrics earlier.
            var time = line.Time - offset;
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            // Collapse runs of empty lines into a single instrumental gap.
            if (line.Text.Length == 0 && result.Count > 0 && result[^1].Text.Length == 0)
            {
                continue;
            }

            result.Add(line with { Time = time });
        }

        return result;
    }

    /// <summary>
    /// Reads the leading "[...]" tags of a line. Time tags go to <paramref name="times"/>; an offset tag updates
    /// <paramref name="offsetMs"/>. Returns the index where the lyric text starts.
    /// </summary>
    private static int ReadTimeTags(ReadOnlySpan<char> line, List<TimeSpan> times, ref long offsetMs)
    {
        int pos = 0;
        int textStart = 0;
        while (pos < line.Length && line[pos] == '[')
        {
            int close = line[pos..].IndexOf(']');
            if (close < 0)
            {
                break;
            }

            var tag = line.Slice(pos + 1, close - 1);
            if (TryParseTime(tag, out var time))
            {
                times.Add(time);
            }
            else if (times.Count == 0)
            {
                // Metadata line ([ar:], [ti:], [offset:], ...): nothing else on it is lyric text.
                if (TryParseOffset(tag, out long offset))
                {
                    offsetMs = offset;
                }

                return line.Length;
            }
            else
            {
                // A non-time bracket after timestamps belongs to the text (e.g. "[Chorus]").
                break;
            }

            pos += close + 1;
            textStart = pos;

            // Allow whitespace between consecutive time tags.
            int next = pos;
            while (next < line.Length && char.IsWhiteSpace(line[next]))
            {
                next++;
            }

            if (next < line.Length && line[next] == '[')
            {
                pos = next;
            }
        }

        return textStart;
    }

    /// <summary>Parses "mm:ss", "mm:ss.f", "mm:ss.ff", "mm:ss.fff" and "mm:ss:ff".</summary>
    internal static bool TryParseTime(ReadOnlySpan<char> tag, out TimeSpan time)
    {
        time = default;
        tag = tag.Trim();
        int colon = tag.IndexOf(':');
        if (colon <= 0 || !TryParseDigits(tag[..colon], out long minutes))
        {
            return false;
        }

        var rest = tag[(colon + 1)..];
        int sep = rest.IndexOfAny('.', ':');
        var secondsPart = sep < 0 ? rest : rest[..sep];
        if (secondsPart.Length is 0 or > 2 || !TryParseDigits(secondsPart, out long seconds))
        {
            return false;
        }

        long ms = 0;
        if (sep >= 0)
        {
            var fraction = rest[(sep + 1)..];
            if (fraction.Length == 0 || !TryParseDigits(fraction, out _))
            {
                return false;
            }

            // Keep at most millisecond precision: ".5" = 500 ms, ".05" = 50 ms, ".005" = 5 ms.
            if (fraction.Length > 3)
            {
                fraction = fraction[..3];
            }

            TryParseDigits(fraction, out long value);
            ms = fraction.Length switch
            {
                1 => value * 100,
                2 => value * 10,
                _ => value,
            };
        }

        time = TimeSpan.FromMilliseconds((minutes * 60 + seconds) * 1000 + ms);
        return true;
    }

    internal static bool TryParseOffset(ReadOnlySpan<char> tag, out long offsetMs)
    {
        offsetMs = 0;
        const string Prefix = "offset:";
        tag = tag.Trim();
        if (!tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(tag[Prefix.Length..].Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out offsetMs);
    }

    private static bool TryParseDigits(ReadOnlySpan<char> span, out long value)
    {
        value = 0;
        if (span.Length == 0 || span.Length > 9)
        {
            return false;
        }

        foreach (char c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = value * 10 + (c - '0');
        }

        return true;
    }

    /// <summary>Trims the text and drops enhanced-LRC word timestamps ("&lt;mm:ss.xx&gt;").</summary>
    private static string CleanText(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.IndexOf('<') < 0)
        {
            return text.ToString();
        }

        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                int close = text[i..].IndexOf('>');
                if (close > 0 && TryParseTime(text.Slice(i + 1, close - 1), out _))
                {
                    i += close + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        // Word tags usually sit between words, so normalise the spacing they leave behind.
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
