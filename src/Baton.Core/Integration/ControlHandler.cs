using Baton.Core.Duplicates;

namespace Baton.Core.Integration;

/// <summary>
/// The control messages of protocol 4 (list, status, pause, resume, retry, remove, open, queue start/stop, version and "add" from
/// an origin that cannot show a dialog). The answers carry <see cref="DownloadInfo"/>, never the secrets of a download.
/// </summary>
public sealed class ControlHandler(DownloadService service, Func<AutomationLevel> mcpLevel, Func<AutomationLevel> apiLevel,
    Action<string> openFile, string appVersion, Func<DateTime>? clock = null, int maxPending = ControlHandler.MaxPendingForAgents)
{
    /// <summary>The most requests MCP may send in a minute (every message counts; asking for the level does not).</summary>
    public const int McpRequestsPerMinute = 120;
    /// <summary>The most downloads waiting (queued or paused, not finished) that agents may leave in the list; more are refused.</summary>
    public const int MaxPendingForAgents = 1000;
    public const string TooManyPendingError = "Too many downloads are still waiting: let some finish or remove them, then add more.";
    public const string TooManyRequestsError = "Too many requests: wait a moment and try again.";

    private readonly RateWindow _mcpRate = new(McpRequestsPerMinute, clock);

    /// <summary>Null when the message may go on; otherwise the refusal to send back.</summary>
    public ChannelReply? Authorize(ChannelMessage message)
    {
        if (OriginPolicy.Refusal(message, mcpLevel(), apiLevel()) is { } refusal)
            return Refused(message, Fail(refusal.Code, refusal.Text));
        // MCP is an agent that can loop: its budget is kept here, in the app, whatever the client does.
        if (message.Origin == Origins.Mcp && message.Type != Messages.Capabilities && !_mcpRate.TryTake())
            return Refused(message, Fail(ReplyCode.TooManyRequests, TooManyRequestsError));
        return null;
    }

    private DateTime _lastRateLine = DateTime.MinValue;

    /// <summary>An MCP refusal goes to the activity log (one line per minute for the rate limit, so a loop cannot fill the log).</summary>
    private ChannelReply Refused(ChannelMessage message, ChannelReply reply)
    {
        if (message.Origin != Origins.Mcp || message.Type == Messages.Capabilities)
            return reply;
        if (reply.Code == ReplyCode.TooManyRequests)
        {
            var now = (clock ?? (() => DateTime.UtcNow))();
            if (now - _lastRateLine < TimeSpan.FromMinutes(1))
                return reply;
            _lastRateLine = now;
        }
        LogMcp(message, "refused", Automation.ActivityText.Reason(Automation.ActivityText.ReasonOf(reply.Code)));
        return reply;
    }

    private void LogMcp(ChannelMessage message, string outcome, string reason)
    {
        var what = message.Download is { } d ? Automation.AutomationLog.Clean(d.Url) : message.Url is { } u ? Automation.AutomationLog.Clean(u)
            : message.Id is { } id ? "#" + id : message.Queue ?? "";
        service.AutomationLog.Record(Automation.AutomationLog.Mcp, Automation.ActivityText.McpAction(message.Type), outcome,
            reason.Length > 0 && what.Length > 0 ? reason + " " + what : reason.Length > 0 ? reason : what, message.Id);
    }

    /// <summary>True for the types this handler answers (everything but the browser's own).</summary>
    public static bool Handles(ChannelMessage message) =>
        message.Type is Messages.List or Messages.Status or Messages.Pause or Messages.Resume or Messages.Retry or Messages.Remove
            or Messages.Open or Messages.QueueStart or Messages.QueueStop or Messages.Version or Messages.Probe or Messages.Capabilities
        || (message.Type == Messages.Add && (message.Origin ?? Origins.Browser) != Origins.Browser);

    /// <summary>
    /// Answers one message. A failure inside (a file type with no program, a database error) is an error reply: a connection closed
    /// without an answer would look like "Baton is not running" to the command line, which would start it and send the message again.
    /// </summary>
    public async Task<ChannelReply> HandleAsync(ChannelMessage message)
    {
        ChannelReply reply;
        try
        {
            reply = await HandleCoreAsync(message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            reply = Fail(ReplyCode.Failed, "Baton could not do that: " + Log.Redact(ex.Message));
        }
        // What MCP did is in the activity log, whether it worked or not (asking for the level is not an action).
        if (message.Origin == Origins.Mcp && message.Type != Messages.Capabilities)
        {
            if (!reply.Ok)
                LogMcp(message, "refused", Automation.ActivityText.Reason(Automation.ActivityText.ReasonOf(reply.Code)));
            else if (reply.Result == "duplicate")
                LogMcp(message, "skipped", Automation.ActivityText.Reason("duplicate"));
            else
                LogMcp(message, "ok", "");
        }
        return reply;
    }

    private async Task<ChannelReply> HandleCoreAsync(ChannelMessage message)
    {
        DownloadSnapshot found;
        ChannelReply error;
        switch (message.Type)
        {
            case Messages.Version:
                return Ok(version: appVersion);
            case Messages.Capabilities:
                return new ChannelReply(true, Protocol: Messages.ProtocolVersion, AppVersion: appVersion, Level: LevelName(message.Origin));
            case Messages.Probe:
                return await ProbeAsync(message);
            case Messages.List:
                return ListPage(message);
            case Messages.Status:
                return Find(message, out found, out error) ? Ok(download: Info(found)) : error;
            case Messages.Add:
                return await AddAsync(message);
            case Messages.Pause:
                if (message.All == true)
                {
                    await service.PauseAllAsync();
                    return Ok();
                }
                if (!Find(message, out found, out error))
                    return error;
                await service.PauseAsync(found.Id);
                return Ok(download: Info(service.GetSnapshot(found.Id)!));
            case Messages.Resume:
            case Messages.Retry:
                if (message.All == true)
                {
                    // In order, through the queues: hundreds of downloads do not all start at once.
                    var wanted = service.GetSnapshots()
                        .Where(s => message.Type == Messages.Retry ? s.Status == DownloadStatus.Failed : s.Status is DownloadStatus.Paused or DownloadStatus.Failed)
                        .ToList();
                    foreach (var s in wanted)
                        service.Enqueue(s.Id);
                    return Ok(count: wanted.Count);
                }
                if (!Find(message, out found, out error))
                    return error;
                if (found.Status == DownloadStatus.Completed || (message.Type == Messages.Retry && found.Status != DownloadStatus.Failed))
                    return Fail(ReplyCode.CannotRestart, "This download cannot be started again.");
                service.Start(found.Id);
                return Ok(download: Info(service.GetSnapshot(found.Id)!));
            case Messages.Remove:
                // From the list only: the files stay on the disk, whatever the origin.
                if (message.All == true)
                {
                    var (removed, _) = await service.ClearCompletedAsync(recycleFiles: false);
                    return Ok(count: removed);
                }
                if (!Find(message, out found, out error))
                    return error;
                // Removing an unfinished download also deletes its partial file and segments: that is the user's decision, not an agent's.
                if (message.Origin is Origins.Api or Origins.Mcp && found.Status != DownloadStatus.Completed)
                    return Fail(ReplyCode.NotFinished, "Only finished downloads can be removed from the list by an agent; pause or leave the others.");
                await service.RemoveAsync(found.Id, deleteFile: false);
                return Ok(id: found.Id);
            case Messages.Open:
                if (!Find(message, out found, out error))
                    return error;
                if (found.Status != DownloadStatus.Completed || found.FinalPath is not { } path || !File.Exists(path))
                    return Fail(ReplyCode.NoFile, "The file is not there.");
                // The command line is the user's own terminal; MCP and the API could chain "add" and "open" into running a download.
                if (message.Origin != Origins.Cli && Rules.ExecutableTypes.IsRunnable(path))
                    return Fail(ReplyCode.CliOnly, "Only the command line may open a program or script.");
                openFile(path);
                return Ok(id: found.Id);
            case Messages.QueueStart:
            case Messages.QueueStop:
                var wantedName = message.Queue?.Trim();
                var queue = string.IsNullOrEmpty(wantedName)
                    ? service.Queues.FirstOrDefault(q => q.Key == DownloadQueue.MainKey)
                    : service.Queues.FirstOrDefault(q => string.Equals(q.Name, wantedName, StringComparison.CurrentCultureIgnoreCase)
                                                         || string.Equals(q.DisplayName, wantedName, StringComparison.CurrentCultureIgnoreCase));
                if (queue is null)
                    return Fail(ReplyCode.NoQueue, NoQueueError);
                if (message.Type == Messages.QueueStart)
                    service.StartQueue(queue.Id, byAgent: message.Origin is Origins.Api or Origins.Mcp);
                else
                    await service.StopQueueAsync(queue.Id);
                return Ok();
            default:
                return Fail(ReplyCode.UnknownType, "Unknown message type.");
        }
    }

    /// <summary>The level of the caller's origin as the protocol names it; the command line has no level and may do everything.</summary>
    private string LevelName(string? origin) => (origin switch { Origins.Mcp => mcpLevel(), Origins.Api => apiLevel(), _ => AutomationLevel.Extended }) switch
    {
        AutomationLevel.Off => "off",
        AutomationLevel.ReadOnly => "readOnly",
        AutomationLevel.ReadAdd => "readAdd",
        _ => "extended",
    };

    public const int DefaultPageSize = 50;

    /// <summary>Everything, as before, or with a limit or a cursor one page in order of number; "nextCursor" is the "after" of the next page.</summary>
    private ChannelReply ListPage(ChannelMessage message)
    {
        var snapshots = service.GetSnapshots();
        if (message.Limit is null && message.After is null)
            return Ok(downloads: snapshots.Select(Info).ToList());
        var limit = message.Limit ?? DefaultPageSize;
        var page = snapshots.Where(s => s.Id > (message.After ?? 0)).OrderBy(s => s.Id).Take(limit + 1).ToList();
        var more = page.Count > limit;
        if (more)
            page.RemoveAt(limit);
        return new ChannelReply(true, Protocol: Messages.ProtocolVersion, Downloads: page.Select(Info).ToList(), NextCursor: more ? page[^1].Id : null);
    }

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);

    private async Task<ChannelReply> ProbeAsync(ChannelMessage message)
    {
        var (context, refusal) = await GuardAgentAddressAsync(message.Origin, message.Url!, new RequestContext(null, null, []));
        if (refusal is not null)
            return refusal;
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            var found = await service.Network.ProbeAsync(new DownloadRecord { Url = message.Url!, Context = context }, timeout.Token);
            return new ChannelReply(true, Protocol: Messages.ProtocolVersion,
                Probe: new ProbeInfo(found.FileName, found.ContentType, found.TotalSize, found.SupportsRanges));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(ReplyCode.ProbeFailed, "The address did not answer: " + Log.Redact(ex is OperationCanceledException ? "no answer in time." : ex.Message));
        }
    }

    /// <summary>
    /// For MCP and the API (programs the user did not type): only http, https, ftp, ftps and ftpes, and nothing on this computer or the local
    /// network unless the user allowed it; the context then carries the flag that checks every connection and redirect again.
    /// Shared by "add" and "probe". The command line and the browser are not restricted.
    /// </summary>
    private async Task<(RequestContext Context, ChannelReply? Refusal)> GuardAgentAddressAsync(string? origin, string url, RequestContext context)
    {
        if (origin is not (Origins.Api or Origins.Mcp))
            return (context, null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https" or "ftp" or "ftps" or "ftpes"))
            return (context, Fail(ReplyCode.BadScheme, "Only http, https, ftp, ftps and ftpes addresses can be downloaded."));
        if (service.Settings.AllowLocalNetworkForAgents)
            return (context, null);
        switch (await Net.UrlGuard.ClassifyHostAsync(target.IdnHost, Net.UrlGuard.AgentResolveTimeout))
        {
            case Net.HostKind.Private:
                return (context, Fail(ReplyCode.PrivateNetwork, PrivateNetworkError));
            case Net.HostKind.Unresolvable:   // not shown to be public: refused, never guessed at (a name that only a proxy could resolve included)
                return (context, Fail(ReplyCode.Unresolvable, UnresolvableError));
        }
        return (context with { PublicOnly = true }, null);
    }

    /// <summary>Refusal texts the local API turns into status codes.</summary>
    public const string PrivateNetworkError = "This address is on this computer or a private network; turn on \"Allow addresses of the local network\" in the settings to allow it.";
    public const string UnresolvableError = "The name of this address could not be resolved in time, so it cannot be shown to be public.";
    public const string NoDownloadError = "No download with this number.";
    public const string NoQueueError = "No queue with this name.";

    private async Task<ChannelReply> AddAsync(ChannelMessage message)
    {
        var d = message.Download!;
        var (context, refusal) = await GuardAgentAddressAsync(message.Origin, d.Url, d.ToContext());
        if (refusal is not null)
            return refusal;
        if (message.Origin is Origins.Mcp or Origins.Api
            && service.GetSnapshots().Count(s => s.Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Downloading) >= maxPending)
            return Fail(ReplyCode.TooManyPending, TooManyPendingError);
        // Only the command line chooses a folder: MCP and the API go through categories, so they cannot write elsewhere.
        if (d.Folder is not null && message.Origin != Origins.Cli)
            return Fail(ReplyCode.FolderRefused, "Only the command line may choose a folder; use a category.");
        long? categoryId = null;
        if (!string.IsNullOrWhiteSpace(d.Category))
        {
            var wanted = d.Category.Trim();
            // The stored name, the name the interface shows (a built-in one follows the language) and the stable key all find it.
            categoryId = service.Categories.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.CurrentCultureIgnoreCase)
                                                               || string.Equals(c.DisplayName, wanted, StringComparison.CurrentCultureIgnoreCase)
                                                               || string.Equals(c.Key, wanted, StringComparison.OrdinalIgnoreCase))?.Id;
            if (categoryId is null)
                return Fail(ReplyCode.NoCategory, "No category with this name.");
        }
        var added = service.AddAutomatic(d.Url, d.Folder, string.IsNullOrWhiteSpace(d.FileName) ? null : Path.GetFileName(d.FileName), d.Start,
            context, categoryId, d.Queue is null ? null : service.ResolveQueue(d.Queue), allowDuplicate: d.AllowDuplicate == true, paused: d.Start == false,
            ruleOrigin: Rules.RuleOrigins.FromProtocol(message.Origin));
        if (added.Id is { } id)
            return Ok(id: id, result: "added");
        return new ChannelReply(true, Protocol: Messages.ProtocolVersion, Result: "duplicate",
            Duplicate: new DuplicateInfo(ReasonCode(added.Duplicate!.Reason), added.Duplicate.ExistingId, added.Duplicate.ExistingPath));
    }

    /// <summary>The stable code of a duplicate's reason in the protocol and in --json: "sameUrl", "sameName", "sameFile".</summary>
    public static string ReasonCode(Duplicates.DuplicateReason reason) => char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString()[1..];

    private bool Find(ChannelMessage message, out DownloadSnapshot snapshot, out ChannelReply error)
    {
        snapshot = null!;
        error = Fail(ReplyCode.MissingId, "Missing download number.");
        if (message.Id is not { } id)
            return false;
        if (service.GetSnapshot(id) is not { } found)
        {
            error = Fail(ReplyCode.NoDownload, NoDownloadError);
            return false;
        }
        snapshot = found;
        return true;
    }

    private DownloadInfo Info(DownloadSnapshot s) => new(s.Id, s.DisplayName, Log.Url(s.Url), s.Directory, s.FinalPath,
        s.Status.ToString().ToLowerInvariant(), s.Downloaded, s.TotalSize, s.Error is null ? null : Log.Redact(s.Error),
        service.Queues.FirstOrDefault(q => q.Id == s.QueueId)?.DisplayName,
        service.Categories.FirstOrDefault(c => c.Id == s.CategoryId)?.Name, s.AddedUtc, s.CompletedUtc);

    private static ChannelReply Fail(ReplyCode code, string error) => new(false, Error: error, Protocol: Messages.ProtocolVersion, Code: code);

    private static ChannelReply Ok(string? version = null, List<DownloadInfo>? downloads = null, DownloadInfo? download = null, long? id = null,
        int? count = null, string? result = null) =>
        new(true, Protocol: Messages.ProtocolVersion, AppVersion: version, Downloads: downloads, Download: download, Id: id, Count: count, Result: result);
}
