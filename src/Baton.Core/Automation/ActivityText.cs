using System.Globalization;
using Baton.Core.Integration;
using Baton.Core.Resources;

namespace Baton.Core.Automation;

/// <summary>
/// The stable codes the activity log stores and the words that show them. The database keeps only codes (an action such as
/// "api.add", an outcome, and a detail that starts with "!reason" followed by free text such as an address or a rule name);
/// the words come from <see cref="Strings"/> when a line is shown, in the language of the interface.
/// </summary>
public static class ActivityText
{
    public const string RuleApplied = "rule.applied";
    public const string RuleOpen = "rule.open";
    public const string DuplicateSkip = "duplicate.skip";
    public const string ApiRequest = "api.request";

    /// <summary>Every code the log can store, so a test can check that each has words in both languages.</summary>
    public static readonly string[] AllActions =
    [
        RuleApplied, RuleOpen, DuplicateSkip, ApiRequest, "api.version", "api.openapi", "api.queues", "api.queueStart", "api.queueStop",
        "api.add", "api.list", "api.remove", "api.status", "api.pause", "api.resume", "api.retry",
        "mcp.request", "mcp.queues", "mcp.queueStart", "mcp.queueStop", "mcp.add", "mcp.list", "mcp.remove", "mcp.status", "mcp.pause", "mcp.resume",
        "mcp.retry", "mcp.probe",
    ];

    /// <summary>The action code of an MCP call from its message type ("mcp.add"); one a tool never sends is the generic "mcp.request".</summary>
    public static string McpAction(string type) => type switch
    {
        Messages.Queues => "mcp.queues",
        Messages.QueueStart => "mcp.queueStart",
        Messages.QueueStop => "mcp.queueStop",
        Messages.Add => "mcp.add",
        Messages.List => "mcp.list",
        Messages.Remove => "mcp.remove",
        Messages.Status => "mcp.status",
        Messages.Pause => "mcp.pause",
        Messages.Resume => "mcp.resume",
        Messages.Retry => "mcp.retry",
        Messages.Probe => "mcp.probe",
        _ => "mcp.request",
    };

    public static readonly string[] AllReasons =
    [
        "notLoopback", "badHost", "webPage", "noToken", "wrongToken", "rateLimit", "bodyTooLarge", "invalid", "level", "noDownload", "noQueue",
        "privateNetwork", "cannotRestart", "noFile", "cliOnly", "badScheme", "folderRefused", "noCategory", "unknownType", "failed", "badBody",
        "missingUrl", "duplicate", "programOrScript", "noCleanScan", "fileMissing", "sameUrl", "sameName", "sameFile",
    ];

    public static readonly string[] AllOutcomes = ["ok", "refused", "skipped", "200", "201", "400", "401", "403", "404", "409", "413", "429", "500"];

    /// <summary>The action code of an API call from its method and path (no ids, no query).</summary>
    public static string ApiAction(string method, string path)
    {
        var parts = path.Trim('/').Split('/');
        // parts: api, v1, then the route.
        var route = parts.Length > 2 ? parts[2..] : [];
        var m = method.ToUpperInvariant();
        return route switch
        {
            ["version"] => "api.version",
            ["openapi.json"] => "api.openapi",
            ["queues"] => "api.queues",
            ["queues", _, "start"] => "api.queueStart",
            ["queues", _, "stop"] => "api.queueStop",
            ["downloads"] => m == "POST" ? "api.add" : "api.list",
            ["downloads", _] => m == "DELETE" ? "api.remove" : "api.status",
            ["downloads", _, "pause"] => "api.pause",
            ["downloads", _, "resume"] => "api.resume",
            ["downloads", _, "retry"] => "api.retry",
            _ => ApiRequest,
        };
    }

    /// <summary>The reason code the activity log keeps for the <see cref="ReplyCode"/> of a failure.</summary>
    public static string ReasonOf(ReplyCode? code) => code switch
    {
        ReplyCode.NoDownload => "noDownload",
        ReplyCode.NoQueue => "noQueue",
        ReplyCode.PrivateNetwork => "privateNetwork",
        ReplyCode.AutomationOff or ReplyCode.LevelTooLow => "level",
        ReplyCode.TooManyRequests => "rateLimit",
        ReplyCode.Invalid => "invalid",
        ReplyCode.CannotRestart => "cannotRestart",
        ReplyCode.NoFile => "noFile",
        ReplyCode.CliOnly => "cliOnly",
        ReplyCode.BadScheme => "badScheme",
        ReplyCode.Unresolvable => "unresolvable",
        ReplyCode.FolderRefused => "folderRefused",
        ReplyCode.NoCategory => "noCategory",
        ReplyCode.UnknownType => "unknownType",
        _ => "failed",
    };

    /// <summary>A detail made of a reason code and, optionally, free text (an address, a rule name).</summary>
    public static string Reason(string code, string? text = null) => string.IsNullOrEmpty(text) ? "!" + code : "!" + code + " " + text;

    public static string Action(string code, CultureInfo? culture = null) => Lookup("ActivityAction_" + code.Replace('.', '_'), culture) ?? code;

    public static string Outcome(string code, CultureInfo? culture = null)
    {
        var known = Lookup("ActivityOutcome_" + code, culture);
        if (known is not null)
            return known;
        // An HTTP status the API answered: "201" -> "Added (201)".
        return int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            ? string.Format(culture ?? Strings.Culture ?? CultureInfo.CurrentUICulture, Strings.ActivityOutcomeStatus, StatusWord(status, culture), status)
            : code;
    }

    public static string Detail(string detail, CultureInfo? culture = null)
    {
        if (!detail.StartsWith('!'))
            return detail;
        var space = detail.IndexOf(' ');
        var code = space < 0 ? detail[1..] : detail[1..space];
        var text = space < 0 ? "" : detail[(space + 1)..];
        var word = Lookup("ActivityReason_" + code, culture) ?? code;
        return text.Length == 0 ? word : word + ": " + text;
    }

    private static string StatusWord(int status, CultureInfo? culture) =>
        Lookup("ActivityStatus_" + status, culture) ?? Lookup(status < 300 ? "ActivityStatus_ok" : status < 500 ? "ActivityStatus_refused" : "ActivityStatus_error", culture) ?? "";

    private static string? Lookup(string key, CultureInfo? culture) =>
        Strings.ResourceManager.GetString(key, culture ?? Strings.Culture ?? CultureInfo.CurrentUICulture);
}
