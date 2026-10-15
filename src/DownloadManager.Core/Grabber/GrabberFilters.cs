using System.Text.RegularExpressions;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core.Grabber;

/// <summary>
/// The project's filters: domains, file types, sizes and the include/exclude expressions. The expressions run with
/// .NET's non-backtracking engine, so a pattern typed by the user (or stored in a project) cannot hang the crawler.
/// </summary>
public sealed class GrabberFilters
{
    private readonly GrabberProject _project;
    private readonly Regex? _include;
    private readonly Regex? _exclude;
    private readonly string _site;

    public GrabberFilters(GrabberProject project)
    {
        _project = project;
        _include = Compile(project.IncludePattern);
        _exclude = Compile(project.ExcludePattern);
        _site = CrawlUrls.SiteOf(new Uri(project.StartUrl).Host);
    }

    /// <summary>File types offered as presets in the wizard.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
    {
        ["images"] = ["jpg", "jpeg", "png", "gif", "webp", "avif", "svg", "bmp", "tif", "tiff"],
        ["documents"] = ["pdf", "doc", "docx", "odt", "rtf", "txt", "xls", "xlsx", "ods", "csv", "ppt", "pptx", "odp", "epub"],
        ["audio"] = ["mp3", "flac", "wav", "ogg", "m4a", "aac", "opus"],
        ["video"] = ["mp4", "mkv", "webm", "mov", "avi", "m4v"],
        ["archives"] = ["zip", "rar", "7z", "gz", "tar", "bz2", "xz", "iso"],
    };

    private static Regex? Compile(string pattern) =>
        string.IsNullOrWhiteSpace(pattern)
            ? null
            : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));

    /// <summary>Null when the expression is usable, otherwise why not, for the user.</summary>
    public static string? CheckPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        if (pattern.Length > 1000)
            return Strings.GrabberPatternTooLong;
        try
        {
            _ = Compile(pattern);
            return null;
        }
        catch (NotSupportedException)
        {
            // Constructs the safe engine refuses (backreferences, lookarounds, atomic groups).
            return Strings.GrabberPatternUnsupported;
        }
        catch (ArgumentException ex)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.GrabberPatternInvalid, ex.Message);
        }
    }

    /// <summary>The host may be visited: the start page's site, or a listed domain.</summary>
    public bool DomainAllowed(Uri url)
    {
        if (CrawlUrls.HostMatches(url.Host, _site))
            return true;
        return !_project.SameDomainOnly && _project.AllowedDomains.Any(d => CrawlUrls.HostMatches(url.Host, d));
    }

    /// <summary>The exclusion expression rules the address out (pages and files alike).</summary>
    public bool Excluded(Uri url) => _exclude is not null && SafeMatch(_exclude, url.AbsoluteUri);

    /// <summary>A file passes the type and inclusion filters (sizes are checked once known).</summary>
    public bool FileWanted(Uri url, string extension)
    {
        if (Excluded(url))
            return false;
        if (_project.Extensions.Count > 0 && !_project.Extensions.Contains(extension))
            return false;
        return _include is null || SafeMatch(_include, url.AbsoluteUri);
    }

    /// <summary>An unknown size passes: the file is listed and its size shows as unknown.</summary>
    public bool SizeWanted(long? size) =>
        size is not { } s || ((_project.MinSizeBytes <= 0 || s >= _project.MinSizeBytes) && (_project.MaxSizeBytes <= 0 || s <= _project.MaxSizeBytes));

    private static bool SafeMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
