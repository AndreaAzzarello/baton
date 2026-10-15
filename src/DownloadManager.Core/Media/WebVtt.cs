using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManager.Core.Media;

/// <summary>One subtitle cue: its times and text (without cue settings).</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// WebVTT subtitles: an HLS or DASH subtitle track arrives in segments, each a small WebVTT file with its own header;
/// they are joined into one file (a cue that spans two segments, repeated in both, is kept once), and converted to
/// SubRip (.srt) for players that do not read WebVTT.
/// </summary>
public static partial class WebVtt
{
    /// <summary>The cues of every segment, in time order, without duplicates.</summary>
    public static List<SubtitleCue> Cues(IEnumerable<string> segments) => Cues(segments.Select(s => (s, TimeSpan.Zero)));

    /// <summary>The cues of every segment, each moved by its segment's shift (the start of its DASH period).</summary>
    public static List<SubtitleCue> Cues(IEnumerable<(string Text, TimeSpan Shift)> segments)
    {
        var cues = new List<SubtitleCue>();
        var seen = new HashSet<(TimeSpan, TimeSpan, string)>();
        foreach (var (text, shift) in segments)
        {
            foreach (var parsed in Parse(text))
            {
                var cue = parsed with { Start = parsed.Start + shift, End = parsed.End + shift };
                if (seen.Add((cue.Start, cue.End, cue.Text)))
                    cues.Add(cue);
            }
        }
        return cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
    }

    public static IEnumerable<SubtitleCue> Parse(string text)
    {
        var blocks = text.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Where(l => l.Length > 0).ToList();
            var timing = lines.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0)
                continue; // the header, NOTE, STYLE or REGION blocks
            var match = Timing().Match(lines[timing]);
            if (!match.Success || ParseTime(match.Groups["start"].Value) is not { } start || ParseTime(match.Groups["end"].Value) is not { } end)
                continue;
            yield return new SubtitleCue(start, end, string.Join('\n', lines.Skip(timing + 1)));
        }
    }

    public static string ToVtt(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
            builder.Append(Format(cue.Start, '.')).Append(" --> ").Append(Format(cue.End, '.')).Append('\n').Append(cue.Text).Append("\n\n");
        return builder.ToString();
    }

    /// <summary>SubRip: numbered cues, times with a comma, and only the tags SubRip knows (b, i, u).</summary>
    public static string ToSrt(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var text = Tags().Replace(cues[i].Text, m => m.Groups["tag"].Value.ToLowerInvariant() is "b" or "i" or "u" ? $"<{m.Groups["close"].Value}{m.Groups["tag"].Value.ToLowerInvariant()}>" : "");
            text = System.Net.WebUtility.HtmlDecode(text);
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(Format(cues[i].Start, ',')).Append(" --> ").Append(Format(cues[i].End, ',')).Append("\r\n")
                .Append(text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return builder.ToString();
    }

    private static TimeSpan? ParseTime(string value)
    {
        // hh:mm:ss.ttt or mm:ss.ttt
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3)
            return null;
        var hours = 0;
        if ((parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) || hours > 99_999
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return null;
        return new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
    }

    private static string Format(TimeSpan time, char separator) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}");

    [GeneratedRegex(@"(?<start>(?:\d+:)?\d{1,2}:\d{2}\.\d{1,3})\s+-->\s+(?<end>(?:\d+:)?\d{1,2}:\d{2}\.\d{1,3})")]
    private static partial Regex Timing();

    [GeneratedRegex(@"<(?<close>/?)(?<tag>[a-zA-Z]+)[^>]*>|<\d{2}:[^>]*>")]
    private static partial Regex Tags();
}
