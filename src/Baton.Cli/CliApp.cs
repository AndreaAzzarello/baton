using System.Globalization;
using System.Text.Json;
using Baton.Core;
using Baton.Core.Integration;

namespace Baton.Cli;

/// <summary>Exit codes of batonctl (documented in docs/CLI.md).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int NotRunning = 3;
    public const int Duplicate = 4;
}

/// <summary>
/// The commands of batonctl. It talks to the running app only through <paramref name="send"/> (the pipe) and starts the app with
/// <paramref name="startApp"/> when nobody answers, so the tests run it against a pretend app.
/// </summary>
/// <param name="send">One message to the app; null when no app listens.</param>
/// <param name="startApp">Starts Baton in the notification area and waits until it answers; false if it could not.</param>
internal sealed class CliApp(TextWriter output, TextWriter error, TextReader input, Func<ChannelMessage, Task<ChannelReply?>> send,
    Func<Task<bool>> startApp, string cliVersion, string workingDirectory, bool italian = false)
{
    public const string Help = """
        batonctl - the command line of Baton

        Usage: batonctl <command> [options] [--json]

        Commands:
          add <url...|->          Add downloads (- reads one address per line from the input)
              --folder <path>       destination folder (a relative path is taken from the current folder)
              --category <name>     category that decides the folder
              --queue <name>        queue to put it in
              --no-start            add it paused (--start, the default, starts it)
              --allow-duplicate     add it even if it is a duplicate
          list [--status <s>]     The downloads (status: queued, downloading, paused, completed, failed)
          status <id>             One download
          pause <id|all>          Pause a download, or all
          resume <id|all>         Resume (start) a download, or all paused and failed ones
          retry <id|all>          Try a failed download again, or all failed ones
          queues start|stop [name]  Start or stop a queue (the main queue without a name)
          open <id>               Open the finished file
          remove <id>             Remove a download from the list (the file stays on the disk)
          remove --all-completed  Remove all completed downloads from the list
          version                 Versions of batonctl and of the running Baton

        --json prints the answer as JSON, the same fields every time. Baton is started in the notification area if it is closed.

        Exit codes: 0 done, 1 error, 2 wrong arguments, 3 Baton could not be reached, 4 duplicate skipped.

        Examples:
          batonctl add https://example.com/file.zip --category Software
          batonctl add https://example.com/a.iso --folder D:\Downloads --no-start
          Get-Content links.txt | batonctl add -
          batonctl list --status downloading --json
          batonctl pause all
        """;

    public async Task<int> RunAsync(string[] args)
    {
        var parsed = ArgParser.Parse(args);
        if (parsed.Error is { } problem)
            return Usage(parsed, problem);
        switch (parsed.Command)
        {
            case "help":
                output.WriteLine(Help);
                return ExitCodes.Ok;
            case "version":
                return await VersionAsync(parsed);
            case "add":
                return await AddAsync(parsed);
            case "list":
                return await ListAsync(parsed);
            case "status":
                return await OneAsync(parsed, Messages.Status, withProgress: true);
            case "open":
                return await OneAsync(parsed, Messages.Open, withProgress: false);
            case "pause":
            case "resume":
            case "retry":
                return await ActAsync(parsed);
            case "remove":
                return await RemoveAsync(parsed);
            case "queues":
                return await QueuesAsync(parsed);
            default:
                return Usage(parsed, "Unknown command.");
        }
    }

    // ------------------------------------------------------------------ commands

    private async Task<int> VersionAsync(ParsedArgs parsed)
    {
        // A question: it never starts the app.
        var reply = await send(new ChannelMessage(Messages.Version, Origin: Origins.Cli));
        if (parsed.Json)
        {
            Json(new VersionOutput(cliVersion, reply?.Ok == true ? reply.AppVersion : null, reply?.Ok == true));
            return ExitCodes.Ok;
        }
        output.WriteLine($"batonctl {cliVersion}");
        output.WriteLine(reply?.Ok == true ? $"Baton {reply.AppVersion} (running)"
            : reply is not null ? "Baton is running, but it is older than 1.4 and does not know the command line" : "Baton is not running");
        return ExitCodes.Ok;
    }

    private async Task<int> AddAsync(ParsedArgs parsed)
    {
        var urls = new List<string>();
        foreach (var word in parsed.Words)
        {
            if (word == "-")
            {
                for (var line = await input.ReadLineAsync(); line is not null; line = await input.ReadLineAsync())
                {
                    if (line.Trim() is { Length: > 0 } text)
                        urls.Add(text);
                }
            }
            else
            {
                urls.Add(word);
            }
        }
        if (urls.Count == 0)
            return Usage(parsed, "Give at least one address.");
        foreach (var url in urls)
        {
            if (!Urls.TryParseDownload(url, out _))
                return Usage(parsed, $"Not a download address: {url}");
        }
        if (parsed.Has("start") && parsed.Has("no-start"))
            return Usage(parsed, "--start and --no-start exclude each other.");
        string? folder = null;
        if (parsed.Value("folder") is { } given)
        {
            try
            {
                folder = Path.GetFullPath(given, workingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Usage(parsed, "Not a valid folder.");
            }
        }

        var results = new List<AddResult>();
        foreach (var url in urls)
        {
            var download = new BrowserDownload(url, Folder: folder, Category: parsed.Value("category"), Queue: parsed.Value("queue"),
                Start: parsed.Has("no-start") ? false : null, AllowDuplicate: parsed.Has("allow-duplicate") ? true : null);
            var (reply, code) = await CallAsync(new ChannelMessage(Messages.Add, Download: download, Origin: Origins.Cli));
            if (reply is null)
                return Failed(parsed, code, "Baton could not be reached.");
            results.Add(!reply.Ok
                ? new AddResult(url, "error", null, null, reply.Error)
                : reply.Result == "duplicate"
                    ? new AddResult(url, "duplicate", reply.Duplicate?.ExistingId, reply.Duplicate, null)
                    : new AddResult(url, "added", reply.Id, null, null));
        }
        var exit = results.Any(r => r.Status == "error") ? ExitCodes.Error : results.Any(r => r.Status == "duplicate") ? ExitCodes.Duplicate : ExitCodes.Ok;
        if (parsed.Json)
        {
            Json(new AddOutput(exit == ExitCodes.Ok, results));
            return exit;
        }
        foreach (var r in results)
        {
            if (r.Status == "added")
                output.WriteLine($"added #{r.Id}  {r.Url}");
            else if (r.Status == "duplicate")
                error.WriteLine($"{CliText.Skipped(CliText.DuplicateReason(r.Duplicate?.Reason, italian), r.Id, italian)}  {r.Url}");
            else
                error.WriteLine($"error: {r.Error}  {r.Url}");
        }
        return exit;
    }

    private async Task<int> ListAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count > 0)
            return Usage(parsed, "list takes no arguments.");
        var status = parsed.Value("status")?.ToLowerInvariant();
        if (status is not null && status is not ("queued" or "downloading" or "paused" or "completed" or "failed"))
            return Usage(parsed, "Status must be queued, downloading, paused, completed or failed.");
        var (reply, code) = await CallAsync(new ChannelMessage(Messages.List, Origin: Origins.Cli));
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        var downloads = (reply.Downloads ?? []).Where(d => status is null || d.Status == status).ToList();
        if (parsed.Json)
        {
            Json(new ListOutput(true, downloads));
            return ExitCodes.Ok;
        }
        foreach (var d in downloads)
            output.WriteLine($"{d.Id,5}  {d.Status,-11} {Progress(d),5}  {d.Name}");
        if (downloads.Count == 0)
            output.WriteLine("No downloads.");
        return ExitCodes.Ok;
    }

    /// <summary>status and open: one download by number.</summary>
    private async Task<int> OneAsync(ParsedArgs parsed, string type, bool withProgress)
    {
        if (!TryId(parsed, out var id, out var bad))
            return bad;
        var (reply, code) = await CallAsync(new ChannelMessage(type, Origin: Origins.Cli, Id: id));
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
        {
            Json(new DownloadOutput(true, reply.Download, reply.Id ?? reply.Download?.Id));
            return ExitCodes.Ok;
        }
        if (withProgress && reply.Download is { } d)
        {
            output.WriteLine($"#{d.Id}  {d.Name}");
            output.WriteLine($"  status   {d.Status}{(d.Error is null ? "" : " - " + d.Error)}");
            output.WriteLine($"  progress {Progress(d)} ({d.Downloaded.ToString(CultureInfo.InvariantCulture)}{(d.TotalSize is { } t ? "/" + t.ToString(CultureInfo.InvariantCulture) : "")} bytes)");
            output.WriteLine($"  folder   {d.Folder}");
            output.WriteLine($"  queue    {d.Queue ?? "-"}   category {d.Category ?? "-"}");
        }
        else
        {
            output.WriteLine("done");
        }
        return ExitCodes.Ok;
    }

    private async Task<int> ActAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count != 1)
            return Usage(parsed, $"{parsed.Command} needs a download number, or all.");
        var all = parsed.Words[0].Equals("all", StringComparison.OrdinalIgnoreCase);
        long id = 0;
        if (!all && !TryId(parsed, out id, out var bad))
            return bad;
        var message = new ChannelMessage(parsed.Command, Origin: Origins.Cli, Id: all ? null : id, All: all ? true : null);
        var (reply, code) = await CallAsync(message);
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
            Json(new DownloadOutput(true, reply.Download, reply.Id) { Count = reply.Count });
        else
            output.WriteLine(reply.Count is { } count ? $"{count} download(s)" : "done");
        return ExitCodes.Ok;
    }

    private async Task<int> RemoveAsync(ParsedArgs parsed)
    {
        var allCompleted = parsed.Has("all-completed");
        if (allCompleted == (parsed.Words.Count > 0))
            return Usage(parsed, "remove needs a download number, or --all-completed.");
        long id = 0;
        if (!allCompleted && !TryId(parsed, out id, out var bad))
            return bad;
        var message = new ChannelMessage(Messages.Remove, Origin: Origins.Cli, All: allCompleted ? true : null,
            Id: allCompleted ? null : id);
        var (reply, code) = await CallAsync(message);
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
            Json(new DownloadOutput(true, null, reply.Id) { Count = reply.Count });
        else
            output.WriteLine(reply.Count is { } count ? $"{count} download(s) removed from the list" : "removed from the list");
        return ExitCodes.Ok;
    }

    private async Task<int> QueuesAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count is < 1 or > 2 || parsed.Words[0] is not ("start" or "stop"))
            return Usage(parsed, "queues needs start or stop, and optionally a queue name.");
        var type = parsed.Words[0] == "start" ? Messages.QueueStart : Messages.QueueStop;
        var (reply, code) = await CallAsync(new ChannelMessage(type, Origin: Origins.Cli, Queue: parsed.Words.Count == 2 ? parsed.Words[1] : null));
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
            Json(new DownloadOutput(true, null, null));
        else
            output.WriteLine("done");
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Sends the message; with the app closed, starts it first. The code is the exit code when there is no reply.</summary>
    private async Task<(ChannelReply? Reply, int Code)> CallAsync(ChannelMessage message)
    {
        // An answer that does not come in time means Baton is running but busy: not "unreachable", and not to be sent twice.
        static (ChannelReply, int) Late() => (new ChannelReply(false, Protocol: Messages.ProtocolVersion, Error: "Baton did not answer in time."), ExitCodes.Ok);
        ChannelReply? reply;
        try
        {
            reply = await send(message);
        }
        catch (OperationCanceledException)
        {
            return Late();
        }
        if (reply is not null)
            return (reply, ExitCodes.Ok);
        if (!await startApp())
            return (null, ExitCodes.NotRunning);
        try
        {
            reply = await send(message);
        }
        catch (OperationCanceledException)
        {
            return Late();
        }
        return reply is null ? (null, ExitCodes.NotRunning) : (reply, ExitCodes.Ok);
    }

    private bool TryId(ParsedArgs parsed, out long id, out int exit)
    {
        id = 0;
        exit = ExitCodes.Ok;
        if (parsed.Words.Count == 1 && long.TryParse(parsed.Words[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0)
            return true;
        exit = Usage(parsed, $"{parsed.Command} needs a download number.");
        return false;
    }

    private static string Progress(DownloadInfo d) =>
        d.Status == "completed" ? "100%" : d.TotalSize is > 0 ? $"{Math.Min(100, d.Downloaded * 100 / d.TotalSize.Value)}%" : "-";

    /// <summary>Why a reply is not a success. An app older than 1.4 answers the new commands with an error and no protocol number.</summary>
    private static string Why(ChannelReply? reply) =>
        reply?.Error is not { } text ? "Baton could not be reached."
        : reply.Protocol is null ? text + " (this Baton is older than 1.4 and does not know the command line: update it)"
        : text;

    private int Usage(ParsedArgs parsed, string problem)
    {
        if (parsed.Json)
            Json(new ErrorOutput(false, problem));
        else
            error.WriteLine($"batonctl: {problem}\nTry: batonctl --help");
        return ExitCodes.Usage;
    }

    private int Failed(ParsedArgs parsed, int code, string problem)
    {
        if (parsed.Json)
            Json(new ErrorOutput(false, problem));
        else
            error.WriteLine($"batonctl: {problem}");
        return code == ExitCodes.Ok ? ExitCodes.Error : code;
    }

    private void Json<T>(T value) where T : class => output.WriteLine(JsonSerializer.Serialize(value, typeof(T), CliJson.Default));
}
