using Baton.Core.Duplicates;

namespace Baton.Core.Integration;

/// <summary>
/// The control messages of protocol 4 (list, status, pause, resume, retry, remove, open, queue start/stop, version and "add" from
/// an origin that cannot show a dialog). The answers carry <see cref="DownloadInfo"/>, never the secrets of a download.
/// </summary>
public sealed class ControlHandler(DownloadService service, Func<AutomationLevel> mcpLevel, Func<AutomationLevel> apiLevel,
    Action<string> openFile, string appVersion)
{
    /// <summary>Null when the message may go on; otherwise the refusal to send back.</summary>
    public ChannelReply? Authorize(ChannelMessage message) =>
        OriginPolicy.Check(message, mcpLevel(), apiLevel()) is { } error ? Fail(error) : null;

    /// <summary>True for the types this handler answers (everything but the browser's own).</summary>
    public static bool Handles(ChannelMessage message) =>
        message.Type is Messages.List or Messages.Status or Messages.Pause or Messages.Resume or Messages.Retry or Messages.Remove
            or Messages.Open or Messages.QueueStart or Messages.QueueStop or Messages.Version
        || (message.Type == Messages.Add && (message.Origin ?? Origins.Browser) != Origins.Browser);

    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".lnk",
        ".hta", ".jar", ".reg", ".cpl", ".dll", ".appx", ".msix", ".url",
    };

    /// <summary>
    /// Answers one message. A failure inside (a file type with no program, a database error) is an error reply: a connection closed
    /// without an answer would look like "Baton is not running" to the command line, which would start it and send the message again.
    /// </summary>
    public async Task<ChannelReply> HandleAsync(ChannelMessage message)
    {
        try
        {
            return await HandleCoreAsync(message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Fail("Baton could not do that: " + Log.Redact(ex.Message));
        }
    }

    private async Task<ChannelReply> HandleCoreAsync(ChannelMessage message)
    {
        DownloadSnapshot found;
        ChannelReply error;
        switch (message.Type)
        {
            case Messages.Version:
                return Ok(version: appVersion);
            case Messages.List:
                return Ok(downloads: service.GetSnapshots().Select(Info).ToList());
            case Messages.Status:
                return Find(message, out found, out error) ? Ok(download: Info(found)) : error;
            case Messages.Add:
                return Add(message);
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
                    return Fail("This download cannot be started again.");
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
                await service.RemoveAsync(found.Id, deleteFile: false);
                return Ok(id: found.Id);
            case Messages.Open:
                if (!Find(message, out found, out error))
                    return error;
                if (found.Status != DownloadStatus.Completed || found.FinalPath is not { } path || !File.Exists(path))
                    return Fail("The file is not there.");
                // The command line is the user's own terminal; MCP and the API could chain "add" and "open" into running a download.
                if (message.Origin != Origins.Cli && Runnable.Contains(Path.GetExtension(path)))
                    return Fail("Only the command line may open a program or script.");
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
                    return Fail("No queue with this name.");
                if (message.Type == Messages.QueueStart)
                    service.StartQueue(queue.Id);
                else
                    await service.StopQueueAsync(queue.Id);
                return Ok();
            default:
                return Fail("Unknown message type.");
        }
    }

    private ChannelReply Add(ChannelMessage message)
    {
        var d = message.Download!;
        // Only the command line chooses a folder: MCP and the API go through categories, so they cannot write elsewhere.
        if (d.Folder is not null && message.Origin != Origins.Cli)
            return Fail("Only the command line may choose a folder; use a category.");
        long? categoryId = null;
        if (!string.IsNullOrWhiteSpace(d.Category))
        {
            var wanted = d.Category.Trim();
            categoryId = service.Categories.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.CurrentCultureIgnoreCase)
                                                               || string.Equals(c.Key, wanted, StringComparison.OrdinalIgnoreCase))?.Id;
            if (categoryId is null)
                return Fail("No category with this name.");
        }
        var added = service.AddAutomatic(d.Url, d.Folder, string.IsNullOrWhiteSpace(d.FileName) ? null : Path.GetFileName(d.FileName), d.Start ?? true,
            d.ToContext(), categoryId, service.ResolveQueue(d.Queue), allowDuplicate: d.AllowDuplicate == true, paused: d.Start == false);
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
        error = Fail("Missing download number.");
        if (message.Id is not { } id)
            return false;
        if (service.GetSnapshot(id) is not { } found)
        {
            error = Fail("No download with this number.");
            return false;
        }
        snapshot = found;
        return true;
    }

    private DownloadInfo Info(DownloadSnapshot s) => new(s.Id, s.DisplayName, Log.Url(s.Url), s.Directory, s.FinalPath,
        s.Status.ToString().ToLowerInvariant(), s.Downloaded, s.TotalSize, s.Error is null ? null : Log.Redact(s.Error),
        service.Queues.FirstOrDefault(q => q.Id == s.QueueId)?.DisplayName,
        service.Categories.FirstOrDefault(c => c.Id == s.CategoryId)?.Name, s.AddedUtc, s.CompletedUtc);

    private static ChannelReply Fail(string error) => new(false, Error: error, Protocol: Messages.ProtocolVersion);

    private static ChannelReply Ok(string? version = null, List<DownloadInfo>? downloads = null, DownloadInfo? download = null, long? id = null,
        int? count = null, string? result = null) =>
        new(true, Protocol: Messages.ProtocolVersion, AppVersion: version, Downloads: downloads, Download: download, Id: id, Count: count, Result: result);
}
