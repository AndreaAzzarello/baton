using System.Globalization;

namespace DownloadManager.Core.Grabber;

/// <summary>
/// robots.txt as RFC 9309 describes it: the group of the most specific matching user-agent (else "*"), Allow and
/// Disallow rules with "*" and "$", the longest match wins and Allow wins a tie. Crawl-delay, which is not in the
/// RFC but widely used, is read too.
/// </summary>
public sealed class RobotsTxt
{
    private readonly List<(string Pattern, bool Allow)> _rules;

    private RobotsTxt(List<(string Pattern, bool Allow)> rules, double? crawlDelay)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    /// <summary>Everything allowed (no robots.txt, or 4xx).</summary>
    public static RobotsTxt AllowAll { get; } = new([], null);

    /// <summary>Nothing allowed (robots.txt unreachable: RFC 9309 says to assume a complete disallow).</summary>
    public static RobotsTxt DisallowAll { get; } = new([("/", false)], null);

    /// <summary>Seconds between requests asked by the site, if any.</summary>
    public double? CrawlDelay { get; }

    public int RuleCount => _rules.Count;

    /// <summary>The crawler's name as robots.txt groups name it: the first product token of the user agent.</summary>
    public static string ProductToken(string userAgent)
    {
        var token = userAgent.Trim();
        var end = token.IndexOfAny([' ', '/', '(', ';']);
        return end > 0 ? token[..end] : token.Length > 0 ? token : "*";
    }

    public static RobotsTxt Parse(string text, string userAgent)
    {
        var token = ProductToken(userAgent).ToLowerInvariant();
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;

        // Bigger files are cut at 500 KiB, as the RFC allows.
        if (text.Length > 512 * 1024)
            text = text[..(512 * 1024)];
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (field)
            {
                case "user-agent":
                    if (current is null || !lastWasAgent)
                    {
                        current = new Group();
                        groups.Add(current);
                    }
                    current.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "allow" or "disallow" when current is not null:
                    if (value.Length > 0)
                        current.Rules.Add((value, field == "allow"));
                    lastWasAgent = false;
                    break;
                case "crawl-delay" when current is not null:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay) && delay >= 0)
                        current.Delay = delay;
                    lastWasAgent = false;
                    break;
                default:
                    lastWasAgent = false; // sitemap and unknown fields end a run of user-agent lines
                    break;
            }
        }

        // Every group naming this crawler is merged; otherwise every "*" group.
        var mine = groups.Where(g => g.Agents.Any(a => a != "*" && a.Length > 0 && token.StartsWith(a, StringComparison.Ordinal))).ToList();
        if (mine.Count == 0)
            mine = groups.Where(g => g.Agents.Contains("*")).ToList();
        var rules = mine.SelectMany(g => g.Rules).ToList();
        var crawlDelay = mine.Select(g => g.Delay).FirstOrDefault(d => d is not null);
        return new RobotsTxt(rules, crawlDelay);
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<(string Pattern, bool Allow)> Rules { get; } = [];
        public double? Delay { get; set; }
    }

    /// <summary>Whether the path (with its query) may be fetched.</summary>
    public bool IsAllowed(Uri url)
    {
        if (url.AbsolutePath == "/robots.txt")
            return true;
        var target = url.PathAndQuery;
        var bestLength = -1;
        var allowed = true;
        foreach (var (pattern, allow) in _rules)
        {
            if (!Matches(pattern, target))
                continue;
            var length = pattern.Length;
            if (length > bestLength || (length == bestLength && allow))
            {
                bestLength = length;
                allowed = allow;
            }
        }
        return allowed;
    }

    /// <summary>"*" matches any characters, a final "$" anchors at the end; otherwise a prefix match.</summary>
    public static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored)
            pattern = pattern[..^1];
        pattern = NormalizeEscapes(pattern);
        path = NormalizeEscapes(path);
        return Match(pattern, 0, path, 0, anchored);
    }

    private static bool Match(string pattern, int p, string path, int s, bool anchored)
    {
        // Iterative wildcard matching with one backtracking point per star (no exponential blow-up).
        int star = -1, mark = 0;
        while (true)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = s;
                continue;
            }
            if (p == pattern.Length)
            {
                if (!anchored || s == path.Length)
                    return true;
            }
            else if (s < path.Length && pattern[p] == path[s])
            {
                p++;
                s++;
                continue;
            }
            if (star < 0 || mark >= path.Length)
                return false;
            p = star + 1;
            s = ++mark;
        }
    }

    /// <summary>Percent-escapes compared without regard to case, as RFC 9309 asks.</summary>
    private static string NormalizeEscapes(string value)
    {
        if (!value.Contains('%'))
            return value;
        var chars = value.ToCharArray();
        for (var i = 0; i + 2 < chars.Length; i++)
        {
            if (chars[i] == '%' && Uri.IsHexDigit(chars[i + 1]) && Uri.IsHexDigit(chars[i + 2]))
            {
                chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
                chars[i + 2] = char.ToUpperInvariant(chars[i + 2]);
            }
        }
        return new string(chars);
    }
}
