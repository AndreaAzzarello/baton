using System.Text.Json.Serialization;
using Baton.Core.Integration;

namespace Baton.Core.Rules;

/// <summary>Where an add came from, as the rules see it (a condition can be limited to some of them).</summary>
public static class RuleOrigins
{
    public const string Browser = "browser";
    public const string Dialog = "dialog";
    public const string Batch = "batch";
    public const string Clipboard = "clipboard";
    public const string Cli = "cli";
    public const string Api = "api";
    public const string Mcp = "mcp";

    public static readonly string[] All = [Browser, Dialog, Batch, Clipboard, Cli, Api, Mcp];

    /// <summary>The rule origin for the origin a protocol message declares (browser, cli, api, mcp, clipboard).</summary>
    public static string FromProtocol(string? origin) => origin switch
    {
        Origins.Cli => Cli,
        Origins.Api => Api,
        Origins.Mcp => Mcp,
        Origins.Clipboard => Clipboard,
        _ => Browser,
    };
}

/// <summary>How a rule starts the download; without it the entry's own choice (usually "start now") stands.</summary>
public enum RuleStart
{
    /// <summary>Start at once.</summary>
    Now,

    /// <summary>Add to the queue, which starts it (at once if it is running, at its schedule otherwise).</summary>
    Queued,

    /// <summary>Add paused: nothing starts until the user does.</summary>
    Paused,
}

/// <summary>All conditions of a rule must hold (AND). A rule without any condition never matches.</summary>
public sealed class RuleConditions
{
    /// <summary>The address's host is one of these domains or a subdomain of one ("example.com" matches "cdn.example.com").</summary>
    public List<string> Domains { get; set; } = [];

    /// <summary>The whole address matches this pattern: "*" for any text, "?" for one character.</summary>
    public string? UrlPattern { get; set; }

    /// <summary>The address matches this regular expression (always run without backtracking).</summary>
    public string? UrlRegex { get; set; }

    /// <summary>The file's extension is one of these (no dot, any case).</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>The category the file would get by its name.</summary>
    public long? CategoryId { get; set; }

    /// <summary>Only when the size is known when the download is added; with an unknown size a rule with a size condition does not match.</summary>
    public long? MinSize { get; set; }

    public long? MaxSize { get; set; }

    /// <summary>Empty: every origin.</summary>
    public List<string> Origins { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Domains.Count == 0 && string.IsNullOrWhiteSpace(UrlPattern) && string.IsNullOrWhiteSpace(UrlRegex) && Extensions.Count == 0
                           && CategoryId is null && MinSize is null && MaxSize is null && Origins.Count == 0;

    public RuleConditions Clone() => new()
    {
        Domains = [.. Domains], UrlPattern = UrlPattern, UrlRegex = UrlRegex, Extensions = [.. Extensions], CategoryId = CategoryId,
        MinSize = MinSize, MaxSize = MaxSize, Origins = [.. Origins],
    };
}

/// <summary>What a matching rule does. Whatever the entry chose explicitly (folder, name, category, queue, start) is kept over it.</summary>
public sealed class RuleActions
{
    public string? Folder { get; set; }
    public long? CategoryId { get; set; }
    public long? QueueId { get; set; }
    public RuleStart? Start { get; set; }

    /// <summary>A name pattern with {name} {ext} {host} {date} {time} {category} {n}; see <see cref="RenameTemplate"/>.</summary>
    public string? Rename { get; set; }

    /// <summary>Scan the finished file with the antivirus, also when scanning is off in the settings.</summary>
    public bool Scan { get; set; }

    public int? Connections { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>KB/s for this download alone (on top of the global limit).</summary>
    public long? SpeedLimitKBps { get; set; }

    /// <summary>Do not ask in the add dialog or the browser's confirmation: add it right away.</summary>
    public bool SkipConfirmation { get; set; }

    public bool ShowInFolder { get; set; }

    /// <summary>Open the file when it is done: never a program or script, and only after a scan that found nothing.</summary>
    public bool OpenWhenDone { get; set; }

    /// <summary>The actions that run something or add without asking: a rule imported with them stays off until the user turns it on.</summary>
    [JsonIgnore]
    public bool IsRisky => OpenWhenDone || SkipConfirmation;

    public RuleActions Clone() => (RuleActions)MemberwiseClone();
}

public sealed class Rule
{
    public long Id { get; set; }
    public int SortOrder { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Came from an imported file, not made here.</summary>
    public bool Imported { get; set; }

    public RuleConditions Conditions { get; set; } = new();
    public RuleActions Actions { get; set; } = new();

    /// <summary>
    /// The stored text of this rule cannot be read by this version (for example it was saved by a newer one and the app went back).
    /// Such a rule never applies, cannot be edited, is saved exactly as stored (only its place may change) and can only be deleted.
    /// </summary>
    [JsonIgnore]
    public bool Unreadable { get; set; }

    public Rule Clone() => new()
    {
        Id = Id, SortOrder = SortOrder, Name = Name, Enabled = Enabled, Imported = Imported, Unreadable = Unreadable,
        Conditions = Conditions.Clone(), Actions = Actions.Clone(),
    };
}

/// <summary>
/// What the rule that matched left with the download: what is still to happen after the add (the rename waits for the server's
/// name, the rest for the transfer and the finished file). Stored as JSON with the download.
/// </summary>
public sealed class AppliedRule
{
    public long RuleId { get; set; }
    public string Name { get; set; } = "";
    public string? Rename { get; set; }

    /// <summary>The name the rename gave: it is applied once, when the server's answer reveals the name.</summary>
    public string? RenamedTo { get; set; }

    public bool Scan { get; set; }
    public int? Connections { get; set; }
    public long? SpeedLimitKBps { get; set; }
    public bool ShowInFolder { get; set; }
    public bool OpenWhenDone { get; set; }

    public AppliedRule Clone() => (AppliedRule)MemberwiseClone();
}
