using Baton.Core.Automation;

namespace Baton.Core.Integration;

/// <summary>
/// Refusals (of API requests that did not show the token (wrong host, a web page, no token, a wrong one) or of an MCP client above its level) can come in thousands.
/// The first of each reason in a minute is written at once; the others are counted and written as one line ("×N") when the
/// minute is over (at the next refusal of that reason, or when the API stops), so a loop cannot fill the activity log.
/// </summary>
public sealed class UnauthenticatedRefusals(AutomationLog log, Func<DateTime> clock, string origin = AutomationLog.Api)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class Counter
    {
        public DateTime Start;
        public int Suppressed;
        public string Action = "";
        public string Outcome = "";
        public string Detail = "";
    }

    private readonly Dictionary<string, Counter> _byReason = [];
    private readonly object _lock = new();

    public void Note(string reason, string action, string outcome, string detail)
    {
        string? summaryDetail = null;
        string summaryAction = "", summaryOutcome = "";
        var writeNow = false;
        lock (_lock)
        {
            var now = clock();
            if (!_byReason.TryGetValue(reason, out var c))
                _byReason[reason] = c = new Counter { Start = DateTime.MinValue };
            if (now - c.Start >= Window)
            {
                if (c.Suppressed > 0)
                    (summaryAction, summaryOutcome, summaryDetail) = (c.Action, c.Outcome, Summary(reason, c.Suppressed));
                c.Start = now;
                c.Suppressed = 0;
                writeNow = true;
            }
            else
            {
                c.Suppressed++;
                (c.Action, c.Outcome, c.Detail) = (action, outcome, detail);
            }
        }
        if (summaryDetail is not null)
            log.Record(origin, summaryAction, summaryOutcome, summaryDetail);
        if (writeNow)
            log.Record(origin, action, outcome, detail);
    }

    /// <summary>Writes the counts still waiting (the API is stopping).</summary>
    public void Flush()
    {
        List<(string Action, string Outcome, string Detail)> pending = [];
        lock (_lock)
        {
            foreach (var (reason, c) in _byReason)
            {
                if (c.Suppressed > 0)
                    pending.Add((c.Action, c.Outcome, Summary(reason, c.Suppressed)));
                c.Suppressed = 0;
            }
        }
        foreach (var (action, outcome, detail) in pending)
            log.Record(origin, action, outcome, detail);
    }

    private static string Summary(string reason, int count) => ActivityText.Reason(reason, "×" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
