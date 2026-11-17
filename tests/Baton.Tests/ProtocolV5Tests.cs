using Baton.Core;
using Baton.Core.Integration;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Protocol 5 (MCP): probe, capabilities, a page of the list, and the app-side budget of MCP requests.</summary>
public sealed class ProtocolV5Tests : IAsyncLifetime
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

    private DownloadStore Store(bool allowLocal = false)
    {
        var store = new DownloadStore(Path.Combine(_dir, "state.db"));
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None, AllowLocalNetworkForAgents = allowLocal });
        return store;
    }

    private static ControlHandler Handler(DownloadService service, AutomationLevel mcp = AutomationLevel.Off, AutomationLevel api = AutomationLevel.Off,
        Func<DateTime>? clock = null) =>
        new(service, () => mcp, () => api, _ => { }, "9.9.9", clock);

    private static ChannelMessage Msg(string type, string? origin, string? url = null, int? limit = null, long? after = null,
        BrowserDownload? download = null) =>
        new(type, Origin: origin, Url: url, Limit: limit, After: after, Download: download);

    // ------------------------------------------------------------------ permissions

    private static string? Check(string type, string? origin, AutomationLevel level) =>
        OriginPolicy.Check(new ChannelMessage(type, Origin: origin), origin == Origins.Mcp ? level : AutomationLevel.Off,
            origin == Origins.Api ? level : AutomationLevel.Off);

    [Fact]
    public void Probe_NeedsReadAndAdd_ForMcpAndTheApi_ButNotForTheCommandLine()
    {
        foreach (var origin in new[] { Origins.Mcp, Origins.Api })
        {
            Assert.NotNull(Check(Messages.Probe, origin, AutomationLevel.Off));
            Assert.NotNull(Check(Messages.Probe, origin, AutomationLevel.ReadOnly)); // a probe is a request to an address the caller chose
            Assert.Null(Check(Messages.Probe, origin, AutomationLevel.ReadAdd));
            Assert.Null(Check(Messages.Probe, origin, AutomationLevel.Extended));
        }
        Assert.Null(Check(Messages.Probe, Origins.Cli, AutomationLevel.Off));
        Assert.NotNull(Check(Messages.Probe, Origins.Browser, AutomationLevel.Extended));
        Assert.NotNull(Check(Messages.Probe, null, AutomationLevel.Extended));
    }

    [Fact]
    public void Capabilities_IsAnsweredAtEveryLevel_ToMcpAndTheApiAndTheCommandLine_ButNeverToTheBrowser()
    {
        foreach (var level in Enum.GetValues<AutomationLevel>())
        {
            Assert.Null(Check(Messages.Capabilities, Origins.Mcp, level));
            Assert.Null(Check(Messages.Capabilities, Origins.Api, level));
        }
        Assert.Null(Check(Messages.Capabilities, Origins.Cli, AutomationLevel.Off));
        Assert.NotNull(Check(Messages.Capabilities, Origins.Browser, AutomationLevel.Extended));
        Assert.NotNull(Check(Messages.Capabilities, null, AutomationLevel.Extended));
        // Everything else stays refused at "off": the exception is for this one type.
        Assert.NotNull(Check(Messages.List, Origins.Mcp, AutomationLevel.Off));
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Validator_AcceptsTheNewTypes_AndRefusesBadPagesAndAddresses()
    {
        Assert.Null(MessageValidator.Validate(Msg(Messages.Capabilities, Origins.Mcp)));
        Assert.Null(MessageValidator.Validate(Msg(Messages.Probe, Origins.Mcp, "https://example.com/a.zip")));
        Assert.NotNull(MessageValidator.Validate(Msg(Messages.Probe, Origins.Mcp)));
        Assert.NotNull(MessageValidator.Validate(Msg(Messages.Probe, Origins.Mcp, "file:///c:/windows/win.ini")));
        Assert.NotNull(MessageValidator.Validate(Msg(Messages.Probe, Origins.Mcp, "not an address")));
        Assert.Null(MessageValidator.Validate(Msg(Messages.List, Origins.Mcp, limit: 200, after: 1)));
        Assert.NotNull(MessageValidator.Validate(Msg(Messages.List, Origins.Mcp, limit: 0)));
        Assert.NotNull(MessageValidator.Validate(Msg(Messages.List, Origins.Mcp, limit: 201)));
        Assert.NotNull(MessageValidator.Validate(Msg(Messages.List, Origins.Mcp, after: 0)));
        Assert.Equal(5, Messages.ProtocolVersion);
    }

    [Fact]
    public void Messages_RoundTripThroughTheSourceGeneratedJson_WithTheNewFields()
    {
        var message = Msg(Messages.List, Origins.Mcp, limit: 10, after: 7);
        var back = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.ChannelMessage),
            ProtocolJson.Default.ChannelMessage)!;
        Assert.Equal((10, 7L), (back.Limit, back.After));
        var reply = new ChannelReply(true, Probe: new ProbeInfo("a.zip", "application/zip", 12, true), Level: "readAdd", NextCursor: 9);
        var replyBack = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(reply, ProtocolJson.Default.ChannelReply),
            ProtocolJson.Default.ChannelReply)!;
        Assert.Equal(new ProbeInfo("a.zip", "application/zip", 12, true), replyBack.Probe);
        Assert.Equal(("readAdd", 9L), (replyBack.Level, replyBack.NextCursor));
    }

    // ------------------------------------------------------------------ capabilities

    [Theory]
    [InlineData(AutomationLevel.Off, "off")]
    [InlineData(AutomationLevel.ReadOnly, "readOnly")]
    [InlineData(AutomationLevel.ReadAdd, "readAdd")]
    [InlineData(AutomationLevel.Extended, "extended")]
    public async Task Capabilities_ReportsTheLevelOfTheCallersOrigin(AutomationLevel level, string name)
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service, mcp: level, api: AutomationLevel.ReadOnly);
        Assert.Equal(name, (await control.HandleAsync(Msg(Messages.Capabilities, Origins.Mcp))).Level);
        Assert.Equal("readOnly", (await control.HandleAsync(Msg(Messages.Capabilities, Origins.Api))).Level);
        Assert.Equal("extended", (await control.HandleAsync(Msg(Messages.Capabilities, Origins.Cli))).Level);
        Assert.Equal(Messages.ProtocolVersion, (await control.HandleAsync(Msg(Messages.Capabilities, Origins.Mcp))).Protocol);
    }

    // ------------------------------------------------------------------ stable error codes (A3)

    [Fact]
    public async Task EveryRefusal_CarriesAStableCode_AndTheWireNameIsCamelCase()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service, mcp: AutomationLevel.Extended, api: AutomationLevel.ReadOnly);

        Assert.Equal(ReplyCode.NoDownload, (await control.HandleAsync(new ChannelMessage(Messages.Status, Origin: Origins.Cli, Id: 99))).Code);
        Assert.Equal(ReplyCode.MissingId, (await control.HandleAsync(new ChannelMessage(Messages.Status, Origin: Origins.Cli))).Code);
        Assert.Equal(ReplyCode.NoQueue, (await control.HandleAsync(new ChannelMessage(Messages.QueueStart, Origin: Origins.Cli, Queue: "nope"))).Code);
        Assert.Equal(ReplyCode.BadScheme, (await control.HandleAsync(Msg(Messages.Probe, Origins.Mcp, "gopher://example.com/x"))).Code);
        Assert.Equal(ReplyCode.PrivateNetwork, (await control.HandleAsync(Msg(Messages.Probe, Origins.Mcp, "http://192.168.1.1/x"))).Code);
        // A7: a name that does not resolve cannot be shown to be public: refused for agents (the command line is not restricted).
        Assert.Equal(ReplyCode.Unresolvable, (await control.HandleAsync(Msg(Messages.Probe, Origins.Mcp, "http://no-such-host.invalid/x"))).Code);
        Assert.Equal(ReplyCode.UnknownType, (await control.HandleAsync(new ChannelMessage("nonsense", Origin: Origins.Cli))).Code);
        Assert.Equal(ReplyCode.LevelTooLow, control.Authorize(Msg(Messages.Pause, Origins.Api))!.Code);
        Assert.Equal(ReplyCode.AutomationOff, Handler(service).Authorize(Msg(Messages.List, Origins.Mcp))!.Code);
        Assert.Equal(ReplyCode.NotAllowed, control.Authorize(Msg(Messages.List, Origins.Browser))!.Code);
        Assert.Equal(ReplyCode.CliOnly, control.Authorize(Msg(Messages.Open, Origins.Mcp))!.Code);
        Assert.Null((await control.HandleAsync(Msg(Messages.Capabilities, Origins.Mcp))).Code);

        var failed = new ChannelReply(false, Error: "x", Code: ReplyCode.NoDownload);
        var json = System.Text.Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(failed, ProtocolJson.Default.ChannelReply));
        Assert.Contains("\"code\":\"noDownload\"", json);
        // An older app never sends a code.
        var old = System.Text.Json.JsonSerializer.Deserialize("""{"ok":false,"error":"x"}"""u8, ProtocolJson.Default.ChannelReply)!;
        Assert.Null(old.Code);
    }

    [Fact]
    public async Task TheApiStatus_FollowsTheCode_NotTheWordsOfTheError()
    {
        // A rephrased message must not change the status: the same code, other words, gives the same answer.
        Assert.Equal(404, StatusOf(ReplyCode.NoDownload));
        Assert.Equal(404, StatusOf(ReplyCode.NoQueue));
        Assert.Equal(403, StatusOf(ReplyCode.PrivateNetwork));
        Assert.Equal(403, StatusOf(ReplyCode.LevelTooLow));
        Assert.Equal(403, StatusOf(ReplyCode.AutomationOff));
        Assert.Equal(400, StatusOf(ReplyCode.BadScheme));
        Assert.Equal(400, StatusOf(null));
        await Task.CompletedTask;
        static int StatusOf(ReplyCode? code) => Baton.Core.Integration.LocalApiServer.StatusFor(code);
    }

    // ------------------------------------------------------------------ the list in pages

    [Fact]
    public async Task List_WithALimit_ReturnsPagesInOrderOfNumber_AndWithoutOneReturnsEverythingAsBefore()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service, mcp: AutomationLevel.Extended);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var url = _server.Add($"page{i}.bin", new ServedFile { Content = TestFileServer.RandomBytes(2_000, i) });
            ids.Add((await control.HandleAsync(Msg(Messages.Add, Origins.Cli, download: new BrowserDownload(url, Start: false)))).Id!.Value);
        }

        var all = await control.HandleAsync(Msg(Messages.List, Origins.Mcp));
        Assert.Equal(5, all.Downloads!.Count);
        Assert.Null(all.NextCursor); // no limit: the whole list, as the command line and the API have always had it

        var first = await control.HandleAsync(Msg(Messages.List, Origins.Mcp, limit: 2));
        Assert.Equal(ids.Take(2), first.Downloads!.Select(d => d.Id));
        Assert.Equal(ids[1], first.NextCursor);
        var second = await control.HandleAsync(Msg(Messages.List, Origins.Mcp, limit: 2, after: first.NextCursor));
        Assert.Equal(ids.Skip(2).Take(2), second.Downloads!.Select(d => d.Id));
        var last = await control.HandleAsync(Msg(Messages.List, Origins.Mcp, limit: 2, after: second.NextCursor));
        Assert.Equal(ids.Skip(4), last.Downloads!.Select(d => d.Id));
        Assert.Null(last.NextCursor);

        // Past the end, or a cursor that names a number no longer there: an empty page, not an error.
        var beyond = await control.HandleAsync(Msg(Messages.List, Origins.Mcp, limit: 2, after: 999_999));
        Assert.True(beyond.Ok);
        Assert.Empty(beyond.Downloads!);
        Assert.Null(beyond.NextCursor);
    }

    // ------------------------------------------------------------------ the budget of MCP requests

    [Fact]
    public async Task Mcp_HasABudgetOfRequestsPerMinute_KeptInTheApp_AndAskingForTheLevelIsFree()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var now = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        var control = Handler(service, mcp: AutomationLevel.Extended, clock: () => now);
        var list = Msg(Messages.List, Origins.Mcp);

        for (var i = 0; i < ControlHandler.McpRequestsPerMinute; i++)
            Assert.Null(control.Authorize(list));
        var refused = control.Authorize(list);
        Assert.NotNull(refused);
        Assert.Equal(ControlHandler.TooManyRequestsError, refused!.Error);

        Assert.Null(control.Authorize(Msg(Messages.Capabilities, Origins.Mcp))); // does not use the budget, and is answered when it is spent
        Assert.Null(control.Authorize(Msg(Messages.List, Origins.Cli)));         // the command line has no budget

        now = now.AddSeconds(61);
        Assert.Null(control.Authorize(list));                                    // a minute later the window has moved on
    }

    // ------------------------------------------------------------------ probe

    [Fact]
    public async Task Probe_ReturnsNameTypeSizeAndRanges_ForTheCommandLine_AndNeverTheContent()
    {
        var content = TestFileServer.RandomBytes(150_000, 21);
        var url = _server.Add("probe-me.bin", new ServedFile { Content = content });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service);

        var reply = await control.HandleAsync(Msg(Messages.Probe, Origins.Cli, url));
        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("probe-me.bin", reply.Probe!.Name);
        Assert.Equal(content.Length, reply.Probe.Size);
        Assert.True(reply.Probe.Resumable);
        Assert.Empty(service.GetSnapshots()); // nothing was added
    }

    [Fact]
    public async Task Probe_ForMcp_RefusesThisComputerAndThePrivateNetwork_UnlessTheSettingAllowsIt()
    {
        var url = _server.Add("local.bin", new ServedFile { Content = TestFileServer.RandomBytes(10_000, 5) });
        using (var store = Store(allowLocal: false))
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
            var control = Handler(service, mcp: AutomationLevel.ReadAdd);
            var refused = await control.HandleAsync(Msg(Messages.Probe, Origins.Mcp, url));
            Assert.False(refused.Ok);
            Assert.Equal(ControlHandler.PrivateNetworkError, refused.Error);
            var scheme = await control.HandleAsync(Msg(Messages.Probe, Origins.Mcp, "ftps://example.com/a.bin"));
            Assert.False(scheme.Ok); // only http, https and ftp, as for "add"
            Assert.Contains("Only http", scheme.Error);
        }
        using (var store = Store(allowLocal: true))
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
            var control = Handler(service, mcp: AutomationLevel.ReadAdd);
            Assert.True((await control.HandleAsync(Msg(Messages.Probe, Origins.Mcp, url))).Ok);
        }
    }

    [Fact]
    public async Task Probe_OfAnAddressThatDoesNotAnswer_IsAnErrorReply_NotAnException()
    {
        using var store = Store(allowLocal: true);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service);
        var reply = await control.HandleAsync(Msg(Messages.Probe, Origins.Cli, "http://127.0.0.1:1/never.bin"));
        Assert.False(reply.Ok);
        Assert.StartsWith("The address did not answer", reply.Error);
    }
}
