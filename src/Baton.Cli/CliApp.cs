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
/// Texts for people come in English and Italian (<see cref="Msg"/>, the language of Windows); <c>--json</c> always carries the English
/// text and the stable codes.
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
          probe <url>             What an address is (name, type, size, ranges) without downloading it
          pause <id|all>          Pause a download, or all
          resume <id|all>         Resume (start) a download, or all paused and failed ones
          retry <id|all>          Try a failed download again, or all failed ones
          queues                  The names of the queues (for --queue)
          queues start|stop [name]  Start or stop a queue (the main queue without a name)
          categories              The names of the categories (for --category)
          open <id>               Open the finished file
          remove <id>             Remove a download from the list (a finished file stays on the disk)
          remove --all-completed  Remove all completed downloads from the list
          version                 Versions of batonctl and of the running Baton
          mcp                     Run as an MCP server on standard input and output, for AI agents (see docs/MCP.md)

        --json prints the answer as JSON, the same fields every time. Baton is started in the notification area if it is closed.

        Exit codes: 0 done, 1 error, 2 wrong arguments, 3 Baton could not be reached, 4 duplicate skipped.

        Examples:
          batonctl add https://example.com/file.zip --category Software
          batonctl add https://example.com/a.iso --folder D:\Downloads --no-start
          Get-Content links.txt | batonctl add -
          batonctl list --status downloading --json
          batonctl pause all
        """;

    public const string HelpItalian = """
        batonctl - la riga di comando di Baton

        Uso: batonctl <comando> [opzioni] [--json]

        Comandi:
          add <url...|->          Aggiunge download (- legge un indirizzo per riga dall'input)
              --folder <percorso>   cartella di destinazione (un percorso relativo parte dalla cartella corrente)
              --category <nome>     categoria che decide la cartella
              --queue <nome>        coda in cui metterlo
              --no-start            lo aggiunge in pausa (--start, il predefinito, lo avvia)
              --allow-duplicate     lo aggiunge anche se è un duplicato
          list [--status <s>]     I download (stato: queued, downloading, paused, completed, failed)
          status <id>             Un download
          probe <url>             Cos'è un indirizzo (nome, tipo, dimensione, ripresa) senza scaricarlo
          pause <id|all>          Mette in pausa un download, o tutti
          resume <id|all>         Riprende (avvia) un download, o tutti quelli in pausa o falliti
          retry <id|all>          Riprova un download fallito, o tutti quelli falliti
          queues                  I nomi delle code (per --queue)
          queues start|stop [nome]  Avvia o ferma una coda (la coda principale senza nome)
          categories              I nomi delle categorie (per --category)
          open <id>               Apre il file finito
          remove <id>             Toglie un download dall'elenco (un file finito resta sul disco)
          remove --all-completed  Toglie dall'elenco tutti i download completati
          version                 Versioni di batonctl e del Baton in esecuzione
          mcp                     Si avvia come server MCP su input e output standard, per gli agenti AI (vedi docs/MCP.md)

        --json stampa la risposta come JSON, con gli stessi campi ogni volta (in inglese). Se Baton è chiuso viene avviato nell'area di notifica.

        Codici di uscita: 0 fatto, 1 errore, 2 argomenti sbagliati, 3 Baton non raggiungibile, 4 duplicato saltato.

        Esempi:
          batonctl add https://example.com/file.zip --category Software
          batonctl add https://example.com/a.iso --folder D:\Downloads --no-start
          Get-Content links.txt | batonctl add -
          batonctl list --status downloading --json
          batonctl pause all
        """;

    private string T(string en, string it) => italian ? it : en;

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await RunCoreAsync(args);
        }
        catch (AppElevatedException)
        {
            // Baton runs as administrator: its pipe is not reachable from a normal program. Not a .NET resource key: what to do.
            var problem = new Msg("Baton is running as administrator: start it normally, or run batonctl from an administrator terminal.",
                "Baton è avviato come amministratore: avvialo normalmente oppure usa batonctl da un terminale amministratore.");
            if (args.Contains("--json"))
                Json(new CodedErrorOutput(false, problem.En, "appElevated"));
            else
                error.WriteLine($"batonctl: {problem.For(italian)}");
            return ExitCodes.NotRunning;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never an unhandled exception (exit code 0xE0434352 and a stack trace): a pipe that cannot be used is "not reached", the rest is an error.
            var unreachable = ex is IOException or UnauthorizedAccessException or TimeoutException;
            var detail = AgentText.Clean(ex.Message, AgentText.MaxMessage);
            var problem = unreachable ? new Msg($"Baton could not be reached: {detail}", $"Impossibile raggiungere Baton: {detail}")
                : new Msg($"Unexpected error ({ex.GetType().Name}): {detail}", $"Errore imprevisto ({ex.GetType().Name}): {detail}");
            if (args.Contains("--json"))
                Json(new ErrorOutput(false, problem.En));
            else
                error.WriteLine($"batonctl: {problem.For(italian)}");
            return unreachable ? ExitCodes.NotRunning : ExitCodes.Error;
        }
    }

    private async Task<int> RunCoreAsync(string[] args)
    {
        var parsed = ArgParser.Parse(args);
        if (parsed.Error is { } problem)
            return Usage(parsed, problem);
        switch (parsed.Command)
        {
            case "help":
                if (parsed.Json)
                    Json(new HelpOutput(true, Help));
                else
                    output.WriteLine(italian ? HelpItalian : Help);
                return ExitCodes.Ok;
            case "version":
                return await VersionAsync(parsed);
            case "add":
                return await AddAsync(parsed);
            case "list":
                return await ListAsync(parsed);
            case "status":
                return await OneAsync(parsed, Messages.Status, withProgress: true);
            case "probe":
                return await ProbeAsync(parsed);
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
            case "categories":
                return await NamesAsync(parsed, Messages.Categories);
            default:
                return Usage(parsed, new Msg("Unknown command.", "Comando sconosciuto."));
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
        output.WriteLine(reply?.Ok == true ? T($"Baton {reply.AppVersion} (running)", $"Baton {reply.AppVersion} (in esecuzione)")
            : reply is not null ? T("Baton is running, but it is older than 1.4 and does not know the command line",
                "Baton è in esecuzione, ma è più vecchio della 1.4 e non conosce la riga di comando")
            : T("Baton is not running", "Baton non è in esecuzione"));
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
                    // A file saved with a BOM (Notepad, PowerShell 5.1 Out-File) starts with U+FEFF, which Trim() does not remove.
                    if (line.Trim().TrimStart('﻿').Trim() is { Length: > 0 } text)
                        urls.Add(text);
                }
            }
            else
            {
                urls.Add(word);
            }
        }
        if (urls.Count == 0)
            return Usage(parsed, new Msg("Give at least one address.", "Indica almeno un indirizzo."));
        foreach (var url in urls)
        {
            if (!Urls.TryParseDownload(url, out _))
                return Usage(parsed, new Msg($"Not a download address: {url}", $"Non è un indirizzo di download: {url}"));
        }
        if (parsed.Has("start") && parsed.Has("no-start"))
            return Usage(parsed, new Msg("--start and --no-start exclude each other.", "--start e --no-start si escludono a vicenda."));
        string? folder = null;
        if (parsed.Value("folder") is { } given)
        {
            if (BadFolderCharacters(given) is { } why)
                return Usage(parsed, why);
            try
            {
                folder = Path.GetFullPath(given, workingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Usage(parsed, new Msg("Not a valid folder.", "Cartella non valida."));
            }
        }

        var results = new List<AddResult>();
        var localErrors = new List<string?>(); // the words for the terminal of each result's error, same order
        var unreachable = ExitCodes.Ok;
        foreach (var url in urls)
        {
            if (unreachable != ExitCodes.Ok)
            {
                // Baton went away: what was added stays reported, the rest says it was never sent.
                results.Add(new AddResult(url, "notAttempted", null, null, null));
                localErrors.Add(null);
                continue;
            }
            var download = new BrowserDownload(url, Folder: folder, Category: parsed.Value("category"), Queue: parsed.Value("queue"),
                Start: parsed.Has("no-start") ? false : null, AllowDuplicate: parsed.Has("allow-duplicate") ? true : null);
            var (reply, code) = await CallAsync(new ChannelMessage(Messages.Add, Origin: Origins.Cli, Download: download));
            if (reply is null)
            {
                unreachable = code;
                var why = _unreachable ?? new Msg("Baton could not be reached.", "Impossibile raggiungere Baton.");
                results.Add(new AddResult(url, "error", null, null, why.En));
                localErrors.Add(why.For(italian));
                continue;
            }
            if (!reply.Ok)
            {
                var text = Why(reply);
                results.Add(new AddResult(url, "error", null, null, text.En));
                localErrors.Add(text.For(italian));
                continue;
            }
            localErrors.Add(null);
            results.Add(reply.Result == "duplicate"
                ? new AddResult(url, "duplicate", reply.Duplicate?.ExistingId, reply.Duplicate is null ? null : AgentText.Safe(reply.Duplicate), null)
                : new AddResult(url, "added", reply.Id, null, null));
        }
        var exit = unreachable != ExitCodes.Ok ? unreachable
            : results.Any(r => r.Status == "error") ? ExitCodes.Error : results.Any(r => r.Status == "duplicate") ? ExitCodes.Duplicate : ExitCodes.Ok;
        if (parsed.Json)
        {
            Json(new AddOutput(exit == ExitCodes.Ok, results));
            return exit;
        }
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.Status == "added")
                output.WriteLine($"{T("added", "aggiunto")} #{r.Id}  {r.Url}");
            else if (r.Status == "duplicate")
                error.WriteLine($"{CliText.Skipped(CliText.DuplicateReason(r.Duplicate?.Reason, italian), r.Id, italian)}  {r.Url}");
            else if (r.Status == "notAttempted")
                error.WriteLine($"{T("not attempted", "non tentato")}  {r.Url}");
            else
                error.WriteLine($"{T("error", "errore")}: {localErrors[i]}  {r.Url}");
        }
        return exit;
    }

    private async Task<int> ListAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count > 0)
            return Usage(parsed, new Msg("list takes no arguments.", "list non accetta argomenti."));
        var status = parsed.Value("status")?.ToLowerInvariant();
        if (status is not null && status is not ("queued" or "downloading" or "paused" or "completed" or "failed"))
            return Usage(parsed, new Msg("Status must be queued, downloading, paused, completed or failed.",
                "Lo stato deve essere queued, downloading, paused, completed o failed."));
        var (reply, code) = await CallAsync(new ChannelMessage(Messages.List, Origin: Origins.Cli));
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        var downloads = (reply.Downloads ?? []).Select(AgentText.Safe).Where(d => status is null || d.Status == status).ToList();
        if (parsed.Json)
        {
            Json(new ListOutput(true, downloads));
            return ExitCodes.Ok;
        }
        foreach (var d in downloads)
            output.WriteLine($"{d.Id,5}  {d.Status,-11} {Progress(d),5}  {d.Name}");
        if (downloads.Count == 0)
            output.WriteLine(T("No downloads.", "Nessun download."));
        return ExitCodes.Ok;
    }

    /// <summary>probe: asks the app what an address is, without adding anything.</summary>
    private async Task<int> ProbeAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count != 1 || !Urls.TryParseDownload(parsed.Words[0], out _))
            return Usage(parsed, new Msg("probe needs one download address.", "probe richiede un indirizzo di download."));
        var (reply, code) = await CallAsync(new ChannelMessage(Messages.Probe, Origin: Origins.Cli, Url: parsed.Words[0]));
        // An app of protocol 4 (1.4.1) answers an unknown type with an error and no code; a reply that is Ok without a probe is as old.
        if (reply is { Ok: true, Probe: null } || (reply is { Ok: false, Code: null } && reply.Protocol is null or < 5))
            return TooOld(parsed, "probe");
        if (reply is null || !reply.Ok || reply.Probe is not { } probe)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
        {
            Json(new ProbeOutput(true, AgentText.Safe(probe)));
            return ExitCodes.Ok;
        }
        output.WriteLine(AgentText.Clean(probe.Name));
        output.WriteLine($"  {T("type   ", "tipo   ")} {probe.ContentType ?? "-"}");
        output.WriteLine($"  {T("size   ", "peso   ")} {(probe.Size is { } size ? size.ToString(CultureInfo.InvariantCulture) + " bytes" : T("unknown", "sconosciuto"))}");
        output.WriteLine($"  {T("ranges ", "ripresa")} {(probe.Resumable ? T("yes (can resume)", "sì (si può riprendere)") : "no")}");
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
            Json(new DownloadOutput(true, Safe(reply.Download), reply.Id ?? reply.Download?.Id));
            return ExitCodes.Ok;
        }
        if (withProgress && Safe(reply.Download) is { } d)
        {
            output.WriteLine($"#{d.Id}  {d.Name}");
            output.WriteLine($"  {T("status  ", "stato   ")} {d.Status}{(d.Error is null ? "" : " - " + d.Error)}");
            output.WriteLine($"  {T("progress", "avanzam.")} {Progress(d)} ({d.Downloaded.ToString(CultureInfo.InvariantCulture)}{(d.TotalSize is { } t ? "/" + t.ToString(CultureInfo.InvariantCulture) : "")} bytes)");
            output.WriteLine($"  {T("folder  ", "cartella")} {d.Folder}");
            output.WriteLine($"  {T("queue   ", "coda    ")} {d.Queue ?? "-"}   {T("category", "categoria")} {d.Category ?? "-"}");
        }
        else
        {
            output.WriteLine(T("done", "fatto"));
        }
        return ExitCodes.Ok;
    }

    private async Task<int> ActAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count != 1)
            return Usage(parsed, new Msg($"{parsed.Command} needs a download number, or all.", $"{parsed.Command} richiede il numero di un download, oppure all."));
        var all = parsed.Words[0].Equals("all", StringComparison.OrdinalIgnoreCase);
        long id = 0;
        if (!all && !TryId(parsed, out id, out var bad))
            return bad;
        var message = new ChannelMessage(parsed.Command, Origin: Origins.Cli, Id: all ? null : id, All: all ? true : null);
        var (reply, code) = await CallAsync(message);
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
            Json(new DownloadOutput(true, Safe(reply.Download), reply.Id) { Count = reply.Count });
        else
            output.WriteLine(reply.Count is { } count ? T($"{count} download(s)", $"{count} download") : T("done", "fatto"));
        return ExitCodes.Ok;
    }

    private async Task<int> RemoveAsync(ParsedArgs parsed)
    {
        var allCompleted = parsed.Has("all-completed");
        if (allCompleted == (parsed.Words.Count > 0))
            return Usage(parsed, new Msg("remove needs a download number, or --all-completed.", "remove richiede il numero di un download, oppure --all-completed."));
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
            output.WriteLine(reply.Count is { } count ? T($"{count} download(s) removed from the list", $"{count} download tolti dall'elenco")
                : T("removed from the list", "tolto dall'elenco"));
        return ExitCodes.Ok;
    }

    /// <summary>queues (no word) and categories: the names to give to --queue and --category.</summary>
    private async Task<int> NamesAsync(ParsedArgs parsed, string type)
    {
        if (parsed.Words.Count > 0)
            return Usage(parsed, type == Messages.Queues
                ? new Msg($"{parsed.Command} takes no arguments here (queues start|stop [name] starts or stops one).",
                    $"{parsed.Command} qui non accetta argomenti (queues start|stop [nome] avvia o ferma una coda).")
                : new Msg($"{parsed.Command} takes no arguments here.", $"{parsed.Command} qui non accetta argomenti."));
        var (reply, code) = await CallAsync(new ChannelMessage(type, Origin: Origins.Cli));
        // "categories" is new in 1.4.2: an older app answers an unknown type with an error, protocol 4 or less and no code.
        if (type == Messages.Categories && reply is { Ok: false, Code: null } && reply.Protocol is null or < 5)
            return TooOld(parsed, "categories");
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        var names = (type == Messages.Queues ? reply.Queues : reply.Categories) ?? [];
        names = names.Select(n => AgentText.Clean(n, AgentText.MaxName)).ToList();
        if (parsed.Json)
            Json(type == Messages.Queues ? new NamesOutput(true, names, null) : new NamesOutput(true, null, names));
        else
            foreach (var name in names)
                output.WriteLine(name);
        return ExitCodes.Ok;
    }

    private async Task<int> QueuesAsync(ParsedArgs parsed)
    {
        if (parsed.Words.Count == 0)
            return await NamesAsync(parsed, Messages.Queues);
        if (parsed.Words.Count is < 1 or > 2 || parsed.Words[0] is not ("start" or "stop"))
            return Usage(parsed, new Msg("queues needs start or stop, and optionally a queue name.", "queues richiede start o stop, e facoltativamente il nome di una coda."));
        var type = parsed.Words[0] == "start" ? Messages.QueueStart : Messages.QueueStop;
        var (reply, code) = await CallAsync(new ChannelMessage(type, Origin: Origins.Cli, Queue: parsed.Words.Count == 2 ? parsed.Words[1] : null));
        if (reply is null || !reply.Ok)
            return Failed(parsed, code, Why(reply));
        if (parsed.Json)
            Json(new DownloadOutput(true, null, null));
        else
            output.WriteLine(T("done", "fatto"));
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ helpers

    private static readonly Msg BusyMessage = new(
        "Baton is running but busy: it did not answer in time. The command may still be carried out; check with \"batonctl list\" before trying again.",
        "Baton è in esecuzione ma occupato: non ha risposto in tempo. Il comando potrebbe essere comunque eseguito; controlla con \"batonctl list\" prima di riprovare.");

    private static readonly Msg ClosedMessage = new(
        "Baton closed the connection without answering; the command may have been done. Check with \"batonctl list\" before trying again.",
        "Baton ha chiuso la connessione senza rispondere; il comando potrebbe essere stato eseguito. Controlla con \"batonctl list\" prima di riprovare.");

    /// <summary>Sends the message; with the app closed, starts it first. The code is the exit code when there is no reply.</summary>
    private async Task<(ChannelReply? Reply, int Code)> CallAsync(ChannelMessage message)
    {
        // An answer that does not come in time means Baton is running but busy: not "unreachable", and not to be sent twice.
        (ChannelReply, int) Synthetic(Msg text)
        {
            _synthetic = text;
            return (new ChannelReply(false, Protocol: Messages.ProtocolVersion, Error: text.En), ExitCodes.Ok);
        }
        ChannelReply? reply;
        try
        {
            reply = await send(message);
        }
        catch (OperationCanceledException)
        {
            return Synthetic(BusyMessage);
        }
        catch (ChannelNoReplyException)
        {
            // Reached, but the connection closed before an answer: the command may have been done, so it is never sent again.
            return Synthetic(ClosedMessage);
        }
        if (reply is not null)
            return (reply, ExitCodes.Ok);
        // Standard error, so that --json and a script reading standard output see nothing else than the answer.
        error.WriteLine(T("batonctl: Starting Baton...", "batonctl: avvio di Baton..."));
        if (!await startApp())
        {
            _unreachable = new Msg("Baton is not running and could not be started. Start Baton from the Start menu, then try again.",
                "Baton non è in esecuzione e non è stato possibile avviarlo. Avvia Baton dal menu Start, poi riprova.");
            return (null, ExitCodes.NotRunning);
        }
        try
        {
            reply = await send(message);
        }
        catch (OperationCanceledException)
        {
            return Synthetic(BusyMessage);
        }
        catch (ChannelNoReplyException)
        {
            return Synthetic(ClosedMessage);
        }
        if (reply is null)
            _unreachable = new Msg("Baton was started but did not answer. Try again in a moment.", "Baton è stato avviato ma non ha risposto. Riprova tra un momento.");
        return reply is null ? (null, ExitCodes.NotRunning) : (reply, ExitCodes.Ok);
    }

    /// <summary>Why the last call found nobody (set by <see cref="CallAsync"/>), for the message of exit code 3.</summary>
    private Msg? _unreachable;

    /// <summary>The last answer that batonctl made up itself (busy, closed), to show it in the language of the terminal.</summary>
    private Msg? _synthetic;

    private bool TryId(ParsedArgs parsed, out long id, out int exit)
    {
        id = 0;
        exit = ExitCodes.Ok;
        if (parsed.Words.Count == 1 && long.TryParse(parsed.Words[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0)
            return true;
        exit = Usage(parsed, new Msg($"{parsed.Command} needs a download number.", $"{parsed.Command} richiede il numero di un download."));
        return false;
    }

    /// <summary>
    /// Why a --folder value cannot be a folder, or null. The usual cause is <c>--folder "D:\x\"</c>: Windows reads the backslash
    /// before the closing quote as an escaped quote, so the value arrives as <c>D:\x"</c>.
    /// </summary>
    private static Msg? BadFolderCharacters(string folder)
    {
        if (folder.Contains('"'))
            return new Msg("--folder contains a quote. A backslash before the closing quote escapes it: write \"D:\\x\" without the last backslash, or double it (\"D:\\x\\\\\").",
                "--folder contiene una virgoletta. Un backslash prima della virgoletta di chiusura la annulla: scrivi \"D:\\x\" senza l'ultimo backslash, oppure raddoppialo (\"D:\\x\\\\\").");
        for (var i = 0; i < folder.Length; i++)
        {
            var c = folder[i];
            if (c is '*' or '?' or '<' or '>' or '|' || char.IsControl(c) || (c == ':' && i != 1))
            {
                var shown = char.IsControl(c) ? null : c.ToString();
                return new Msg($"--folder contains a character that cannot be in a path: U+{(int)c:X4} ({shown ?? "control"})",
                    $"--folder contiene un carattere che non può stare in un percorso: U+{(int)c:X4} ({shown ?? "di controllo"})");
            }
        }
        return null;
    }

    private static string Progress(DownloadInfo d) =>
        d.Status == "completed" ? "100%" : d.TotalSize is > 0 ? $"{Math.Min(100, d.Downloaded * 100 / d.TotalSize.Value)}%" : "-";

    /// <summary>Why a reply is not a success. An app older than 1.4 answers the new commands with an error and no protocol number.</summary>
    private Msg Why(ChannelReply? reply)
    {
        if (reply?.Error is not { } text)
            return _unreachable ?? new Msg("Baton could not be reached.", "Impossibile raggiungere Baton.");
        if (_synthetic is { } made && text == made.En)
            return made;
        var clean = AgentText.Clean(text, AgentText.MaxMessage);
        // A refusal with a code the CLI knows is worded in the language of Windows; what the app said stays for --json and an English terminal.
        // The codes that carry a detail of their own (why a probe failed, what was not valid) keep it next to the Italian words.
        if (reply.Protocol is not null && reply.Code is { } known)
        {
            var words = CliText.ForCode(known).It;
            return new Msg(clean, known is ReplyCode.Failed or ReplyCode.Invalid or ReplyCode.ProbeFailed ? $"{words} ({clean})" : words);
        }
        return reply.Protocol is null
            ? new Msg(clean + " (this Baton is older than 1.4 and does not know the command line: update it)",
                clean + " (questo Baton è più vecchio della 1.4 e non conosce la riga di comando: aggiornalo)")
            : clean;
    }

    private int Usage(ParsedArgs parsed, Msg problem)
    {
        if (parsed.Json)
            Json(new ErrorOutput(false, problem.En));
        else
            error.WriteLine($"batonctl: {problem.For(italian)}\n{T("Try", "Prova")}: batonctl --help");
        return ExitCodes.Usage;
    }

    /// <summary>The app is older than the command needs: the exit code is 1 and, with --json, the code is "appTooOld" (as in MCP).</summary>
    private int TooOld(ParsedArgs parsed, string command)
    {
        var problem = new Msg($"This Baton is older than 1.4.2 and does not know \"{command}\": update it.",
            $"Questo Baton è più vecchio della 1.4.2 e non conosce \"{command}\": aggiornalo.");
        if (parsed.Json)
            Json(new CodedErrorOutput(false, problem.En, "appTooOld"));
        else
            error.WriteLine($"batonctl: {problem.For(italian)}");
        return ExitCodes.Error;
    }

    private int Failed(ParsedArgs parsed, int code, Msg problem)
    {
        if (parsed.Json)
            Json(new ErrorOutput(false, problem.En));
        else
            error.WriteLine($"batonctl: {problem.For(italian)}");
        return code == ExitCodes.Ok ? ExitCodes.Error : code;
    }

    /// <summary>A download with the texts a server or a user chose cleaned (a terminal and a script must not get control characters).</summary>
    private static DownloadInfo? Safe(DownloadInfo? d) => d is null ? null : AgentText.Safe(d);

    private void Json<T>(T value) where T : class => output.WriteLine(JsonSerializer.Serialize(value, typeof(T), CliJson.Default));
}
