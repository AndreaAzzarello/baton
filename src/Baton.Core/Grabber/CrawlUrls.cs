using System.Globalization;
using System.Text;

namespace Baton.Core.Grabber;

/// <summary>
/// Normalizes addresses so the same resource is fetched once: lower-case scheme and host, no default port, no
/// fragment, no login, session and tracking parameters removed (also ";jsessionid=" in the path), query parameters
/// in a stable order.
/// </summary>
public static class CrawlUrls
{
    /// <summary>Parameters that only identify a visit, never a different resource.</summary>
    private static readonly string[] SessionParameters =
    [
        "jsessionid", "phpsessid", "sid", "sessionid", "session_id", "session", "cfid", "cftoken", "aspsessionid",
        "fbclid", "gclid", "dclid", "msclkid", "yclid", "_ga", "_gl", "mc_cid", "mc_eid", "igshid",
    ];

    public static bool IsSessionParameter(string name)
    {
        var key = name.ToLowerInvariant();
        return SessionParameters.Contains(key) || key.StartsWith("utm_", StringComparison.Ordinal)
               || key.StartsWith("aspsessionid", StringComparison.Ordinal);
    }

    /// <summary>The normalized absolute address, or null for anything that is not http or https.</summary>
    public static Uri? Normalize(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || url.Host.Length == 0)
            return null;
        var path = url.AbsolutePath;
        var semicolon = path.IndexOf(";jsessionid=", StringComparison.OrdinalIgnoreCase);
        if (semicolon >= 0)
            path = path[..semicolon];
        if (path.Length == 0)
            path = "/";

        var query = url.Query.Length > 1 ? url.Query[1..] : "";
        var kept = new List<(string Key, string Raw)>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            if (!IsSessionParameter(key))
                kept.Add((key, pair));
        }
        // A stable order: "?b=2&a=1" and "?a=1&b=2" are the same page.
        var ordered = kept.OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Raw, StringComparer.Ordinal).Select(p => p.Raw);
        var builder = new StringBuilder();
        builder.Append(url.Scheme).Append("://").Append(url.IdnHost.ToLowerInvariant());
        if (!url.IsDefaultPort)
            builder.Append(':').Append(url.Port.ToString(CultureInfo.InvariantCulture));
        builder.Append(path);
        var q = string.Join('&', ordered);
        if (q.Length > 0)
            builder.Append('?').Append(q);
        return Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var normalized) ? normalized : null;
    }

    /// <summary>Resolves a link found in a page; null for javascript:, mailto:, data: and anything not http(s).</summary>
    public static Uri? Resolve(Uri baseUrl, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var text = reference.Trim();
        if (text.StartsWith('#'))
            return null;
        if (!Uri.TryCreate(baseUrl, text, out var resolved) || resolved.Scheme is not ("http" or "https"))
            return null;
        return resolved;
    }

    /// <summary>The address without its fragment, as the key of a resource.</summary>
    public static string Key(Uri url) => Normalize(url)?.AbsoluteUri ?? url.GetLeftPart(UriPartial.Query);

    /// <summary>The site a host belongs to for "same domain": the host without a leading "www.".</summary>
    public static string SiteOf(string host)
    {
        var lower = host.ToLowerInvariant();
        return lower.StartsWith("www.", StringComparison.Ordinal) ? lower[4..] : lower;
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or one of its subdomains.</summary>
    public static bool HostMatches(string host, string domain)
    {
        var h = host.ToLowerInvariant().TrimEnd('.');
        var d = domain.Trim().ToLowerInvariant().TrimStart('*', '.').TrimEnd('.');
        return d.Length > 0 && (h == d || h.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>
    /// The extension of the last path segment, lower case without the dot ("" when there is none), as the grabber's
    /// filters and the page/file guess use it.
    /// </summary>
    public static string ExtensionOf(Uri url)
    {
        var path = url.AbsolutePath;
        var slash = path.LastIndexOf('/');
        var last = Uri.UnescapeDataString(path[(slash + 1)..]);
        var dot = last.LastIndexOf('.');
        return dot <= 0 || dot == last.Length - 1 ? "" : last[(dot + 1)..].ToLowerInvariant();
    }

    private static readonly HashSet<string> PageExtensions =
        ["", "html", "htm", "xhtml", "shtml", "shtm", "php", "php3", "php4", "php5", "asp", "aspx", "jsp", "jspx", "cfm", "cgi", "pl", "do", "action"];

    /// <summary>A link that probably leads to another page rather than a file (decided for sure by the response type).</summary>
    public static bool LooksLikePage(Uri url) => url.AbsolutePath.EndsWith('/') || PageExtensions.Contains(ExtensionOf(url));
}

/// <summary>
/// Stops the crawler from wandering into address spaces without end: calendars that always have a "next month",
/// paths that repeat themselves (relative links resolved against the wrong folder), endless parameters. Each check
/// is cheap and deterministic.
/// </summary>
public sealed class TrapGuard(int maxPerPattern = 100)
{
    public const int MaxUrlLength = 2000;
    public const int MaxPathSegments = 30;
    public const int MaxRepeatedSegment = 3;
    public const int MaxQueryParameters = 30;

    private readonly Dictionary<string, int> _patterns = new(StringComparer.Ordinal);

    public int MaxPerPattern { get; } = Math.Max(1, maxPerPattern);

    /// <summary>Why the address looks like a trap, or null when it may be visited.</summary>
    public static string? Inspect(Uri url)
    {
        if (url.AbsoluteUri.Length > MaxUrlLength)
            return "address too long";
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > MaxPathSegments)
            return "path too deep";
        if (segments.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() >= MaxRepeatedSegment))
            return "path repeats itself";
        if (url.Query.Count(c => c == '&') + 1 > MaxQueryParameters && url.Query.Length > 1)
            return "too many parameters";
        return null;
    }

    /// <summary>
    /// The shape of an address: numbers replaced and parameter values dropped, so "?month=2026-11" and "?month=2026-12",
    /// or "/page-7.html" and "/page-8.html", share one pattern.
    /// </summary>
    public static string PatternOf(Uri url)
    {
        var path = DigitsOut(url.AbsolutePath);
        var keys = url.Query.Length > 1
            ? string.Join('&', url.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=')[0]).Order(StringComparer.Ordinal))
            : "";
        return $"{url.Host}{path}?{keys}";
    }

    private static string DigitsOut(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inDigits = false;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                if (!inDigits)
                    builder.Append('0');
                inDigits = true;
            }
            else
            {
                builder.Append(c);
                inDigits = false;
            }
        }
        return builder.ToString();
    }

    /// <summary>Counts one more page of this shape; false once the shape has had its share.</summary>
    public bool TryCount(Uri url)
    {
        var pattern = PatternOf(url);
        lock (_patterns)
        {
            _patterns.TryGetValue(pattern, out var count);
            if (count >= MaxPerPattern)
                return false;
            _patterns[pattern] = count + 1;
            return true;
        }
    }
}
