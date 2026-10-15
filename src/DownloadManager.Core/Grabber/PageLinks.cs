using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace DownloadManager.Core.Grabber;

/// <summary>What a reference in a page is for, which decides whether and how the crawler follows it.</summary>
public enum LinkKind
{
    /// <summary>A link to another page (a, area, frames, meta refresh): followed within the depth limit.</summary>
    Page,

    /// <summary>Something the page shows or runs (images, scripts, media, icons): part of the page.</summary>
    Resource,

    /// <summary>A style sheet: part of the page, and read in turn for the images and fonts it uses.</summary>
    Stylesheet,
}

/// <summary>A reference found in a page or a style sheet, already resolved against its base address.</summary>
public sealed record FoundLink(Uri Url, LinkKind Kind, string Source);

/// <summary>
/// Finds every address in static HTML (parsed by AngleSharp exactly as a browser's HTML5 parser would, scripts never
/// run) and CSS, and can replace each one: the same walk serves the crawler (to find links) and the offline copy (to
/// point them at the local files).
/// </summary>
public static partial class PageLinks
{
    /// <summary>Called for each address; returning a string replaces the address in the document.</summary>
    public delegate string? Visitor(string raw, Uri resolved, LinkKind kind);

    private static readonly HtmlParser Parser = new(new HtmlParserOptions { IsScripting = false, IsNotConsumingCharacterReferences = false });

    public static IHtmlDocument Parse(string html) => Parser.ParseDocument(html);

    /// <summary>The page's base address: its own, or the first &lt;base href&gt;.</summary>
    public static Uri BaseOf(IDocument document, Uri pageUrl) =>
        document.QuerySelector("base[href]")?.GetAttribute("href") is { } href && Uri.TryCreate(pageUrl, href.Trim(), out var b)
            && b.Scheme is "http" or "https"
            ? b
            : pageUrl;

    public static List<FoundLink> Find(IDocument document, Uri pageUrl)
    {
        var found = new List<FoundLink>();
        Walk(document, pageUrl, (_, url, kind) =>
        {
            found.Add(new FoundLink(url, kind, ""));
            return null;
        });
        return found;
    }

    /// <summary>Visits every address of the document; the visitor's non-null answers replace them in place.</summary>
    public static void Walk(IDocument document, Uri pageUrl, Visitor visit)
    {
        var baseUrl = BaseOf(document, pageUrl);

        void Attribute(string selector, string attribute, LinkKind kind)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                var raw = element.GetAttribute(attribute);
                if (CrawlUrls.Resolve(baseUrl, raw) is not { } url || visit(raw!, url, kind) is not { } replacement)
                    continue;
                element.SetAttribute(attribute, replacement);
                if (element.LocalName is "script" or "link")
                {
                    // A local copy is not a CORS request (file:// pages refuse those) and a rewritten style sheet no
                    // longer matches its integrity hash.
                    element.RemoveAttribute("integrity");
                    element.RemoveAttribute("crossorigin");
                }
            }
        }

        Attribute("a[href]", "href", LinkKind.Page);
        Attribute("area[href]", "href", LinkKind.Page);
        Attribute("iframe[src]", "src", LinkKind.Page);
        Attribute("frame[src]", "src", LinkKind.Page);
        foreach (var link in document.QuerySelectorAll("link[href]"))
        {
            var rel = (link.GetAttribute("rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            LinkKind? kind = rel.Contains("stylesheet") ? LinkKind.Stylesheet
                : rel.Any(r => r is "icon" or "apple-touch-icon" or "preload" or "image_src" or "manifest") ? LinkKind.Resource
                : rel.Any(r => r is "next" or "prev") ? LinkKind.Page
                : null;
            if (kind is null)
                continue;
            var raw = link.GetAttribute("href");
            if (CrawlUrls.Resolve(baseUrl, raw) is { } url && visit(raw!, url, kind.Value) is { } replacement)
            {
                link.SetAttribute("href", replacement);
                link.RemoveAttribute("integrity");
                link.RemoveAttribute("crossorigin");
            }
        }
        Attribute("script[src]", "src", LinkKind.Resource);
        Attribute("img[src]", "src", LinkKind.Resource);
        Attribute("img[data-src]", "data-src", LinkKind.Resource);
        Attribute("input[type=image][src]", "src", LinkKind.Resource);
        Attribute("video[src]", "src", LinkKind.Resource);
        Attribute("video[poster]", "poster", LinkKind.Resource);
        Attribute("audio[src]", "src", LinkKind.Resource);
        Attribute("source[src]", "src", LinkKind.Resource);
        Attribute("track[src]", "src", LinkKind.Resource);
        Attribute("embed[src]", "src", LinkKind.Resource);
        Attribute("object[data]", "data", LinkKind.Resource);
        Attribute("body[background], table[background], td[background], th[background]", "background", LinkKind.Resource);

        foreach (var attribute in new[] { "srcset", "data-srcset" })
        {
            foreach (var element in document.QuerySelectorAll($"img[{attribute}], source[{attribute}]"))
            {
                var value = element.GetAttribute(attribute) ?? "";
                var changed = false;
                var candidates = SrcSet.Parse(value);
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (CrawlUrls.Resolve(baseUrl, candidates[i].Url) is { } url && visit(candidates[i].Url, url, LinkKind.Resource) is { } replacement)
                    {
                        candidates[i] = candidates[i] with { Url = replacement };
                        changed = true;
                    }
                }
                if (changed)
                    element.SetAttribute(attribute, SrcSet.Format(candidates));
            }
        }

        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]"))
        {
            if (!string.Equals(meta.GetAttribute("http-equiv")?.Trim(), "refresh", StringComparison.OrdinalIgnoreCase))
                continue;
            var content = meta.GetAttribute("content") ?? "";
            var match = RefreshUrl().Match(content);
            if (!match.Success)
                continue;
            var raw = match.Groups["url"].Value.Trim().Trim('\'', '"');
            if (CrawlUrls.Resolve(baseUrl, raw) is { } url && visit(raw, url, LinkKind.Page) is { } replacement)
                meta.SetAttribute("content", content[..match.Groups["url"].Index] + replacement);
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var css = element.GetAttribute("style") ?? "";
            var rewritten = CssLinks.Walk(css, baseUrl, visit);
            if (!ReferenceEquals(rewritten, css))
                element.SetAttribute("style", rewritten);
        }
        foreach (var style in document.QuerySelectorAll("style"))
        {
            var css = style.TextContent;
            var rewritten = CssLinks.Walk(css, baseUrl, visit);
            if (!ReferenceEquals(rewritten, css))
                style.TextContent = rewritten;
        }
    }

    [GeneratedRegex(@"^\s*\d*\s*[;,]?\s*url\s*=\s*(?<url>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefreshUrl();
}

/// <summary>Addresses in CSS: url(...) and @import "...".</summary>
public static partial class CssLinks
{
    public static List<FoundLink> Find(string css, Uri baseUrl)
    {
        var found = new List<FoundLink>();
        Walk(css, baseUrl, (_, url, kind) =>
        {
            found.Add(new FoundLink(url, kind, "css"));
            return null;
        });
        return found;
    }

    /// <summary>
    /// Visits each address; returns the CSS with the replacements, or the same string instance when nothing changed.
    /// Comments are skipped, so a commented-out url() is neither fetched nor rewritten.
    /// </summary>
    public static string Walk(string css, Uri baseUrl, PageLinks.Visitor visit)
    {
        if (css.IndexOf("url", StringComparison.OrdinalIgnoreCase) < 0 && !css.Contains("@import", StringComparison.OrdinalIgnoreCase))
            return css;
        StringBuilder? output = null;
        var copied = 0;
        // A comment that never closes runs to the end of the sheet: nothing after it is read, and the expression is not
        // left to look for its end again at every "/*".
        var scanned = css;
        var open = css.LastIndexOf("/*", StringComparison.Ordinal);
        if (open >= 0 && css.IndexOf("*/", open + 2, StringComparison.Ordinal) < 0)
            scanned = css[..open];
        MatchCollection matches;
        try
        {
            matches = Reference().Matches(scanned);
            _ = matches.Count; // evaluated here, within the time limit
        }
        catch (RegexMatchTimeoutException)
        {
            return css; // a sheet built to be slow: its addresses are not followed
        }
        foreach (Match match in matches)
        {
            if (match.Groups["comment"].Success)
                continue;
            var group = match.Groups["url"].Success ? match.Groups["url"] : match.Groups["import"];
            var raw = group.Value.Trim();
            if (raw.Length == 0 || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var kind = match.Groups["import"].Success || IsImport(css, match.Index) ? LinkKind.Stylesheet : LinkKind.Resource;
            if (CrawlUrls.Resolve(baseUrl, Unescape(raw)) is not { } url || visit(raw, url, kind) is not { } replacement)
                continue;
            output ??= new StringBuilder(css.Length + 64);
            output.Append(css, copied, group.Index - copied);
            output.Append(EscapeForCss(replacement, quoted: match.Groups["quote"].Value.Length > 0 || match.Groups["import"].Success));
            copied = group.Index + group.Length;
        }
        if (output is null)
            return css;
        output.Append(css, copied, css.Length - copied);
        return output.ToString();
    }

    /// <summary>"@import url(...)": the url() right after an @import names a style sheet.</summary>
    private static bool IsImport(string css, int index)
    {
        var start = Math.Max(0, index - 24);
        return css[start..index].TrimEnd().EndsWith("@import", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>CSS escapes in an address ("\(" etc.) are undone before it is resolved.</summary>
    private static string Unescape(string value) => value.Contains('\\') ? CssEscape().Replace(value, "$1") : value;

    private static string EscapeForCss(string value, bool quoted) =>
        quoted ? value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "\\'")
               : value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace(" ", "%20").Replace("'", "\\'").Replace("\"", "\\\"");

    // Bounded: an address is at most 4096 characters and cannot span lines, and the whole scan has a time limit, so a
    // hostile style sheet (thousands of "url(" without ")") cannot keep the crawler busy.
    [GeneratedRegex("""(?<comment>/\*.*?\*/)|url\(\s*(?<quote>['"]?)(?<url>[^\r\n]{0,4096}?)\k<quote>\s*\)|@import\s+(?<q2>['"])(?<import>[^'"\r\n]{0,4096})\k<q2>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Reference();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex CssEscape();
}

/// <summary>The srcset attribute: candidates "address [descriptor]" separated by commas (HTML's parsing rules).</summary>
public static class SrcSet
{
    public sealed record Candidate(string Url, string Descriptor);

    public static List<Candidate> Parse(string value)
    {
        var result = new List<Candidate>();
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && (char.IsWhiteSpace(value[i]) || value[i] == ','))
                i++;
            if (i >= value.Length)
                break;
            var start = i;
            while (i < value.Length && !char.IsWhiteSpace(value[i]))
                i++;
            var url = value[start..i];
            var descriptor = "";
            if (url.EndsWith(','))
            {
                url = url.TrimEnd(',');
            }
            else
            {
                // The descriptor runs to the next comma that is not inside parentheses.
                var depth = 0;
                var d = i;
                while (i < value.Length && (value[i] != ',' || depth > 0))
                {
                    if (value[i] == '(')
                        depth++;
                    else if (value[i] == ')')
                        depth = Math.Max(0, depth - 1);
                    i++;
                }
                descriptor = value[d..i].Trim();
            }
            if (url.Length > 0)
                result.Add(new Candidate(url, descriptor));
        }
        return result;
    }

    public static string Format(IEnumerable<Candidate> candidates) =>
        string.Join(", ", candidates.Select(c => c.Descriptor.Length > 0 ? $"{c.Url.Replace(",", "%2C").Replace(" ", "%20")} {c.Descriptor}" : c.Url.Replace(",", "%2C").Replace(" ", "%20")));
}
