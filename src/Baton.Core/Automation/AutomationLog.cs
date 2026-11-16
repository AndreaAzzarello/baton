namespace Baton.Core.Automation;

/// <summary>One line of the activity log: what an automatic caller (API, MCP) or the app on its own did, and how it went.</summary>
public sealed record AutomationLogEntry(long Id, DateTime AtUtc, string Origin, string Action, string Outcome, long? DownloadId, string Detail);

/// <summary>
/// The log of what the local API, MCP and the rules did (the command line is not logged). It keeps 30 days or 10,000 lines.
/// Nothing sensitive goes in: an address is cut before its query and fragment (and any user name or password in it), and the
/// text is bounded; cookies and tokens are never passed here.
/// </summary>
public sealed class AutomationLog(DownloadStore store, Func<DateTime>? clock = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public const int MaxEntries = 10_000;
    private const int MaxDetail = 300;
    private const int PruneEvery = 200;

    public const string Api = "api";
    public const string Mcp = "mcp";
    public const string RulesOrigin = "rules";
    public const string Duplicates = "duplicates";

    /// <summary>The origins the window can filter by.</summary>
    public static readonly string[] Origins = [Api, Mcp, RulesOrigin, Duplicates];

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private int _written;

    /// <summary>Raised after a line is written (possibly on a worker thread).</summary>
    public event Action? Changed;

    public void Record(string origin, string action, string outcome, string detail = "", long? downloadId = null)
    {
        var now = _clock();
        store.AddAutomationLog(new AutomationLogEntry(0, now, origin, action, outcome, downloadId, Clean(detail)));
        if (Interlocked.Increment(ref _written) % PruneEvery == 1)
            store.PruneAutomationLog(now, MaxAge, MaxEntries);
        Changed?.Invoke();
    }

    public IReadOnlyList<AutomationLogEntry> Read(string? origin = null, int limit = 500) => store.ReadAutomationLog(origin, limit);

    public void Clear()
    {
        store.ClearAutomationLog();
        Changed?.Invoke();
    }

    /// <summary>Removes old lines now (also done while writing).</summary>
    public void Prune() => store.PruneAutomationLog(_clock(), MaxAge, MaxEntries);

    /// <summary>Cuts every address in the text before its user info, query and fragment, and bounds the length.</summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Contains("://", StringComparison.Ordinal))
            {
                var scheme = w.IndexOf("://", StringComparison.Ordinal) + 3;
                // User info first, taken up to the last "@" before any "/": a password may hold "@", "?" or "#".
                var at = w.LastIndexOf('@');
                if (at >= scheme && w.IndexOf('/', scheme, at - scheme) < 0)
                    w = w[..scheme] + w[(at + 1)..];
                var end = w.IndexOfAny(['?', '#'], scheme);
                if (end >= 0)
                    w = w[..end];
                words[i] = w;
            }
            else if (w.Contains('?') && (w.Contains('=') || w.Contains('&')))
                words[i] = w[..w.IndexOf('?')];
        }
        var cleaned = string.Join(' ', words);
        return cleaned.Length > MaxDetail ? cleaned[..MaxDetail] + "…" : cleaned;
    }
}
