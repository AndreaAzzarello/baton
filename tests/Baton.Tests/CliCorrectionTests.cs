using System.Text.Json;
using Baton.Cli;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>Corrections to batonctl from the 1.4.2 review (B1-B10): each test fails on the code that had the defect.</summary>
public sealed class CliCorrectionTests
{
    private sealed class Run
    {
        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();
        public List<ChannelMessage> Sent { get; } = [];

        public async Task<int> ExecuteAsync(string[] args, Func<ChannelMessage, ChannelReply?> app, string input = "", bool italian = false, bool canStart = false)
        {
            var cli = new CliApp(Out, Err, new StringReader(input), message =>
            {
                Sent.Add(message);
                return Task.FromResult(app(message));
            }, () => Task.FromResult(canStart), "1.2.3", "C:\\work", italian);
            return await cli.RunAsync(args);
        }

        public JsonDocument OutJson() => JsonDocument.Parse(Out.ToString());
    }

    // ------------------------------------------------------------------ B2: --json is read before anything else

    [Theory]
    [InlineData("list", "--nope", "--json")]
    [InlineData("--nope", "--json")]
    [InlineData("frobnicate", "--json")]
    public void B2_Parser_KnowsTheJsonFlag_EvenWhenAnErrorComesBeforeIt(params string[] args)
    {
        var parsed = ArgParser.Parse(args);
        Assert.NotNull(parsed.Error);
        Assert.True(parsed.Json);
    }

    [Fact]
    public async Task B2_SyntaxErrors_AndHelp_AreJson_WhenJsonIsAskedForAnywhere()
    {
        var bad = new Run();
        Assert.Equal(2, await bad.ExecuteAsync(["list", "--nope", "--json"], _ => new ChannelReply(true)));
        using (var doc = bad.OutJson())
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("", bad.Err.ToString());

        var help = new Run();
        Assert.Equal(0, await help.ExecuteAsync(["--help", "--json"], _ => new ChannelReply(true)));
        using var helpDoc = help.OutJson();
        Assert.True(helpDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Usage: batonctl", helpDoc.RootElement.GetProperty("help").GetString());
    }

    // ------------------------------------------------------------------ B3: an exception is an exit code, never a crash

    [Theory]
    [InlineData(typeof(IOException), 3)]
    [InlineData(typeof(UnauthorizedAccessException), 3)]
    [InlineData(typeof(TimeoutException), 3)]
    [InlineData(typeof(InvalidOperationException), 1)]
    [InlineData(typeof(FormatException), 1)]
    public async Task B3_AnExceptionFromTheCall_IsExitCode3_WhenBatonCannotBeReached_Otherwise1(Type exception, int exit)
    {
        foreach (var json in new[] { false, true })
        {
            var run = new Run();
            string[] args = json ? ["list", "--json"] : ["list"];
            var code = await run.ExecuteAsync(args, _ => throw (Exception)Activator.CreateInstance(exception, "boom")!);
            Assert.Equal(exit, code);
            if (json)
            {
                using var doc = run.OutJson();
                Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Contains("boom", doc.RootElement.GetProperty("error").GetString());
            }
            else
            {
                Assert.Contains("boom", run.Err.ToString());
            }
        }
    }

    // ------------------------------------------------------------------ B6: a folder that cannot be a folder is refused, with the likely cause

    [Theory]
    [InlineData("D:\\x\"")]        // what the Windows command line makes of  --folder "D:\x\"  (the \" is a quote)
    [InlineData("D:\\x*")]
    [InlineData("D:\\x?y")]
    [InlineData("D:\\a<b")]
    [InlineData("D:\\a|b")]
    public async Task B6_AFolderWithCharactersThatCannotBeInAPath_IsRefused_AndNothingIsSent(string folder)
    {
        var run = new Run();
        Assert.Equal(2, await run.ExecuteAsync(["add", "https://a.test/f.zip", "--folder", folder], _ => new ChannelReply(true, Result: "added", Id: 1)));
        Assert.Empty(run.Sent);
        Assert.Contains("--folder", run.Err.ToString());
        if (folder.Contains('"'))
            Assert.Contains("backslash", run.Err.ToString());  // the hint about a trailing backslash before the closing quote
    }

    [Fact]
    public async Task B6_AFolderWithATrailingBackslash_IsAccepted_WhenTheCommandLineKeptItWhole()
    {
        var run = new Run();
        Assert.Equal(0, await run.ExecuteAsync(["add", "https://a.test/f.zip", "--folder", "D:\\x\\"], _ => new ChannelReply(true, Result: "added", Id: 1)));
        Assert.Equal("D:\\x\\",run.Sent.Single().Download!.Folder);
    }

    // ------------------------------------------------------------------ B4: results already obtained are not lost

    [Fact]
    public async Task B4_WhenBatonDisappearsInTheMiddleOfAnAdd_TheDownloadsAlreadyAdded_AreReported_AndTheRestAreNotAttempted()
    {
        var calls = 0;
        ChannelReply? App(ChannelMessage _) => ++calls == 1 ? new ChannelReply(true, Result: "added", Id: 11) : null;
        string[] urls = ["https://a.test/1.zip", "https://a.test/2.zip", "https://a.test/3.zip"];

        var json = new Run();
        Assert.Equal(3, await json.ExecuteAsync(["add", ..urls, "--json"], App));
        using (var doc = json.OutJson())
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            var results = doc.RootElement.GetProperty("results");
            Assert.Equal(3, results.GetArrayLength());
            Assert.Equal("added", results[0].GetProperty("status").GetString());
            Assert.Equal(11, results[0].GetProperty("id").GetInt64());
            Assert.Equal("error", results[1].GetProperty("status").GetString());
            Assert.Equal("notAttempted", results[2].GetProperty("status").GetString());
        }

        calls = 0;
        var text = new Run();
        Assert.Equal(3, await text.ExecuteAsync(["add", ..urls], App));
        Assert.Contains("added #11  https://a.test/1.zip", text.Out.ToString());
        Assert.Contains("https://a.test/2.zip", text.Err.ToString());
        Assert.Contains("not attempted", text.Err.ToString());
    }

    // ------------------------------------------------------------------ B5: a BOM at the start of the input is not part of the address

    [Fact]
    public async Task B5_ABomAtTheStartOfStandardInput_IsIgnored()
    {
        var run = new Run();
        var code = await run.ExecuteAsync(["add", "-"], _ => new ChannelReply(true, Result: "added", Id: 3), input: "﻿https://a.test/1.zip\nhttps://a.test/2.zip\n");
        Assert.Equal(0, code);
        Assert.Equal(["https://a.test/1.zip", "https://a.test/2.zip"], run.Sent.Select(m => m.Download!.Url));
    }

    // ------------------------------------------------------------------ B7: what happened and what to do next, on standard error

    private static async Task<(int Code, Run Run)> Execute(string[] args, Func<ChannelMessage, ChannelReply?> app, bool canStart, Func<Task<bool>>? start = null)
    {
        var run = new Run();
        var cli = new CliApp(run.Out, run.Err, new StringReader(""), m => Task.FromResult(app(m)), start ?? (() => Task.FromResult(canStart)), "1.2.3", "C:\\work");
        return (await cli.RunAsync(args), run);
    }

    [Fact]
    public async Task B7_StartingBaton_IsAnnouncedOnStandardError_NeverOnStandardOutput()
    {
        var running = false;
        var (code, run) = await Execute(["list", "--json"], _ => running ? new ChannelReply(true, Downloads: []) : null, canStart: true,
            start: () => { running = true; return Task.FromResult(true); });
        Assert.Equal(0, code);
        Assert.Contains("Starting Baton", run.Err.ToString());
        using var doc = run.OutJson();                                   // standard output is still one JSON object
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task B7_ABatonThatCannotBeStarted_AndOneThatStartsButNeverAnswers_AreToldApart_WithTheNextStep()
    {
        var (cannot, a) = await Execute(["list"], _ => null, canStart: false);
        Assert.Equal(3, cannot);
        Assert.Contains("could not be started", a.Err.ToString());
        Assert.Contains("Start Baton", a.Err.ToString());

        var (silent, b) = await Execute(["list"], _ => null, canStart: true);
        Assert.Equal(3, silent);
        Assert.Contains("was started but did not answer", b.Err.ToString());
        Assert.DoesNotContain("could not be started", b.Err.ToString());
    }

    [Fact]
    public async Task B7_ASlowAnswer_SaysTheCommandMayStillRun_AndHowToCheck()
    {
        var run = new Run();
        var cli = new CliApp(run.Out, run.Err, new StringReader(""), _ => throw new OperationCanceledException(), () => Task.FromResult(true), "1.2.3", "C:\\work");
        Assert.Equal(1, await cli.RunAsync(["pause", "all"]));
        Assert.Contains("batonctl list", run.Err.ToString());
        Assert.Contains("busy", run.Err.ToString());
    }

    // ------------------------------------------------------------------ B8: the names a person must type are discoverable

    [Fact]
    public async Task B8_Queues_WithoutAWord_ListsTheQueueNames_AsTextAndAsJson()
    {
        ChannelReply App(ChannelMessage m) => m.Type == Messages.Queues ? new ChannelReply(true, Queues: ["Main", "Night"]) : new ChannelReply(false, Error: "no");
        var text = new Run();
        Assert.Equal(0, await text.ExecuteAsync(["queues"], App));
        Assert.Equal(["Main", "Night"], text.Out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Origins.Cli, text.Sent.Single().Origin);
        var json = new Run();
        Assert.Equal(0, await json.ExecuteAsync(["queues", "--json"], App));
        using var doc = json.OutJson();
        Assert.Equal(["Main", "Night"], doc.RootElement.GetProperty("queues").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task B8_Categories_ListsTheCategoryNames_AsTextAndAsJson()
    {
        ChannelReply App(ChannelMessage m) => m.Type == Messages.Categories ? new ChannelReply(true, Categories: ["General", "Music"]) : new ChannelReply(false, Error: "no");
        var text = new Run();
        Assert.Equal(0, await text.ExecuteAsync(["categories"], App));
        Assert.Equal(["General", "Music"], text.Out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        var json = new Run();
        Assert.Equal(0, await json.ExecuteAsync(["categories", "--json"], App));
        using var doc = json.OutJson();
        Assert.Equal(["General", "Music"], doc.RootElement.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2, await new Run().ExecuteAsync(["categories", "extra"], App));
    }

    // ------------------------------------------------------------------ B1: docs/CLI.md describes the JSON each command really prints

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Baton.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    /// <summary>The rows of the "JSON answers" table of docs/CLI.md: the command (first cell) and the fields of the answer (second cell).</summary>
    private static Dictionary<string, string[]> DocumentedShapes()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "CLI.md"));
        var start = Array.FindIndex(lines, l => l.StartsWith("## JSON answers", StringComparison.Ordinal));
        Assert.True(start >= 0, "docs/CLI.md has no \"## JSON answers\" section");
        var shapes = new Dictionary<string, string[]>();
        foreach (var line in lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)).Where(l => l.StartsWith("| `", StringComparison.Ordinal)))
        {
            // The first cell may hold an escaped bar (pause\|resume): take it as the text between the first two backticks.
            var row = System.Text.RegularExpressions.Regex.Match(line, @"^\| `(?<key>[^`]+)` \| (?<fields>.*) \|$");
            Assert.True(row.Success, $"unreadable row: {line}");
            var fields = System.Text.RegularExpressions.Regex.Matches(row.Groups["fields"].Value, "`([A-Za-z]+)`").Select(m => m.Groups[1].Value).Order().ToArray();
            shapes[row.Groups["key"].Value.Replace("\\|", "|")] = fields;
        }
        return shapes;
    }

    private static DownloadInfo Info() => new(5, "f.zip", "https://a.test/f.zip", "C:/x", null, "queued", 0, null, null, null, null, DateTime.UtcNow, null);

    [Fact]
    public async Task B1_TheJsonFieldsInTheDocs_AreTheOnesEachCommandPrints()
    {
        var shapes = DocumentedShapes();
        var cases = new (string Key, string[] Args, ChannelReply Reply)[]
        {
            ("add", ["add", "https://a.test/f.zip"], new ChannelReply(true, Result: "added", Id: 1)),
            ("list", ["list"], new ChannelReply(true, Downloads: [Info()])),
            ("status <id>", ["status", "5"], new ChannelReply(true, Download: Info(), Id: 5)),
            ("probe <url>", ["probe", "https://a.test/f.zip"], new ChannelReply(true, Probe: new ProbeInfo("f.zip", null, 1, true))),
            ("pause|resume|retry <id>", ["pause", "5"], new ChannelReply(true, Id: 5)),
            ("pause|resume|retry all", ["pause", "all"], new ChannelReply(true, Count: 2)),
            ("open <id>", ["open", "5"], new ChannelReply(true, Download: Info(), Id: 5)),
            ("remove <id>", ["remove", "5"], new ChannelReply(true, Id: 5)),
            ("remove --all-completed", ["remove", "--all-completed"], new ChannelReply(true, Count: 2)),
            ("queues start|stop", ["queues", "start"], new ChannelReply(true)),
            ("queues", ["queues"], new ChannelReply(true, Queues: ["Main"])),
            ("categories", ["categories"], new ChannelReply(true, Categories: ["General"])),
            ("version", ["version"], new ChannelReply(true, AppVersion: "1.4.2")),
            ("help", ["help"], new ChannelReply(true)),
            ("error", ["list"], new ChannelReply(false, Error: "no")),
        };
        foreach (var (key, args, reply) in cases)
        {
            var run = new Run();
            await run.ExecuteAsync([.. args, "--json"], _ => reply);
            using var doc = run.OutJson();
            var actual = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.True(shapes.TryGetValue(key, out var documented), $"docs/CLI.md does not describe the JSON of \"{key}\"");
            Assert.True(documented!.SequenceEqual(actual), $"\"{key}\": the docs say [{string.Join(", ", documented)}], batonctl prints [{string.Join(", ", actual)}]");
        }
        // The one error that carries a code (an app too old for the command), described next to the plain error.
        Assert.True(shapes.TryGetValue("error (appTooOld)", out var coded));
        Assert.Equal(["code", "error", "ok"], coded);
    }

    // ------------------------------------------------------------------ B10: remove never deletes a finished file; an unfinished download loses only its temporary files

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task B10_Remove_FromTheCommandLine_KeepsFinishedFiles_AndClearsOnlyTheTemporaryFilesOfAnUnfinishedDownload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var server = await TestFileServer.StartAsync();
        try
        {
            using var store = new Baton.Core.DownloadStore(Path.Combine(dir, "state.db"));
            store.SaveSettings(new Baton.Core.AppSettings { DownloadFolder = dir, MaxConnections = 4, ProxyMode = Baton.Core.ProxyMode.None });
            await using var service = new Baton.Core.DownloadService(store, engineTemplate: new Baton.Core.EngineOptions
            {
                MinSegmentSize = 64 * 1024, RetryBaseDelay = TimeSpan.FromMilliseconds(50), ReadTimeout = TimeSpan.FromSeconds(10),
                CheckpointInterval = TimeSpan.FromMilliseconds(100),
            });
            var control = new ControlHandler(service, () => AutomationLevel.Off, () => AutomationLevel.Off, _ => { }, "9.9.9");

            var doneId = service.Add(server.Add("done.bin", new ServedFile { Content = new byte[1000] }));
            await Until(() => service.GetSnapshot(doneId)!.Status == Baton.Core.DownloadStatus.Completed);
            var donePath = service.GetSnapshot(doneId)!.FinalPath!;
            var slowId = service.Add(server.Add("slow.bin", new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 31), Throttle = _ => 64 * 1024 }));
            await Until(() => Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories).Length == 1);

            // A finished download: gone from the list, its file stays.
            Assert.True((await control.HandleAsync(new ChannelMessage(Messages.Remove, Origin: Origins.Cli, Id: doneId))).Ok);
            Assert.Null(service.GetSnapshot(doneId));
            Assert.True(File.Exists(donePath));

            // "all completed" never touches an unfinished download or its partial file.
            var all = await control.HandleAsync(new ChannelMessage(Messages.Remove, Origin: Origins.Cli, All: true));
            Assert.Equal(0, all.Count);
            Assert.NotNull(service.GetSnapshot(slowId));
            Assert.Single(Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories));

            // An unfinished one removed by number from the command line: only its temporary files go (the same call from MCP or the API is refused, A8).
            Assert.True((await control.HandleAsync(new ChannelMessage(Messages.Remove, Origin: Origins.Cli, Id: slowId))).Ok);
            Assert.Null(service.GetSnapshot(slowId));
            Assert.Empty(Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories));
            Assert.True(File.Exists(donePath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void B10_TheDocs_SayWhatRemoveDoesToFiles()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "CLI.md"));
        Assert.Contains("never deletes a finished file", text);
        Assert.Contains("temporary files", text);
        Assert.DoesNotContain("it never deletes files from\nthe disk", text.Replace("\r\n", "\n"));
        Assert.Contains("no confirmation", text);              // the mass commands (pause all, remove --all-completed) ask nothing: documented
    }
}
