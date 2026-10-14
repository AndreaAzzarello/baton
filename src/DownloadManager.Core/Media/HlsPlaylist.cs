using System.Globalization;

namespace DownloadManager.Core.Media;

/// <summary>A piece of a stream: a whole resource, or a byte range of one (HLS EXT-X-BYTERANGE, DASH mediaRange).</summary>
public sealed record MediaSegment(Uri Url, long? Offset = null, long? Length = null, double Duration = 0);

/// <summary>One quality of an HLS master playlist (EXT-X-STREAM-INF).</summary>
public sealed record HlsVariant(Uri Url, long Bandwidth, long? AverageBandwidth, int? Width, int? Height, string? Codecs, string? AudioGroup);

/// <summary>An alternative rendition (EXT-X-MEDIA), here only audio is used. Without a URI it is inside the variant.</summary>
public sealed record HlsRendition(string Type, string GroupId, string? Name, string? Language, bool IsDefault, Uri? Url);

/// <summary>
/// An HLS playlist (RFC 8216): either a master playlist that lists qualities, or a media playlist that lists
/// segments. Only what downloading needs is read; unknown tags are ignored, as the format requires.
/// </summary>
public sealed class HlsPlaylist
{
    public bool IsMaster => Variants.Count > 0;
    public List<HlsVariant> Variants { get; } = [];
    public List<HlsRendition> Renditions { get; } = [];
    public List<MediaSegment> Segments { get; } = [];

    /// <summary>EXT-X-MAP: the initialization section of fragmented-MP4 segments.</summary>
    public MediaSegment? Init { get; private set; }

    /// <summary>EXT-X-ENDLIST: every segment is listed (video on demand). Without it the stream is live.</summary>
    public bool HasEnd { get; private set; }

    /// <summary>
    /// The segments or the session are encrypted (EXT-X-KEY / EXT-X-SESSION-KEY with a method other than NONE):
    /// AES-128, SAMPLE-AES or a DRM key system. Such streams are never downloaded.
    /// </summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>The key systems named (KEYFORMAT), e.g. "com.apple.streamingkeydelivery" for FairPlay.</summary>
    public List<string> KeyFormats { get; } = [];

    public double Duration => Segments.Sum(s => s.Duration);

    /// <summary>Exact size when every segment is a byte range with a length.</summary>
    public long? KnownSize => Segments.Count > 0 && Segments.All(s => s.Length is not null) ? Segments.Sum(s => s.Length!.Value) : null;

    public static bool LooksLikeHls(string text) => text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);

    public static HlsPlaylist Parse(string text, Uri baseUrl)
    {
        if (!LooksLikeHls(text))
            throw new FormatException("Not an HLS playlist (#EXTM3U missing).");
        var playlist = new HlsPlaylist();
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        Dictionary<string, string>? pendingVariant = null;
        double pendingDuration = 0;
        long? pendingLength = null, pendingOffset = null;
        long nextOffset = 0; // a byte range without "@offset" continues where the previous one ended
        Uri? lastRangeUrl = null;

        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            if (line[0] == '#')
            {
                var (tag, value) = SplitTag(line);
                switch (tag)
                {
                    case "#EXT-X-STREAM-INF":
                        pendingVariant = Attributes(value);
                        break;
                    case "#EXT-X-MEDIA":
                        var media = Attributes(value);
                        playlist.Renditions.Add(new HlsRendition(
                            Get(media, "TYPE") ?? "",
                            Get(media, "GROUP-ID") ?? "",
                            Get(media, "NAME"),
                            Get(media, "LANGUAGE"),
                            Get(media, "DEFAULT") == "YES",
                            Get(media, "URI") is { } renditionUri ? Resolve(baseUrl, renditionUri) : null));
                        break;
                    case "#EXT-X-KEY":
                    case "#EXT-X-SESSION-KEY":
                        var key = Attributes(value);
                        if (!string.Equals(Get(key, "METHOD"), "NONE", StringComparison.OrdinalIgnoreCase))
                        {
                            playlist.IsEncrypted = true;
                            if (Get(key, "KEYFORMAT") is { } format && !playlist.KeyFormats.Contains(format))
                                playlist.KeyFormats.Add(format);
                        }
                        break;
                    case "#EXT-X-MAP":
                        var map = Attributes(value);
                        if (Get(map, "URI") is { } initUri)
                        {
                            var (length, offset) = ParseRange(Get(map, "BYTERANGE"));
                            playlist.Init = new MediaSegment(Resolve(baseUrl, initUri), length is null ? null : offset ?? 0, length);
                        }
                        break;
                    case "#EXTINF":
                        var comma = value.IndexOf(',');
                        double.TryParse(comma >= 0 ? value[..comma] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
                        break;
                    case "#EXT-X-BYTERANGE":
                        (pendingLength, pendingOffset) = ParseRange(value);
                        break;
                    case "#EXT-X-ENDLIST":
                        playlist.HasEnd = true;
                        break;
                    case "#EXT-X-PLAYLIST-TYPE" when value == "VOD":
                        playlist.HasEnd = true;
                        break;
                }
                continue;
            }

            // A URI line: belongs to the preceding EXT-X-STREAM-INF or EXTINF.
            var url = Resolve(baseUrl, line);
            if (pendingVariant is not null)
            {
                var (width, height) = ParseResolution(Get(pendingVariant, "RESOLUTION"));
                playlist.Variants.Add(new HlsVariant(url,
                    ParseLong(Get(pendingVariant, "BANDWIDTH")) ?? 0,
                    ParseLong(Get(pendingVariant, "AVERAGE-BANDWIDTH")),
                    width, height,
                    Get(pendingVariant, "CODECS"),
                    Get(pendingVariant, "AUDIO")));
                pendingVariant = null;
                continue;
            }

            if (pendingLength is { } rangeLength)
            {
                var start = pendingOffset ?? (lastRangeUrl == url ? nextOffset : 0);
                playlist.Segments.Add(new MediaSegment(url, start, rangeLength, pendingDuration));
                nextOffset = start + rangeLength;
                lastRangeUrl = url;
            }
            else
            {
                playlist.Segments.Add(new MediaSegment(url, null, null, pendingDuration));
            }
            pendingDuration = 0;
            pendingLength = pendingOffset = null;
        }
        return playlist;
    }

    private static (string Tag, string Value) SplitTag(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? (line, "") : (line[..colon], line[(colon + 1)..]);
    }

    /// <summary>An attribute list: NAME=value pairs separated by commas; quoted values may contain commas.</summary>
    public static Dictionary<string, string> Attributes(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < list.Length)
        {
            var eq = list.IndexOf('=', i);
            if (eq < 0)
                break;
            var name = list[i..eq].Trim().TrimStart(',').Trim();
            i = eq + 1;
            string value;
            if (i < list.Length && list[i] == '"')
            {
                var close = list.IndexOf('"', i + 1);
                if (close < 0)
                    close = list.Length;
                value = list[(i + 1)..close];
                i = Math.Min(list.Length, close + 1);
                var next = list.IndexOf(',', i);
                i = next < 0 ? list.Length : next + 1;
            }
            else
            {
                var next = list.IndexOf(',', i);
                value = (next < 0 ? list[i..] : list[i..next]).Trim();
                i = next < 0 ? list.Length : next + 1;
            }
            if (name.Length > 0)
                result[name] = value;
        }
        return result;
    }

    private static string? Get(Dictionary<string, string> attributes, string name) => attributes.TryGetValue(name, out var v) ? v : null;

    private static Uri Resolve(Uri baseUrl, string reference) => new(baseUrl, reference.Trim());

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"length[@offset]".</summary>
    private static (long? Length, long? Offset) ParseRange(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return (null, null);
        var at = value.IndexOf('@');
        return at < 0 ? (ParseLong(value), null) : (ParseLong(value[..at]), ParseLong(value[(at + 1)..]));
    }

    private static (int? Width, int? Height) ParseResolution(string? value)
    {
        var x = value?.IndexOf('x') ?? -1;
        if (x < 0)
            return (null, null);
        return (int.TryParse(value![..x], out var w) ? w : null, int.TryParse(value[(x + 1)..], out var h) ? h : null);
    }
}
