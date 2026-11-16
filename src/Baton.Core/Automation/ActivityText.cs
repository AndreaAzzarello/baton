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
    ];

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

    /// <summary>The stable reason code of a refusal text from the control handler.</summary>
    public static string ReasonOf(string? error) => error switch
    {
        ControlHandler.NoDownloadError => "noDownload",
        ControlHandler.NoQueueError => "noQueue",
        ControlHandler.PrivateNetworkError => "privateNetwork",
        null => "failed",
        _ when error.StartsWith("The level", StringComparison.Ordinal) || error.StartsWith("Automation is turned off", StringComparison.Ordinal) => "level",
        _ when error.StartsWith("This download cannot be started", StringComparison.Ordinal) => "cannotRestart",
        _ when error.StartsWith("The file is not there", StringComparison.Ordinal) => "noFile",
        _ when error.StartsWith("Only the command line may open", StringComparison.Ordinal) => "cliOnly",
        _ when error.StartsWith("Only http, https and ftp", StringComparison.Ordinal) => "badScheme",
        _ when error.StartsWith("Only the command line may choose", StringComparison.Ordinal) => "folderRefused",
        _ when error.StartsWith("No category", StringComparison.Ordinal) => "noCategory",
        _ when error.StartsWith("Unknown message", StringComparison.Ordinal) => "unknownType",
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
