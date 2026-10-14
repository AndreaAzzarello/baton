using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DownloadManager.Core.Media;

/// <summary>One representation (quality) of a DASH adaptation set, with its segments already listed.</summary>
public sealed record DashRepresentation(
    string Id,
    string ContentType, // "video", "audio" or "text"
    long Bandwidth,
    int? Width,
    int? Height,
    string? Codecs,
    string? Language,
    MediaSegment? Init,
    List<MediaSegment> Segments);

/// <summary>
/// A DASH manifest (MPD, ISO/IEC 23009-1), static (on demand) presentations with SegmentTemplate (with $Number$ or
/// $Time$ and SegmentTimeline), SegmentList or a single file per representation (SegmentBase / BaseURL).
/// </summary>
public sealed class DashManifest
{
    public List<DashRepresentation> Representations { get; } = [];

    /// <summary>type="dynamic": a live presentation whose segments keep changing.</summary>
    public bool IsLive { get; private set; }

    /// <summary>A ContentProtection element: the media is encrypted for a DRM system (Widevine, PlayReady...).</summary>
    public bool IsProtected { get; private set; }

    public List<string> ProtectionSchemes { get; } = [];

    public double? Duration { get; private set; }

    /// <summary>More than one period: only the first is downloaded (often the others are adverts).</summary>
    public int PeriodCount { get; private set; }

    public static bool LooksLikeDash(string text) => text.Contains("<MPD", StringComparison.Ordinal);

    public static DashManifest Parse(string xml, Uri manifestUrl)
    {
        // DTDs are refused: a manifest from the web must not make the parser fetch or expand anything.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 };
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(xml), settings))
            doc = XDocument.Load(reader);
        var mpd = doc.Root ?? throw new FormatException("Empty DASH manifest.");
        if (mpd.Name.LocalName != "MPD")
            throw new FormatException("Not a DASH manifest (MPD missing).");

        var manifest = new DashManifest
        {
            IsLive = string.Equals((string?)mpd.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase),
            Duration = ParseDuration((string?)mpd.Attribute("mediaPresentationDuration")),
        };
        foreach (var protection in mpd.Descendants().Where(e => e.Name.LocalName == "ContentProtection"))
        {
            manifest.IsProtected = true;
            var scheme = (string?)protection.Attribute("schemeIdUri") ?? "";
            if (!manifest.ProtectionSchemes.Contains(scheme))
                manifest.ProtectionSchemes.Add(scheme);
        }

        var periods = Children(mpd, "Period").ToList();
        manifest.PeriodCount = periods.Count;
        // A live presentation has no fixed list of segments: it is reported, not listed.
        if (periods.Count == 0 || manifest.IsLive)
            return manifest;
        var period = periods[0];
        var periodDuration = ParseDuration((string?)period.Attribute("duration")) ?? manifest.Duration;
        var periodBase = BaseOf(BaseOf(manifestUrl, mpd), period);

        foreach (var set in Children(period, "AdaptationSet"))
        {
            var setBase = BaseOf(periodBase, set);
            foreach (var rep in Children(set, "Representation"))
            {
                var type = ContentTypeOf(set, rep);
                if (type is null)
                    continue;
                var repBase = BaseOf(setBase, rep);
                var id = (string?)rep.Attribute("id") ?? manifest.Representations.Count.ToString(CultureInfo.InvariantCulture);
                var bandwidth = (long?)ParseLong((string?)rep.Attribute("bandwidth")) ?? 0;
                var (init, segments) = SegmentsOf(period, set, rep, repBase, id, bandwidth, periodDuration);
                manifest.Representations.Add(new DashRepresentation(
                    id, type, bandwidth,
                    ParseInt((string?)rep.Attribute("width") ?? (string?)set.Attribute("width")),
                    ParseInt((string?)rep.Attribute("height") ?? (string?)set.Attribute("height")),
                    (string?)rep.Attribute("codecs") ?? (string?)set.Attribute("codecs"),
                    (string?)set.Attribute("lang"),
                    init, segments));
            }
        }
        return manifest;
    }

    private static string? ContentTypeOf(XElement set, XElement rep)
    {
        var value = (string?)set.Attribute("contentType")
                    ?? (string?)rep.Attribute("mimeType")
                    ?? (string?)set.Attribute("mimeType")
                    ?? "";
        if (value.StartsWith("video", StringComparison.OrdinalIgnoreCase))
            return "video";
        if (value.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
            return "audio";
        return null; // subtitles and images are not downloaded
    }

    private static (MediaSegment? Init, List<MediaSegment> Segments) SegmentsOf(XElement period, XElement set, XElement rep, Uri baseUrl,
        string id, long bandwidth, double? duration)
    {
        // SegmentTemplate and SegmentList may be given at any level; the innermost wins, attributes are inherited.
        var templates = new[] { rep, set, period }.Select(e => Child(e, "SegmentTemplate")).Where(e => e is not null).Cast<XElement>().ToList();
        if (templates.Count > 0)
            return FromTemplate(templates, baseUrl, id, bandwidth, duration);

        var list = new[] { rep, set, period }.Select(e => Child(e, "SegmentList")).FirstOrDefault(e => e is not null);
        if (list is not null)
        {
            MediaSegment? init = null;
            if (Child(list, "Initialization") is { } initialization)
                init = Ranged(baseUrl, (string?)initialization.Attribute("sourceURL"), (string?)initialization.Attribute("range"));
            var timescale = ParseLong((string?)list.Attribute("timescale")) ?? 1;
            var segmentDuration = (ParseLong((string?)list.Attribute("duration")) ?? 0) / (double)timescale;
            var segments = Children(list, "SegmentURL")
                .Select(s => Ranged(baseUrl, (string?)s.Attribute("media"), (string?)s.Attribute("mediaRange")) with { Duration = segmentDuration })
                .ToList();
            return (init, segments);
        }

        // SegmentBase or nothing: the representation is one file at its BaseURL.
        return (null, [new MediaSegment(baseUrl, null, null, duration ?? 0)]);
    }

    private static (MediaSegment? Init, List<MediaSegment> Segments) FromTemplate(List<XElement> levels, Uri baseUrl, string id, long bandwidth,
        double? duration)
    {
        string? Attr(string name) => levels.Select(l => (string?)l.Attribute(name)).FirstOrDefault(v => v is not null);
        var media = Attr("media") ?? throw new FormatException("SegmentTemplate without media.");
        var timescale = ParseLong(Attr("timescale")) ?? 1;
        var startNumber = ParseLong(Attr("startNumber")) ?? 1;
        MediaSegment? init = Attr("initialization") is { } initialization
            ? new MediaSegment(new Uri(baseUrl, Fill(initialization, id, bandwidth, null, null)))
            : null;

        var segments = new List<MediaSegment>();
        var timeline = levels.Select(l => Child(l, "SegmentTimeline")).FirstOrDefault(t => t is not null);
        if (timeline is not null)
        {
            long time = 0;
            var number = startNumber;
            foreach (var s in Children(timeline, "S"))
            {
                if (ParseLong((string?)s.Attribute("t")) is { } t)
                    time = t;
                var d = ParseLong((string?)s.Attribute("d")) ?? throw new FormatException("SegmentTimeline entry without duration.");
                var repeat = ParseLong((string?)s.Attribute("r")) ?? 0;
                if (repeat < 0)
                {
                    // r = -1: repeat until the end of the period.
                    var end = (duration ?? 0) * timescale;
                    repeat = d > 0 ? Math.Max(0, (long)Math.Ceiling((end - time) / d) - 1) : 0;
                }
                for (var i = 0; i <= repeat; i++)
                {
                    segments.Add(new MediaSegment(new Uri(baseUrl, Fill(media, id, bandwidth, number, time)), null, null, d / (double)timescale));
                    time += d;
                    number++;
                    if (segments.Count > MaxSegments)
                        throw new FormatException("Too many segments.");
                }
            }
            return (init, segments);
        }

        var segmentDuration = ParseLong(Attr("duration")) ?? throw new FormatException("SegmentTemplate without duration or timeline.");
        if (duration is not { } total || segmentDuration <= 0)
            throw new FormatException("The presentation duration is unknown.");
        var seconds = segmentDuration / (double)timescale;
        var count = (long)Math.Ceiling(total / seconds - 1e-6);
        if (count > MaxSegments)
            throw new FormatException("Too many segments.");
        for (long i = 0; i < count; i++)
        {
            var number = startNumber + i;
            segments.Add(new MediaSegment(new Uri(baseUrl, Fill(media, id, bandwidth, number, i * segmentDuration)), null, null,
                Math.Min(seconds, total - i * seconds)));
        }
        return (init, segments);
    }

    private const int MaxSegments = 200_000;

    private static readonly Regex Identifier = new(@"\$(RepresentationID|Number|Time|Bandwidth|)(%0(\d+)d)?\$", RegexOptions.CultureInvariant);

    /// <summary>Replaces $RepresentationID$, $Number$, $Time$, $Bandwidth$ (with an optional %0Nd width) and $$.</summary>
    public static string Fill(string template, string id, long bandwidth, long? number, long? time) =>
        Identifier.Replace(template, m =>
        {
            var width = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            string Pad(long value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            return m.Groups[1].Value switch
            {
                "RepresentationID" => id,
                "Number" => number is { } n ? Pad(n) : m.Value,
                "Time" => time is { } t ? Pad(t) : m.Value,
                "Bandwidth" => Pad(bandwidth),
                _ => "$",
            };
        });

    private static MediaSegment Ranged(Uri baseUrl, string? source, string? range)
    {
        var url = string.IsNullOrEmpty(source) ? baseUrl : new Uri(baseUrl, source);
        if (string.IsNullOrEmpty(range))
            return new MediaSegment(url);
        var dash = range.IndexOf('-');
        var from = long.Parse(range[..dash], CultureInfo.InvariantCulture);
        var to = long.Parse(range[(dash + 1)..], CultureInfo.InvariantCulture);
        return new MediaSegment(url, from, to - from + 1);
    }

    private static Uri BaseOf(Uri parent, XElement element) =>
        Child(element, "BaseURL") is { } baseUrl && !string.IsNullOrWhiteSpace(baseUrl.Value) ? new Uri(parent, baseUrl.Value.Trim()) : parent;

    private static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement element, string name) => element.Elements().Where(e => e.Name.LocalName == name);

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>xs:duration, e.g. "PT1H2M3.5S".</summary>
    public static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return XmlConvert.ToTimeSpan(value.Trim()).TotalSeconds;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
