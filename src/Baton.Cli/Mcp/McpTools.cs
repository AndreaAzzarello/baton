using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Baton.Core.Integration;
using ModelContextProtocol.Protocol;

namespace Baton.Cli.Mcp;

internal enum LinkFailure { None, Unavailable, Timeout, Elevated }

/// <summary>What came back from the app: a reply, or why there is none.</summary>
internal sealed record LinkResult(ChannelReply? Reply, LinkFailure Failure = LinkFailure.None);

/// <summary>The way to the running app. "Unavailable" is no app (and it could not be started) or a connection closed without an answer.</summary>
internal interface IAppLink
{
    Task<LinkResult> SendAsync(ChannelMessage message, TimeSpan replyTimeout, CancellationToken ct);
}

/// <summary>One tool of the contract (docs/MCP.md): what it is called, the level it needs and what it declares about itself.</summary>
internal sealed record ToolDef(string Name, string Title, string Description, string Level, bool ReadOnly, bool Destructive, bool Idempotent,
    bool OpenWorld, string Input, string Output);

/// <summary>
/// The MCP tools. Each one sends one or more protocol messages with origin "mcp"; the app decides what the level allows, so the list
/// shown here is a courtesy and never the protection. Download numbers are always named (at most <see cref="MaxItems"/>), never "all".
/// </summary>
internal sealed class McpTools(IAppLink link, Action<string> log)
{
    public const int MaxItems = 50;
    public const int DefaultPage = 50;
    public const int MaxPage = 200;
    public const int MaxInFlight = 2;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AddBudget = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _inFlight = new(MaxInFlight);

    private static int Rank(string? level) => level switch { "readOnly" => 1, "readAdd" => 2, "extended" => 3, _ => 0 };

    // ------------------------------------------------------------------ the table

    private const string DownloadSchema = """
        {"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"},"url":{"type":"string","description":"scheme, host and file name only"},"status":{"type":"string"},"downloaded":{"type":"integer"},"totalSize":{"type":["integer","null"]},"error":{"type":["string","null"]},"queue":{"type":["string","null"]},"category":{"type":["string","null"]},"addedUtc":{"type":"string"},"completedUtc":{"type":["string","null"]},"path":{"type":["string","null"]}},"required":["id","name","url","status","downloaded"]}
        """;

    private const string IdsInput = """
        {"type":"object","properties":{"ids":{"type":"array","items":{"type":"integer","minimum":1},"minItems":1,"maxItems":50,"uniqueItems":true,"description":"Download numbers as list_downloads shows them. Name each download: there is no 'all'."}},"required":["ids"],"additionalProperties":false}
        """;

    private const string IdsOutput = """
        {"type":"object","properties":{"results":{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"},"status":{"type":"string","enum":["ok","failed"]},"code":{"type":["string","null"]},"message":{"type":["string","null"]}},"required":["id","status"]}},"done":{"type":"integer"},"failed":{"type":"integer"}},"required":["results","done","failed"]}
        """;

    private const string QueueInput = """
        {"type":"object","properties":{"queue":{"type":"string","maxLength":200,"description":"Queue name as list_queues shows it; the main queue when left out."}},"additionalProperties":false}
        """;

    private const string DoneOutput = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""";

    public static readonly IReadOnlyList<ToolDef> All =
    [
        new("list_downloads", "List downloads",
            "Lists the downloads in Baton in order of number, one page at a time. Pass nextCursor back as cursor for the next page.",
            "readOnly", true, false, true, false,
            """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":200,"default":50},"cursor":{"type":"string","description":"nextCursor of the previous page."}},"additionalProperties":false}""",
            $$$"""{"type":"object","properties":{"downloads":{"type":"array","items":{{{DownloadSchema}}}},"nextCursor":{"type":["string","null"]}},"required":["downloads"]}"""),
        new("get_download", "Get one download",
            "Returns the state of one download by its number.",
            "readOnly", true, false, true, false,
            """{"type":"object","properties":{"id":{"type":"integer","minimum":1}},"required":["id"],"additionalProperties":false}""",
            $$$"""{"type":"object","properties":{"download":{{{DownloadSchema}}}},"required":["download"]}"""),
        new("list_queues", "List queues",
            "Lists the names of Baton's download queues, the main one first.",
            "readOnly", true, false, true, false,
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"queues":{"type":"array","items":{"type":"string"}}},"required":["queues"]}"""),
        new("add_downloads", "Add downloads",
            "Adds up to 50 http, https, ftp, ftps or ftpes addresses to Baton. Addresses on this computer or a private network are refused unless the user allowed them. " +
            "A known duplicate is skipped and reported, not added, unless allowDuplicate is true. One result per address.",
            "readAdd", false, false, false, true,
            """{"type":"object","properties":{"urls":{"type":"array","items":{"type":"string","maxLength":2048},"minItems":1,"maxItems":50},"category":{"type":"string","maxLength":200},"queue":{"type":"string","maxLength":200},"start":{"type":"boolean","description":"false adds them paused."},"allowDuplicate":{"type":"boolean"}},"required":["urls"],"additionalProperties":false}""",
            """{"type":"object","properties":{"results":{"type":"array","items":{"type":"object","properties":{"url":{"type":"string"},"status":{"type":"string","enum":["added","duplicate","failed","notAttempted"]},"id":{"type":["integer","null"]},"duplicateReason":{"type":["string","null"]},"existingId":{"type":["integer","null"]},"code":{"type":["string","null"]},"message":{"type":["string","null"]}},"required":["url","status"]}},"added":{"type":"integer"},"duplicates":{"type":"integer"},"failed":{"type":"integer"},"notAttempted":{"type":"integer"}},"required":["results","added","duplicates","failed","notAttempted"]}"""),
        new("probe_url", "Check an address",
            "Asks the server at an address what it would send (file name, type, size, whether it can resume) without downloading it. Makes a request to that address.",
            "readAdd", true, false, true, true,
            """{"type":"object","properties":{"url":{"type":"string","maxLength":2048}},"required":["url"],"additionalProperties":false}""",
            """{"type":"object","properties":{"name":{"type":"string"},"contentType":{"type":["string","null"]},"size":{"type":["integer","null"]},"resumable":{"type":"boolean"}},"required":["name","resumable"]}"""),
        new("pause_downloads", "Pause downloads", "Pauses the named downloads (up to 50).", "extended", false, false, true, false, IdsInput, IdsOutput),
        new("resume_downloads", "Resume downloads", "Starts the named paused or failed downloads again (up to 50), through their queues.", "extended", false, false, true, false, IdsInput, IdsOutput),
        new("retry_downloads", "Retry failed downloads", "Starts the named failed downloads again (up to 50).", "extended", false, false, false, false, IdsInput, IdsOutput),
        new("remove_from_list", "Remove from the list",
            "Removes the named downloads from Baton's list (up to 50). The files stay on the disk. An unfinished download is refused.",
            "extended", false, true, true, false, IdsInput, IdsOutput),
        new("start_queue", "Start a queue", "Starts a queue.", "extended", false, false, true, false, QueueInput, DoneOutput),
        new("stop_queue", "Stop a queue", "Stops a queue and pauses what it was downloading.", "extended", false, false, true, false, QueueInput, DoneOutput),
    ];

    private static Tool ToTool(ToolDef d) => new()
    {
        Name = d.Name,
        Title = d.Title,
        Description = d.Description,
        InputSchema = JsonDocument.Parse(d.Input).RootElement.Clone(),
        OutputSchema = JsonDocument.Parse(d.Output).RootElement.Clone(),
        Annotations = new ToolAnnotations
        {
            Title = d.Title, ReadOnlyHint = d.ReadOnly, DestructiveHint = d.Destructive, IdempotentHint = d.Idempotent, OpenWorldHint = d.OpenWorld,
        },
    };

    // ------------------------------------------------------------------ tools/list

    /// <summary>
    /// The tools the app's level for MCP allows. No answer from the app (it cannot be started, or it is busy) gives the whole list: a call then
    /// says why it fails, and a list cut short would stay cut for the whole session. An app that does not know protocol 5, or level Off, gives none.
    /// </summary>
    public async ValueTask<ListToolsResult> ListAsync(CancellationToken ct)
    {
        var result = await link.SendAsync(new ChannelMessage(Messages.Capabilities, Origin: Origins.Mcp), CallTimeout, ct);
        if (result.Failure != LinkFailure.None)
        {
            log($"capabilities: {result.Failure}; offering every tool, calls will say why they fail");
            return new ListToolsResult { Tools = All.Select(ToTool).ToList() };
        }
        var reply = result.Reply!;
        if (!reply.Ok || reply.Protocol is null or < 5 || reply.Level is null)
        {
            log(reply.Protocol is null or < 5 ? "capabilities: this Baton is older than 1.4.2; no tools" : $"capabilities: refused ({reply.Code}); no tools");
            return new ListToolsResult { Tools = [] };
        }
        var rank = Rank(reply.Level);
        log($"capabilities: level {reply.Level}");
        return new ListToolsResult { Tools = All.Where(d => Rank(d.Level) <= rank).Select(ToTool).ToList() };
    }

    // ------------------------------------------------------------------ tools/call

    public async ValueTask<CallToolResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        var def = All.FirstOrDefault(d => d.Name == name);
        if (def is null)
            return Fail("unknownTool", "There is no tool with this name.");
        var args = new Args(arguments);
        // At most two calls in flight: the app's pipe has few places and the browser's extension shares them.
        await _inFlight.WaitAsync(ct);
        try
        {
            var result = name switch
            {
                "list_downloads" => await ListDownloadsAsync(args, ct),
                "get_download" => await GetDownloadAsync(args, ct),
                "list_queues" => await ListQueuesAsync(args, ct),
                "add_downloads" => await AddAsync(args, ct),
                "probe_url" => await ProbeAsync(args, ct),
                "pause_downloads" => await ByIdsAsync(args, Messages.Pause, ct),
                "resume_downloads" => await ByIdsAsync(args, Messages.Resume, ct),
                "retry_downloads" => await ByIdsAsync(args, Messages.Retry, ct),
                "remove_from_list" => await ByIdsAsync(args, Messages.Remove, ct),
                "start_queue" => await QueueAsync(args, Messages.QueueStart, ct),
                "stop_queue" => await QueueAsync(args, Messages.QueueStop, ct),
                _ => Fail("unknownTool", "There is no tool with this name."),
            };
            log($"{name}: {(result.IsError == true ? "error" : "ok")}");
            return result;
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task<CallToolResult> ListDownloadsAsync(Args args, CancellationToken ct)
    {
        if (args.Check(["limit", "cursor"]) is { } bad)
            return bad;
        var limit = args.OptionalInt("limit", DefaultPage, 1, MaxPage);
        long? after = null;
        if (args.OptionalString("cursor", 20) is { Length: > 0 } cursor)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                return Fail("invalidArguments", "cursor must be the nextCursor of a previous page.");
            after = parsed;
        }
        if (args.Problem is { } problem)
            return Fail("invalidArguments", problem);
        var (reply, error) = await ExchangeAsync(new ChannelMessage(Messages.List, Origin: Origins.Mcp, Limit: limit, After: after), CallTimeout, ct);
        if (error is not null)
            return error;
        return Ok(new ListResult(reply!.Downloads?.Select(View).ToList() ?? [], reply.NextCursor?.ToString(CultureInfo.InvariantCulture)), McpJsonContext.Default.ListResult);
    }

    private async Task<CallToolResult> GetDownloadAsync(Args args, CancellationToken ct)
    {
        if (args.Check(["id"]) is { } bad)
            return bad;
        var id = args.RequiredId("id");
        if (args.Problem is { } problem)
            return Fail("invalidArguments", problem);
        var (reply, error) = await ExchangeAsync(new ChannelMessage(Messages.Status, Origin: Origins.Mcp, Id: id), CallTimeout, ct);
        if (error is not null)
            return error;
        return reply!.Download is { } d ? Ok(new GetResult(View(d)), McpJsonContext.Default.GetResult) : Fail("failed", "Baton sent no download.");
    }

    private async Task<CallToolResult> ListQueuesAsync(Args args, CancellationToken ct)
    {
        if (args.Check([]) is { } bad)
            return bad;
        var (reply, error) = await ExchangeAsync(new ChannelMessage(Messages.Queues, Origin: Origins.Mcp), CallTimeout, ct);
        if (error is not null)
            return error;
        return Ok(new QueuesResult(reply!.Queues?.Select(q => AgentText.Clean(q, AgentText.MaxName)).ToList() ?? []), McpJsonContext.Default.QueuesResult);
    }

    private async Task<CallToolResult> ProbeAsync(Args args, CancellationToken ct)
    {
        if (args.Check(["url"]) is { } bad)
            return bad;
        var url = args.RequiredString("url", AgentText.MaxUrl);
        if (args.Problem is { } problem)
            return Fail("invalidArguments", problem);
        var (reply, error) = await ExchangeAsync(new ChannelMessage(Messages.Probe, Origin: Origins.Mcp, Url: url), ProbeTimeout, ct);
        if (error is not null)
            return error;
        return reply!.Probe is { } p
            ? Ok(new ProbeResult(AgentText.Clean(p.Name), p.ContentType is null ? null : AgentText.Clean(p.ContentType, 200), p.Size, p.Resumable), McpJsonContext.Default.ProbeResult)
            : Fail("failed", "Baton sent no answer for this address.");
    }

    private async Task<CallToolResult> AddAsync(Args args, CancellationToken ct)
    {
        if (args.Check(["urls", "category", "queue", "start", "allowDuplicate"]) is { } bad)
            return bad;
        var urls = args.StringArray("urls", MaxItems, AgentText.MaxUrl);
        var category = args.OptionalString("category", 200);
        var queue = args.OptionalString("queue", 200);
        var start = args.OptionalBool("start");
        var allowDuplicate = args.OptionalBool("allowDuplicate");
        if (args.Problem is { } problem)
            return Fail("invalidArguments", problem);

        var results = new List<AddItem>();
        var started = DateTime.UtcNow;
        var stop = false;
        foreach (var url in urls!)
        {
            var shown = AgentText.Address(url);
            var remaining = AddBudget - (DateTime.UtcNow - started);
            if (stop || remaining <= TimeSpan.Zero || ct.IsCancellationRequested)
            {
                results.Add(new AddItem(shown, "notAttempted", null, null, null, null, null));
                continue;
            }
            var download = new BrowserDownload(url, Category: category, Queue: queue, Start: start, AllowDuplicate: allowDuplicate);
            var (reply, error) = await ExchangeAsync(new ChannelMessage(Messages.Add, Origin: Origins.Mcp, Download: download),
                remaining < CallTimeout ? remaining : CallTimeout, ct);
            if (error is not null)
            {
                var (code, message) = ErrorOf(error);
                results.Add(new AddItem(shown, "failed", null, null, null, code, message));
                // No answer means the rest would wait as long; a spent budget of requests means the same.
                stop = code is "timeout" or "appUnavailable" or "appElevated" or "tooManyRequests" or "appTooOld";
                continue;
            }
            if (reply!.Result == "duplicate")
                results.Add(new AddItem(shown, "duplicate", null, reply.Duplicate?.Reason, reply.Duplicate?.ExistingId, null, null));
            else
                results.Add(new AddItem(shown, "added", reply.Id, null, null, null, null));
        }
        return Ok(new AddResult(results, results.Count(r => r.Status == "added"), results.Count(r => r.Status == "duplicate"),
            results.Count(r => r.Status == "failed"), results.Count(r => r.Status == "notAttempted")), McpJsonContext.Default.AddResult);
    }

    private async Task<CallToolResult> ByIdsAsync(Args args, string type, CancellationToken ct)
    {
        if (args.Check(["ids"]) is { } bad)
            return bad;
        var ids = args.IdArray("ids", MaxItems);
        if (args.Problem is { } problem)
            return Fail("invalidArguments", problem);
        var results = new List<IdItem>();
        var stop = false;
        foreach (var id in ids!)
        {
            if (stop)
            {
                results.Add(new IdItem(id, "failed", "notAttempted", "Not tried: an earlier call failed in a way that would repeat."));
                continue;
            }
            var (_, error) = await ExchangeAsync(new ChannelMessage(type, Origin: Origins.Mcp, Id: id), CallTimeout, ct);
            if (error is null)
            {
                results.Add(new IdItem(id, "ok", null, null));
                continue;
            }
            var (code, message) = ErrorOf(error);
            results.Add(new IdItem(id, "failed", code, message));
            stop = code is "timeout" or "appUnavailable" or "appElevated" or "tooManyRequests" or "appTooOld" or "automationOff" or "levelTooLow";
        }
        return Ok(new IdsResult(results, results.Count(r => r.Status == "ok"), results.Count(r => r.Status == "failed")), McpJsonContext.Default.IdsResult);
    }

    private async Task<CallToolResult> QueueAsync(Args args, string type, CancellationToken ct)
    {
        if (args.Check(["queue"]) is { } bad)
            return bad;
        var queue = args.OptionalString("queue", 200);
        if (args.Problem is { } problem)
            return Fail("invalidArguments", problem);
        var (_, error) = await ExchangeAsync(new ChannelMessage(type, Origin: Origins.Mcp, Queue: queue), CallTimeout, ct);
        return error ?? Ok(new DoneResult(true), McpJsonContext.Default.DoneResult);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One message to the app: the reply, or the tool error that says why there is none.</summary>
    private async Task<(ChannelReply? Reply, CallToolResult? Error)> ExchangeAsync(ChannelMessage message, TimeSpan timeout, CancellationToken ct)
    {
        var result = await link.SendAsync(message, timeout, ct);
        switch (result.Failure)
        {
            case LinkFailure.Unavailable:
                return (null, Fail("appUnavailable", "Baton is not running and could not be started, or it closed the connection without answering. " +
                                                     "A change may have been made: check with list_downloads before trying again."));
            case LinkFailure.Elevated:
                return (null, Fail("appElevated", "Baton is running as administrator, so a normal program cannot reach it (and MCP is off while it does). " +
                                                  "Start Baton normally, or run batonctl from an administrator terminal."));
            case LinkFailure.Timeout:
                return (null, Fail("timeout", "Baton did not answer in time. It is busy; a change may have been made: check with list_downloads before trying again."));
        }
        var reply = result.Reply!;
        if (reply.Ok)
            return (reply, null);
        if (reply.Code is null && reply.Protocol is null or < 5)
            return (null, Fail("appTooOld", "This Baton is older than 1.4.2 and does not speak to MCP: update it."));
        var code = reply.Code is { } c ? JsonNamingPolicy.CamelCase.ConvertName(c.ToString()) : "failed";
        return (null, Fail(code, AgentText.Clean(reply.Error, AgentText.MaxMessage)));
    }

    private static (string Code, string Message) ErrorOf(CallToolResult error)
    {
        if (error.StructuredContent is { } content && content.TryGetProperty("error", out var e))
            return (e.GetProperty("code").GetString() ?? "failed", e.GetProperty("message").GetString() ?? "");
        return ("failed", "");
    }

    private static DownloadView View(DownloadInfo d) => new(d.Id, AgentText.Clean(d.Name), AgentText.Address(d.Url), AgentText.Clean(d.Status, 40), d.Downloaded,
        d.TotalSize, d.Error is null ? null : AgentText.Clean(d.Error, AgentText.MaxMessage), d.Queue is null ? null : AgentText.Clean(d.Queue),
        d.Category is null ? null : AgentText.Clean(d.Category), d.AddedUtc, d.CompletedUtc, d.Path is null ? null : AgentText.Clean(d.Path, 520));

    private static CallToolResult Ok<T>(T value, JsonTypeInfo<T> info)
    {
        var element = JsonSerializer.SerializeToElement(value, info);
        return new CallToolResult { StructuredContent = element, Content = [new TextContentBlock { Text = element.GetRawText() }] };
    }

    /// <summary>An error as data: a stable code and a short message, as structured content and as the same JSON text.</summary>
    internal static CallToolResult Fail(string code, string message)
    {
        var element = JsonSerializer.SerializeToElement(new ErrorResult(new ToolError(code, message)), McpJsonContext.Default.ErrorResult);
        return new CallToolResult { IsError = true, StructuredContent = element, Content = [new TextContentBlock { Text = element.GetRawText() }] };
    }

    /// <summary>The arguments of a call, read with limits; the first problem found is kept.</summary>
    private sealed class Args(IDictionary<string, JsonElement>? values)
    {
        private readonly IDictionary<string, JsonElement> _values = values ?? new Dictionary<string, JsonElement>();

        public string? Problem { get; private set; }

        /// <summary>An unknown property is refused (this is how "all" is refused): null when there is none.</summary>
        public CallToolResult? Check(string[] allowed)
        {
            var unknown = _values.Keys.FirstOrDefault(k => !allowed.Contains(k));
            return unknown is null ? null : Fail("invalidArguments", $"The argument \"{AgentText.Clean(unknown, 40)}\" does not exist for this tool.");
        }

        private bool Has(string name, out JsonElement e) => _values.TryGetValue(name, out e) && e.ValueKind != JsonValueKind.Null;

        public int OptionalInt(string name, int fallback, int min, int max)
        {
            if (!Has(name, out var e))
                return fallback;
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var v) || v < min || v > max)
            {
                Problem ??= $"{name} must be a whole number from {min} to {max}.";
                return fallback;
            }
            return v;
        }

        public long RequiredId(string name)
        {
            if (Has(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) && v > 0)
                return v;
            Problem ??= $"{name} must be a download number (1 or more).";
            return 0;
        }

        public string? OptionalString(string name, int maxLength)
        {
            if (!Has(name, out var e))
                return null;
            if (e.ValueKind != JsonValueKind.String || e.GetString() is not { } s || s.Length > maxLength)
            {
                Problem ??= $"{name} must be text of at most {maxLength} characters.";
                return null;
            }
            return s;
        }

        public string? RequiredString(string name, int maxLength)
        {
            var s = OptionalString(name, maxLength);
            if (string.IsNullOrWhiteSpace(s))
                Problem ??= $"{name} is needed.";
            return s;
        }

        public bool? OptionalBool(string name)
        {
            if (!Has(name, out var e))
                return null;
            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return e.GetBoolean();
            Problem ??= $"{name} must be true or false.";
            return null;
        }

        public List<string>? StringArray(string name, int maxItems, int maxLength)
        {
            if (!Has(name, out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() is 0 || e.GetArrayLength() > maxItems)
            {
                Problem ??= $"{name} must be a list of 1 to {maxItems} texts.";
                return null;
            }
            var list = new List<string>();
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } s || s.Length > maxLength)
                {
                    Problem ??= $"Every item of {name} must be text of at most {maxLength} characters.";
                    return null;
                }
                list.Add(s);
            }
            return list;
        }

        public List<long>? IdArray(string name, int maxItems)
        {
            if (!Has(name, out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() is 0 || e.GetArrayLength() > maxItems)
            {
                Problem ??= $"{name} must be a list of 1 to {maxItems} download numbers; name each download, there is no \"all\".";
                return null;
            }
            var list = new List<long>();
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var id) || id <= 0)
                {
                    Problem ??= $"Every item of {name} must be a download number (1 or more).";
                    return null;
                }
                if (!list.Contains(id))
                    list.Add(id);
            }
            return list;
        }
    }
}
