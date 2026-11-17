using Baton.Core.Rules;

namespace Baton.Core;

public sealed partial class DownloadService
{
    private List<Rule> _rules = [];

    /// <summary>The automatic rules, in order.</summary>
    public IReadOnlyList<Rule> Rules
    {
        get
        {
            lock (_lock)
                return _rules.Select(r => r.Clone()).ToList();
        }
    }

    /// <summary>Raised after the rules were saved.</summary>
    public event Action? RulesChanged;

    /// <summary>
    /// A finished download's file the rule that matched wants shown in its folder or opened. The app does it; it is raised only when
    /// Baton itself decided it is safe (a file that is not a program or script, with a scan that found nothing, for "open").
    /// </summary>
    public event Action<RuleFileRequest>? RuleFileActionRequested;

    /// <summary>Replaces the rules (order = list order). A rule from the outside that opens files or skips the confirmation stays off.</summary>
    public void SaveRules(IReadOnlyList<Rule> rules)
    {
        var copy = rules.Select(r => r.Clone()).ToList();
        _store.SaveRules(copy);
        lock (_lock)
            _rules = copy;
        RulesChanged?.Invoke();
    }

    /// <summary>What the rules would do with an address (the rule window's preview and the entries that ask before adding).</summary>
    public RulePreview PreviewRules(string url, string origin, long? size = null, string? fileName = null)
    {
        if (!Urls.TryParseDownload(url, out var parsed))
            return new RulePreview(null, null, null);
        var (uri, _, _) = Urls.SplitLogin(parsed);
        var name = string.IsNullOrWhiteSpace(fileName) ? GuessName(uri) : FileNames.Sanitize(fileName);
        lock (_lock)
        {
            var rule = MatchRuleLocked(uri, origin, name, size);
            if (rule is null)
                return new RulePreview(null, null, null);
            var renamed = rule.Actions.Rename is { Length: > 0 } pattern && name is not null ? RenameFor(pattern, name, uri, rule.Actions.CategoryId ?? CategoryOf(uri, name)?.Id, null) : null;
            var category = _categories.FirstOrDefault(c => c.Id == rule.Actions.CategoryId);
            var folder = rule.Actions.Folder is { Length: > 0 } f ? f : category?.ResolveFolder(_settings);
            return new RulePreview(rule.Clone(), folder, renamed);
        }
    }

    /// <summary>True when the rule that matches skips the confirmation for an entry that would ask first.</summary>
    public bool RuleSkipsConfirmation(string url, string origin, long? size = null, string? fileName = null) =>
        PreviewRules(url, origin, size, fileName).Rule is { Actions.SkipConfirmation: true };

    private static string? GuessName(Uri uri) =>
        uri.Query.Length == 0 && FileNames.FromUrl(uri) is { } guessed && Path.HasExtension(guessed) ? guessed : null;

    private Category? CategoryOf(Uri uri, string? name) => Core.Categories.MatchUrl(_categories, uri.AbsoluteUri, name);

    private Rule? MatchRuleLocked(Uri uri, string origin, string? name, long? size) =>
        _rules.Count == 0 ? null : RuleEngine.Match(_rules, new RuleInput(uri, origin, name, CategoryOf(uri, name)?.Id, size));

    private string RenameFor(string pattern, string originalName, Uri uri, long? categoryId, long? ownId, string? directory = null)
    {
        var category = _categories.FirstOrDefault(c => c.Id == categoryId);
        return RenameTemplate.Apply(pattern, originalName, uri.IdnHost, category?.Name, _clock.UtcNow.ToLocalTime(),
            directory is null ? null : candidate => NameTaken(directory, candidate, ownId));
    }

    private bool NameTaken(string directory, string name, long? ownId) =>
        File.Exists(Path.Combine(directory, name))
        || _items.Any(i => i.Id != ownId && string.Equals(i.FileName, name, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(i.Directory.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>What an add becomes once the rule that matches (if any) has filled in what the entry did not choose itself.</summary>
    private sealed record RuleOutcome(string? Directory, long? CategoryId, long? QueueId, bool? StartNow, bool Paused, RequestContext? Context, AppliedRule? Applied);

    /// <summary>
    /// Picks the first rule that holds for this add and fills in what the entry left open. An explicit choice (a folder, a name, a
    /// category, a queue, start or not) always stays. Called with the lock held; the media downloads take only the folder, the
    /// category and the queue (<paramref name="fileActions"/> false).
    /// </summary>
    private RuleOutcome ApplyRulesLocked(Uri uri, string? ruleOrigin, string? name, bool explicitName, long? size, string? directory, long? categoryId, long? queueId,
        bool? startNow, RequestContext? context, bool fileActions = true)
    {
        var unchanged = new RuleOutcome(directory, categoryId, queueId, startNow, false, context, null);
        if (ruleOrigin is null || MatchRuleLocked(uri, ruleOrigin, name, size) is not { } rule)
            return unchanged;
        var a = rule.Actions;
        var paused = false;
        var chosenCategory = categoryId ?? (a.CategoryId is { } c && _categories.Any(x => x.Id == c) ? c : null);
        if (string.IsNullOrWhiteSpace(directory) && a.Folder is { Length: > 0 } folder)
            directory = folder;
        var chosenQueue = queueId ?? (a.QueueId is { } q && _queues.Any(x => x.Id == q) ? q : null);
        if (startNow is null && a.Start is { } start)
        {
            switch (start)
            {
                case RuleStart.Now: startNow = true; break;
                case RuleStart.Queued: startNow = false; break;
                case RuleStart.Paused: startNow = false; paused = true; break;
            }
        }
        var applied = new AppliedRule { RuleId = rule.Id, Name = rule.Name };
        if (fileActions)
        {
            if (a.UserAgent is { Length: > 0 } agent && context?.UserAgent is null)
                context = (context ?? new RequestContext(null, null, [])) with { UserAgent = agent };
            // Opening the finished file needs a scan that found nothing, so asking for it asks for the scan.
            // For MCP and the API (programs the user did not type) a rule opens the file only when it names that origin: a rule
            // written for downloads in general must not become "an agent can make Baton open a file".
            var openAllowed = a.OpenWhenDone && (ruleOrigin is not (RuleOrigins.Api or RuleOrigins.Mcp) || rule.Conditions.Origins.Contains(ruleOrigin));
            applied.Scan = a.Scan || openAllowed;
            applied.Connections = a.Connections is { } n ? Math.Clamp(n, 1, 32) : null;
            applied.SpeedLimitKBps = a.SpeedLimitKBps is > 0 ? a.SpeedLimitKBps : null;
            applied.ShowInFolder = a.ShowInFolder;
            applied.OpenWhenDone = openAllowed;
            applied.Rename = !explicitName && a.Rename is { Length: > 0 } pattern ? pattern : null; // an explicit name is kept as it is
        }
        return new RuleOutcome(directory, chosenCategory, chosenQueue, startNow, paused, context, applied);
    }

    /// <summary>The rename waits for the server's answer: the name it reveals is the one the pattern works on.</summary>
    private void ApplyRename(DownloadRecord record)
    {
        if (record.Rule is not { Rename: { Length: > 0 } pattern, RenamedTo: null } rule || record.FileName is not { } serverName)
            return;
        var renamed = RenameFor(pattern, serverName, new Uri(record.Url), record.CategoryId, record.Id, record.Directory);
        record.FileName = renamed;
        rule.RenamedTo = renamed;
    }

    /// <summary>Show in folder / open for a download that has just completed with a rule that asks for it.</summary>
    private void RaiseRuleFileActions(DownloadSnapshot snapshot)
    {
        DownloadRecord? record;
        lock (_lock)
            record = Find(snapshot.Id)?.Clone();
        if (record?.Rule is not { } rule || (!rule.ShowInFolder && !rule.OpenWhenDone) || record.FinalPath is not { } path)
            return;
        var open = false;
        if (rule.OpenWhenDone)
        {
            var reason = ExecutableTypes.IsRunnable(path) ? "programOrScript"
                : record.Check?.Scan != PostDownload.ScanOutcome.Clean ? "noCleanScan"
                : !File.Exists(path) ? "fileMissing" : null;
            if (reason is null)
                open = true;
            else
                AutomationLog.Record(Automation.AutomationLog.RulesOrigin, Automation.ActivityText.RuleOpen, "refused", Automation.ActivityText.Reason(reason, rule.Name), record.Id);
        }
        if (open || rule.ShowInFolder)
            RuleFileActionRequested?.Invoke(new RuleFileRequest(record.Id, path, open, rule.ShowInFolder));
    }
}

/// <summary>What the rules would do with an address: the rule, the folder it gives and the name a rename would give.</summary>
public sealed record RulePreview(Rule? Rule, string? Folder, string? FileName);

public sealed record RuleFileRequest(long Id, string Path, bool Open, bool ShowInFolder);
