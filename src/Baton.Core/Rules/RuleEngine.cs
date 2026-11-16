using System.Text.RegularExpressions;

namespace Baton.Core.Rules;

/// <summary>What is known about a download when it is added (and nothing more: the rules are evaluated once, then).</summary>
/// <param name="FileName">The name given or guessed from the address; null when only the server will tell.</param>
/// <param name="CategoryId">The category the file would get by its name or address.</param>
/// <param name="Size">The size when something told it (the browser's Content-Length, a probe); null when unknown.</param>
public sealed record RuleInput(Uri Url, string Origin, string? FileName, long? CategoryId, long? Size);

/// <summary>Conditions in AND, the first enabled rule that holds wins.</summary>
public static class RuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static Rule? Match(IEnumerable<Rule> rules, RuleInput input) =>
        rules.Where(r => r.Enabled && !r.Unreadable).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).FirstOrDefault(r => Matches(r.Conditions, input));

    public static bool Matches(RuleConditions c, RuleInput input)
    {
        if (c.IsEmpty)
            return false;
        if (c.Origins.Count > 0 && !c.Origins.Contains(input.Origin, StringComparer.OrdinalIgnoreCase))
            return false;
        if (c.Domains.Count > 0 && !c.Domains.Any(d => HostMatches(input.Url.IdnHost, d)))
            return false;
        var address = input.Url.AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(c.UrlPattern) && !GlobMatches(c.UrlPattern!, address))
            return false;
        if (!string.IsNullOrWhiteSpace(c.UrlRegex) && !RegexMatches(c.UrlRegex!, address))
            return false;
        if (c.Extensions.Count > 0)
        {
            var extension = Path.GetExtension(input.FileName ?? "").TrimStart('.');
            if (extension.Length == 0 || !c.Extensions.Any(e => string.Equals(e.Trim().TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (c.CategoryId is { } category && input.CategoryId != category)
            return false;
        if (c.MinSize is not null || c.MaxSize is not null)
        {
            if (input.Size is not { } size)
                return false;
            if (c.MinSize is { } min && size < min || c.MaxSize is { } max && size > max)
                return false;
        }
        return true;
    }

    /// <summary>The host is the domain or a subdomain of it (never just a name that ends with the same letters).</summary>
    public static bool HostMatches(string host, string domain)
    {
        domain = domain.Trim().TrimStart('*').Trim('.');
        if (domain.Length == 0)
            return false;
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    public static bool GlobMatches(string pattern, string text)
    {
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return RegexMatches(expression, text, RegexOptions.Singleline);
    }

    /// <summary>User expressions run without backtracking, so a hostile or careless pattern cannot stall the app.</summary>
    public static bool RegexMatches(string pattern, string text, RegexOptions extra = RegexOptions.None)
    {
        try
        {
            return Compiled(pattern, extra)?.IsMatch(text) ?? false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private const int MaxCached = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex?> Cache = new();

    /// <summary>
    /// Number of expressions built so far (tests): a rule's expression is built once, not at every match. The cache is keyed by the
    /// expression's text, so a rule that is edited simply asks for a new one; the old one is dropped when the cache is full.
    /// </summary>
    public static int CompileCount;

    private static Regex? Compiled(string pattern, RegexOptions extra)
    {
        var key = (pattern, extra);
        if (Cache.TryGetValue(key, out var found))
            return found;
        if (Cache.Count >= MaxCached)
            Cache.Clear();
        Regex? built;
        try
        {
            Interlocked.Increment(ref CompileCount);
            built = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | extra, RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            built = null; // not usable: never matches, and is not tried again
        }
        Cache[key] = built;
        return built;
    }

    /// <summary>Null when <paramref name="pattern"/> is a usable expression (the window shows the message otherwise).</summary>
    public static string? ValidateRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// A rule that comes from an imported file arrives marked as imported, and off when it opens a file or adds without asking:
    /// the user turns those on after reading them.
    /// </summary>
    public static Rule ForImport(Rule rule)
    {
        var copy = rule.Clone();
        copy.Id = 0;
        copy.Imported = true;
        if (copy.Actions.IsRisky)
            copy.Enabled = false;
        return copy;
    }
}
