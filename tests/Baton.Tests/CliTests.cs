using System.Text.Json;
using Baton.Cli;
using Baton.Core;
using Baton.Core.Integration;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>batonctl: the arguments, the exit codes, and the commands against the real handler through a real pipe.</summary>
public sealed class CliTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        ReadTimeout = TimeSpan.FromSeconds(10),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    public async Task InitializeAsync()
    {
        _server = await TestFileServer.StartAsync();
        Directory.CreateDirectory(_dir);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ arguments

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("list", "--nope")]
    [InlineData("--folder", "x")]
    [InlineData("add", "--folder")]
    [InlineData("add", "--allow-duplicate=yes")]
    public void Parser_RejectsWhatItDoesNotKnow(params string[] args) =>
        Assert.NotNull(ArgParser.Parse(args).Error);

    [Fact]
    public void Parser_ReadsCommandWordsAndOptionsInAnyOrder()
    {
        var parsed = ArgParser.Parse(["--json", "add", "https://a.test/x.zip", "--folder=D:\\Dl", "-", "--category", "Video", "--no-start"]);
        Assert.Null(parsed.Error);
        Assert.True(parsed.Json);
        Assert.Equal("add", parsed.Command);
        Assert.Equal(["https://a.test/x.zip", "-"], parsed.Words);
        Assert.Equal("D:\\Dl", parsed.Value("folder"));
        Assert.Equal("Video", parsed.Value("category"));
        Assert.True(parsed.Has("no-start"));
        Assert.Equal("help", ArgParser.Parse([]).Command);
        Assert.Equal("help", ArgParser.Parse(["list", "--help"]).Command);
    }

    // ------------------------------------------------------------------ exit codes, with a pretend app

    private sealed class Run
    {
        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();
        public List<ChannelMessage> Sent { get; } = [];
        public int Starts { get; private set; }

        public async Task<int> ExecuteAsync(string[] args, Func<ChannelMessage, ChannelReply?>? app, bool canStart = false, string input = "", bool closed = false, bool italian = false)
        {
            var running = app is not null && !closed;
            var cli = new CliApp(Out, Err, new StringReader(input), message =>
            {
                Sent.Add(message);
                return Task.FromResult(running ? app!(message) : null);
            }, () =>
            {
                Starts++;
                running = canStart && app is not null;
                return Task.FromResult(canStart);
            }, "1.2.3", "C:\\work", italian);
            return await cli.RunAsync(args);
        }
    }

    [Fact]
    public async Task AppUnreachable_IsExitCode3_AndTheAppIsStartedOnlyWhenTheCommandNeedsIt()
    {
        var run = new Run();
        Assert.Equal(3, await run.ExecuteAsync(["list"], null, canStart: false));
        Assert.Equal(1, run.Starts);

        var started = new Run();
        Assert.Equal(3, await started.ExecuteAsync(["list"], null, canStart: true));   // started, but it never answers

        var version = new Run();
        Assert.Equal(0, await version.ExecuteAsync(["version"], null));                  // a question: never starts the app
        Assert.Equal(0, version.Starts);
        Assert.Contains("batonctl 1.2.3", version.Out.ToString());
        Assert.Contains("not running", version.Out.ToString());
    }

    [Fact]
    public async Task ClosedApp_IsStarted_ThenTheMessageIsSentAgain()
    {
        var run = new Run();
        Assert.Equal(0, await run.ExecuteAsync(["list"], _ => new ChannelReply(true, Downloads: []), canStart: true, closed: true));
        Assert.Equal(1, run.Starts);
        Assert.Equal(2, run.Sent.Count);
        Assert.All(run.Sent, m => Assert.Equal(Origins.Cli, m.Origin));
    }

    [Fact]
    public async Task Add_ChecksTheAddressesFirst_AndReportsDuplicates_AsExitCode4()
    {
        var bad = new Run();
        Assert.Equal(2, await bad.ExecuteAsync(["add", "https://a.test/f.zip", "not a link"], _ => new ChannelReply(true)));
        Assert.Empty(bad.Sent);                                                           // nothing was sent
        Assert.Equal(2, await new Run().ExecuteAsync(["add"], _ => new ChannelReply(true)));
        Assert.Equal(2, await new Run().ExecuteAsync(["add", "https://a.test/f.zip", "--start", "--no-start"], _ => new ChannelReply(true)));

        var duplicate = new Run();
        var code = await duplicate.ExecuteAsync(["add", "https://a.test/f.zip", "--json"],
            _ => new ChannelReply(true, Result: "duplicate", Duplicate: new DuplicateInfo("sameUrl", 7, "C:\\x\\f.zip")));
        Assert.Equal(4, code);
        using var json = JsonDocument.Parse(duplicate.Out.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        var result = json.RootElement.GetProperty("results")[0];
        Assert.Equal("duplicate", result.GetProperty("status").GetString());
        Assert.Equal(7, result.GetProperty("id").GetInt64());
        Assert.Equal("sameUrl", result.GetProperty("duplicate").GetProperty("reason").GetString()); // the stable code, never translated

        Assert.Equal(1, await new Run().ExecuteAsync(["add", "https://a.test/f.zip"], _ => new ChannelReply(false, Error: "No category with this name.")));
    }

    [Theory]
    [InlineData("pause", "#5")]
    [InlineData("resume", "5")]
    [InlineData("retry", "#5")]
    [InlineData("remove", "#5")]
    public async Task NumberedCommands_AcceptAHashBeforeTheNumber(string command, string number)
    {
        var run = new Run();
        Assert.Equal(0, await run.ExecuteAsync([command, number], _ => new ChannelReply(true, Id: 5)));
        Assert.Equal(5, Assert.Single(run.Sent).Id);
    }

    [Fact]
    public async Task AReplyThatDoesNotComeInTime_IsAnError_NotNotRunning_AndIsNotSentTwice()
    {
        var sent = 0;
        var cli = new CliApp(new StringWriter(), new StringWriter(), new StringReader(""), _ => { sent++; throw new OperationCanceledException(); },
            () => Task.FromResult(true), "1.2.3", "C:\\work");
        Assert.Equal(1, await cli.RunAsync(["pause", "all"]));
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData("sameUrl", "skipped (duplicate: same address already in the list, #7)", "saltato (duplicato: stesso indirizzo già in elenco, #7)")]
    [InlineData("sameName", "skipped (duplicate: a file with the same name is already in the folder, #7)", "saltato (duplicato: nella cartella c'è già un file con lo stesso nome, #7)")]
    [InlineData("sameFile", "skipped (duplicate: same file from another address (same size and version), #7)", "saltato (duplicato: stesso file da un altro indirizzo (stessa dimensione e versione), #7)")]
    public async Task DuplicateMessage_IsReadable_InEnglishAndItalian_AndJsonKeepsTheCode(string code, string english, string italian)
    {
        ChannelReply App(ChannelMessage _) => new(true, Result: "duplicate", Duplicate: new DuplicateInfo(code, 7, "C:\\x\\f.zip"));
        var en = new Run();
        Assert.Equal(4, await en.ExecuteAsync(["add", "https://a.test/f.zip"], App));
        Assert.Contains(english + "  https://a.test/f.zip", en.Err.ToString());
        var it = new Run();
        Assert.Equal(4, await it.ExecuteAsync(["add", "https://a.test/f.zip"], App, italian: true));
        Assert.Contains(italian + "  https://a.test/f.zip", it.Err.ToString());
        var json = new Run();
        await json.ExecuteAsync(["add", "https://a.test/f.zip", "--json"], App, italian: true);
        Assert.Contains($"\"reason\":\"{code}\"", json.Out.ToString().Replace(" ", ""));
    }

    [Fact]
    public async Task DuplicateMessage_WithACodeFromANewerBaton_ShowsTheCodeAsItIs()
    {
        var run = new Run();
        await run.ExecuteAsync(["add", "https://a.test/f.zip"], _ => new ChannelReply(true, Result: "duplicate", Duplicate: new DuplicateInfo("somethingNew", null, "")));
        Assert.Contains("skipped (duplicate: somethingNew)", run.Err.ToString());
    }

    [Fact]
    public async Task Add_SendsTheOptions_ReadsAddressesFromTheInput_AndMakesTheFolderAbsolute()
    {
        var run = new Run();
        var code = await run.ExecuteAsync(["add", "-", "https://c.test/3.zip", "--folder", "dl", "--category", "Video", "--queue", "Night", "--no-start", "--allow-duplicate"],
            _ => new ChannelReply(true, Result: "added", Id: 5), input: "https://a.test/1.zip\n\n  https://b.test/2.zip  \n");
        Assert.Equal(0, code);
        Assert.Equal(["https://a.test/1.zip", "https://b.test/2.zip", "https://c.test/3.zip"], run.Sent.Select(m => m.Download!.Url));
        var sent = run.Sent[0].Download!;
        Assert.Equal("C:\\work\\dl", sent.Folder);
        Assert.Equal("Video", sent.Category);
        Assert.Equal("Night", sent.Queue);
        Assert.False(sent.Start);
        Assert.True(sent.AllowDuplicate);
        Assert.Equal("added #5  https://a.test/1.zip", run.Out.ToString().Split(Environment.NewLine)[0]);
    }

    [Fact]
    public async Task CommandsThatNeedANumber_RefuseAnythingElse()
    {
        foreach (var args in new[] { new[] { "status" }, ["status", "abc"], ["status", "0"], ["pause"], ["pause", "1", "2"], ["open", "-5"], ["remove"], ["remove", "--all-completed", "3"],
                     ["queues"], ["queues", "pause"], ["list", "x"], ["list", "--status", "weird"] })
            Assert.Equal(2, await new Run().ExecuteAsync(args, _ => new ChannelReply(true)));
        var run = new Run();
        Assert.Equal(0, await run.ExecuteAsync(["pause", "ALL"], _ => new ChannelReply(true, Count: 3)));
        Assert.True(run.Sent.Single().All);
        Assert.Contains("3 download(s)", run.Out.ToString());
    }

    [Fact]
    public async Task Json_IsOneObjectWithTheSameFieldsEveryTime()
    {
        var run = new Run();
        await run.ExecuteAsync(["list", "--json"], _ => new ChannelReply(true, Downloads: []));
        using var empty = JsonDocument.Parse(run.Out.ToString());
        Assert.True(empty.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, empty.RootElement.GetProperty("downloads").GetArrayLength());

        var failed = new Run();
        Assert.Equal(2, await failed.ExecuteAsync(["status", "--json"], _ => new ChannelReply(true)));
        using var error = JsonDocument.Parse(failed.Out.ToString());
        Assert.False(error.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(error.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task AnAppOlderThan14_IsExplained_NotJustRefused()
    {
        var run = new Run();
        // What 1.3 answers to a type it does not know: an error and no protocol number.
        Assert.Equal(1, await run.ExecuteAsync(["list"], _ => new ChannelReply(false, Error: "Unknown message type: list")));
        Assert.Contains("older than 1.4", run.Err.ToString());
        var version = new Run();
        Assert.Equal(0, await version.ExecuteAsync(["version"], _ => new ChannelReply(false, Error: "Unknown message type: version")));
        Assert.Contains("older than 1.4", version.Out.ToString());
    }

    [Fact]
    public void PipeName_FollowsThePortableFlag_ExactlyLikeTheApp()
    {
        if (PipeNames.SandboxFolder() is not null)
            return;                                          // a sandboxed run names its pipe after its data folder, tested by the trials
        Assert.Equal(ChannelClient.DefaultPipeName, PipeNames.For(_dir));
        File.WriteAllText(Path.Combine(_dir, PipeNames.PortableFlag), "");
        var portable = PipeNames.For(_dir);
        Assert.StartsWith(ChannelClient.DefaultPipeName + ".", portable);
        Assert.Equal(portable, PipeNames.For(_dir.ToUpperInvariant()));  // the copy's data folder is hashed in lower case
    }

    // ------------------------------------------------------------------ the real handler through a real pipe

    [Fact]
    public async Task EveryCommand_WorksAgainstTheRealHandlerThroughThePipe()
    {
        var url = _server.Add("cli.bin", new ServedFile { Content = TestFileServer.RandomBytes(150_000, 5) });
        using var store = new DownloadStore(Path.Combine(_dir, "state.db"));
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var opened = new List<string>();
        var control = new ControlHandler(service, () => AutomationLevel.Off, () => AutomationLevel.Off, opened.Add, "9.9.9");
        var pipe = "baton-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = AppChannel.ServeAsync(pipe, async m => control.Authorize(m) ?? await control.HandleAsync(m), stop.Token);

        async Task<(int Code, string Out, string Err)> Cli(params string[] args)
        {
            var output = new StringWriter();
            var err = new StringWriter();
            var app = new CliApp(output, err, new StringReader(""), m => ChannelClient.SendAsync(pipe, m, TimeSpan.FromSeconds(5)),
                () => Task.FromResult(false), "1.2.3", _dir);
            return (await app.RunAsync(args), output.ToString(), err.ToString());
        }

        try
        {
            var version = await Cli("version", "--json");
            using (var doc = JsonDocument.Parse(version.Out))
                Assert.Equal("9.9.9", doc.RootElement.GetProperty("app").GetString());

            // Paused into a folder of the command line's choosing.
            var folder = Path.Combine(_dir, "chosen");
            var add = await Cli("add", url, "--folder", folder, "--no-start", "--json");
            Assert.Equal(0, add.Code);
            long id;
            using (var doc = JsonDocument.Parse(add.Out))
                id = doc.RootElement.GetProperty("results")[0].GetProperty("id").GetInt64();
            Assert.Equal(folder, service.GetSnapshot(id)!.Directory);

            var again = await Cli("add", url, "--folder", folder, "--no-start");        // same address: skipped
            Assert.Equal(4, again.Code);

            var resume = await Cli("resume", id.ToString());
            Assert.Equal(0, resume.Code);
            await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

            var listed = await Cli("list", "--status", "completed", "--json");
            using (var doc = JsonDocument.Parse(listed.Out))
                Assert.Equal(id, doc.RootElement.GetProperty("downloads")[0].GetProperty("id").GetInt64());
            Assert.Contains("completed", (await Cli("status", id.ToString())).Out);

            Assert.Equal(0, (await Cli("open", id.ToString())).Code);
            Assert.Single(opened);
            Assert.Equal(0, (await Cli("queues", "stop")).Code);
            Assert.Equal(1, (await Cli("queues", "start", "Nope")).Code);                 // refused by the app: exit 1
            Assert.Equal(1, (await Cli("status", "9999")).Code);

            Assert.Equal(0, (await Cli("remove", id.ToString())).Code);
            Assert.Null(service.GetSnapshot(id));
            Assert.True(File.Exists(Path.Combine(folder, "cli.bin")));                    // only the list entry goes
        }
        finally
        {
            stop.Cancel();
            await server;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(30);
        }
    }
}
