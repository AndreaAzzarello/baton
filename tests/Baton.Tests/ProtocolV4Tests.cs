using System.Text.Json;
using System.Xml.Linq;
using Baton.Core;
using Baton.Core.Duplicates;
using Baton.Core.Integration;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Protocol 4: permissions per origin, validation of the new messages, the control handler, and the signed notification buttons.</summary>
public sealed class ProtocolV4Tests : IAsyncLifetime
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

    private DownloadStore Store()
    {
        var store = new DownloadStore(Path.Combine(_dir, "state.db"));
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None });
        return store;
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

    private static readonly NotificationLinks Links = new(new byte[32]);

    // ------------------------------------------------------------------ permissions per origin

    private static string? Check(string type, string? origin, AutomationLevel mcp = AutomationLevel.Off, AutomationLevel api = AutomationLevel.Off) =>
        OriginPolicy.Check(new ChannelMessage(type, Origin: origin), mcp, api);

    [Theory]
    [InlineData(Messages.List)]
    [InlineData(Messages.Remove)]
    [InlineData(Messages.Pause)]
    [InlineData(Messages.Open)]
    [InlineData(Messages.QueueStop)]
    [InlineData(Messages.Version)]
    public void TheBrowser_NeverReachesTheNewTypes(string type)
    {
        Assert.NotNull(Check(type, null));
        Assert.NotNull(Check(type, Origins.Browser));
    }

    [Theory]
    [InlineData(Messages.Ping)]
    [InlineData(Messages.Add)]
    [InlineData(Messages.Hello)]
    [InlineData(Messages.Batch)]
    [InlineData(Messages.Grab)]
    public void TheBrowser_KeepsTheOldTypes(string type) => Assert.Null(Check(type, null));

    [Fact]
    public void TheCommandLine_CanDoEverythingNew_ButNotTheBrowsersPages()
    {
        foreach (var type in new[] { Messages.List, Messages.Status, Messages.Add, Messages.Pause, Messages.Resume, Messages.Retry, Messages.Remove, Messages.Open, Messages.QueueStart, Messages.QueueStop, Messages.Version })
            Assert.Null(Check(type, Origins.Cli));
        Assert.NotNull(Check(Messages.Batch, Origins.Cli));
        Assert.NotNull(Check(Messages.Grab, Origins.Cli));
    }

    [Fact]
    public void McpAndApi_FollowTheirLevel_Separately()
    {
        Assert.NotNull(Check(Messages.List, Origins.Mcp));                                          // off by default
        Assert.Null(Check(Messages.List, Origins.Mcp, mcp: AutomationLevel.ReadOnly));
        Assert.NotNull(Check(Messages.Add, Origins.Mcp, mcp: AutomationLevel.ReadOnly));
        Assert.Null(Check(Messages.Add, Origins.Mcp, mcp: AutomationLevel.ReadAdd));
        Assert.NotNull(Check(Messages.Pause, Origins.Mcp, mcp: AutomationLevel.ReadAdd));
        Assert.Null(Check(Messages.Pause, Origins.Mcp, mcp: AutomationLevel.Extended));
        Assert.NotNull(Check(Messages.Pause, Origins.Api, mcp: AutomationLevel.Extended));         // the API's own level counts
        Assert.NotNull(Check(Messages.Batch, Origins.Api, api: AutomationLevel.Extended));         // browser-only pages never
    }

    [Fact]
    public void AnUnknownOrigin_AndTheClipboard_AreRefused()
    {
        Assert.NotNull(Check(Messages.List, "root"));
        Assert.NotNull(Check(Messages.List, Origins.Clipboard));
    }

    [Fact]
    public async Task TheNativeHost_ForcesTheBrowserOrigin_SoTheExtensionCannotClaimTheCommandLine()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new ChannelMessage(Messages.Remove, Origin: Origins.Cli, Id: 1, All: true), Messages.Json);
        using var input = new MemoryStream();
        await NativeHost.WriteMessageAsync(input, json);
        input.Position = 0;
        using var output = new MemoryStream();

        await NativeHost.RunAsync(input, output, "Baton.test." + Guid.NewGuid().ToString("N"), () => false, TimeSpan.FromMilliseconds(10));

        output.Position = 0;
        var reply = JsonSerializer.Deserialize<ChannelReply>((await NativeHost.ReadMessageAsync(output))!, Messages.Json)!;
        Assert.False(reply.Ok);
        Assert.Contains("browser", reply.Error);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Validator_AcceptsTheNewMessages_AndRefusesTheMalformedOnes()
    {
        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.List, Origin: Origins.Cli)));
        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Pause, Origin: Origins.Cli, All: true)));
        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Status, Origin: Origins.Cli, Id: 7)));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Status, Origin: Origins.Cli)));           // no number
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Remove, Origin: Origins.Cli)));           // no number, not "all"
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Status, Origin: Origins.Cli, Id: -3)));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.List, Origin: "evil")));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.QueueStart, Queue: new string('x', 5000))));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Link, Link: "https://example.com/")));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Link)));
        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Link, Link: "baton:duplicateremove?p=a&d=1&e=2&s=x")));
    }

    [Fact]
    public void Validator_TheExtraAddOptionsAreNotForTheBrowser_AndAFolderMustBeAFullPath()
    {
        var folder = Path.Combine(_dir, "out");
        var fromBrowser = new ChannelMessage(Messages.Add, Download: new BrowserDownload("https://example.com/a.zip", Folder: folder));
        Assert.NotNull(MessageValidator.Validate(fromBrowser));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add, Download: new BrowserDownload("https://example.com/a.zip", AllowDuplicate: true))));

        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Add, Origin: Origins.Cli, Download: new BrowserDownload("https://example.com/a.zip", Folder: folder, Start: false))));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add, Origin: Origins.Cli, Download: new BrowserDownload("https://example.com/a.zip", Folder: "relative\\dir"))));
    }

    [Fact]
    public void ProtocolV3Messages_StillValidate()
    {
        var json = """{"type":"add","version":3,"download":{"url":"https://example.com/a.zip","fileName":"a.zip"}}""";
        var message = JsonSerializer.Deserialize<ChannelMessage>(json, Messages.Json)!;
        Assert.Null(message.Origin);
        Assert.Null(MessageValidator.Validate(message));
        Assert.Null(OriginPolicy.Check(message, AutomationLevel.Off, AutomationLevel.Off));
    }

    // ------------------------------------------------------------------ the control handler

    private ControlHandler Handler(DownloadService service, List<string>? opened = null, AutomationLevel mcp = AutomationLevel.Off) =>
        new(service, () => mcp, () => AutomationLevel.Off, p => opened?.Add(p), "9.9.9");

    private static ChannelMessage Cli(string type, long? id = null, bool? all = null, BrowserDownload? download = null, string? queue = null) =>
        new(type, Download: download, Origin: Origins.Cli, Id: id, All: all, Queue: queue);

    [Fact]
    public async Task Control_AddListStatusOpenRemove_AndTheFileStaysOnTheDisk()
    {
        var url = _server.Add("ctl.bin", new ServedFile { Content = TestFileServer.RandomBytes(120_000, 3) });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var opened = new List<string>();
        var control = Handler(service, opened);

        var added = await control.HandleAsync(Cli(Messages.Add, download: new BrowserDownload(url + "?token=secret")));
        Assert.True(added.Ok);
        Assert.Equal("added", added.Result);
        var id = added.Id!.Value;
        await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        var list = await control.HandleAsync(Cli(Messages.List));
        var info = Assert.Single(list.Downloads!);
        Assert.DoesNotContain("secret", info.Url);                       // no query string: it may hold a token
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(list, Messages.Json));
        Assert.Equal("completed", info.Status);
        Assert.Equal("completed", (await control.HandleAsync(Cli(Messages.Status, id))).Download!.Status);
        Assert.Equal("9.9.9", (await control.HandleAsync(Cli(Messages.Version))).AppVersion);

        Assert.True((await control.HandleAsync(Cli(Messages.Open, id))).Ok);
        Assert.Equal(service.GetSnapshot(id)!.FinalPath, Assert.Single(opened));

        var path = service.GetSnapshot(id)!.FinalPath!;
        Assert.True((await control.HandleAsync(Cli(Messages.Remove, id))).Ok);
        Assert.Empty(service.GetSnapshots());
        Assert.True(File.Exists(path));                                  // removed from the list only
        Assert.False((await control.HandleAsync(Cli(Messages.Status, id))).Ok);
    }

    [Fact]
    public async Task Control_AddWithStartFalse_AddsPaused_AndItStartsOnlyWhenResumed()
    {
        var url = _server.Add("paused.bin", new ServedFile { Content = TestFileServer.RandomBytes(60_000, 9) });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service);

        var added = await control.HandleAsync(Cli(Messages.Add, download: new BrowserDownload(url, Start: false)));
        var id = added.Id!.Value;
        await Task.Delay(500); // a queued download would have started by now
        Assert.Equal(DownloadStatus.Paused, service.GetSnapshot(id)!.Status);

        Assert.True((await control.HandleAsync(Cli(Messages.Resume, id))).Ok);
        await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
    }

    [Fact]
    public async Task Control_RemoveAllCompleted_KeepsTheFiles_AndAnAddTwiceIsReportedAsADuplicate()
    {
        var url = _server.Add("twice.bin", new ServedFile { Content = TestFileServer.RandomBytes(90_000, 4) });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service);
        var first = await control.HandleAsync(Cli(Messages.Add, download: new BrowserDownload(url)));
        await WaitFor(() => service.GetSnapshot(first.Id!.Value)!.Status == DownloadStatus.Completed);

        var second = await control.HandleAsync(Cli(Messages.Add, download: new BrowserDownload(url)));
        Assert.True(second.Ok);
        Assert.Equal("duplicate", second.Result);
        Assert.Equal(first.Id, second.Duplicate!.ExistingId);
        Assert.Equal("sameUrl", second.Duplicate.Reason); // a stable code, not the enum's display name
        Assert.Equal("sameName", ControlHandler.ReasonCode(DuplicateReason.SameName));
        Assert.Equal("sameFile", ControlHandler.ReasonCode(DuplicateReason.SameFile));
        var forced = await control.HandleAsync(Cli(Messages.Add, download: new BrowserDownload(url, AllowDuplicate: true)));
        Assert.Equal("added", forced.Result);
        await WaitFor(() => service.GetSnapshots().All(s => s.Status == DownloadStatus.Completed));

        var removed = await control.HandleAsync(Cli(Messages.Remove, all: true));
        Assert.Equal(2, removed.Count);
        Assert.Empty(service.GetSnapshots());
        Assert.Equal(2, Directory.GetFiles(_dir, "twice*.bin", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Control_Open_OfAProgramOrScript_IsOnlyForTheCommandLine_AndAFailureIsAnErrorReply()
    {
        var url = _server.Add("tool.exe", new ServedFile { Content = TestFileServer.RandomBytes(40_000, 11) });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var opened = new List<string>();
        var control = Handler(service, opened, mcp: AutomationLevel.Extended);
        var id = (await control.HandleAsync(Cli(Messages.Add, download: new BrowserDownload(url)))).Id!.Value;
        await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        var viaMcp = await control.HandleAsync(new ChannelMessage(Messages.Open, Origin: Origins.Mcp, Id: id));
        Assert.False(viaMcp.Ok);
        Assert.Empty(opened);                                              // never started for an automation client
        Assert.True((await control.HandleAsync(Cli(Messages.Open, id))).Ok);
        Assert.Single(opened);

        // A file type with no program: an error reply, not a connection closed without an answer (the CLI would start Baton and resend).
        var failing = new ControlHandler(service, () => AutomationLevel.Off, () => AutomationLevel.Off,
            _ => throw new System.ComponentModel.Win32Exception("No application is associated with the file."), "9.9.9");
        var failed = await failing.HandleAsync(Cli(Messages.Open, id));
        Assert.False(failed.Ok);
        Assert.Contains("could not do that", failed.Error);
    }

    [Fact]
    public async Task Control_McpAndApi_CannotChooseAFolder_AndAreRefusedWhenOff()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service, mcp: AutomationLevel.ReadAdd);
        var mcpAdd = new ChannelMessage(Messages.Add, Origin: Origins.Mcp, Download: new BrowserDownload("https://example.com/a.zip", Folder: Path.Combine(_dir, "x")));

        Assert.Null(control.Authorize(mcpAdd));                          // the level allows adding...
        var reply = await control.HandleAsync(mcpAdd);                   // ...but not into a folder of its choice
        Assert.False(reply.Ok);
        Assert.Empty(service.GetSnapshots());

        Assert.NotNull(control.Authorize(new ChannelMessage(Messages.Remove, Origin: Origins.Mcp, All: true)));
        Assert.NotNull(control.Authorize(new ChannelMessage(Messages.List, Origin: Origins.Api)));
    }

    [Fact]
    public async Task Control_UnknownNumbersAndQueues_AreErrors_NotCrashes()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var control = Handler(service);

        Assert.False((await control.HandleAsync(Cli(Messages.Pause, 999))).Ok);
        Assert.False((await control.HandleAsync(Cli(Messages.Retry, 999))).Ok);
        Assert.False((await control.HandleAsync(Cli(Messages.Open, 999))).Ok);
        Assert.False((await control.HandleAsync(Cli(Messages.QueueStart, queue: "nope"))).Ok);
        Assert.True((await control.HandleAsync(Cli(Messages.QueueStart))).Ok);   // the main queue
        Assert.True((await control.HandleAsync(Cli(Messages.QueueStop))).Ok);
    }

    // ------------------------------------------------------------------ the notification buttons

    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static string[] ButtonLinks(string toastXml) =>
        XElement.Parse(toastXml).Descendants("action").Select(a => (string)a.Attribute("arguments")!).ToArray();

    [Fact]
    public void NewLinks_AreSigned_BoundToTheDownload_AndExpire()
    {
        var link = Links.Create(NotificationAction.DuplicateDownload, @"C:\x\a.bin", 5, Now.AddDays(1));
        Assert.True(Links.TryParse(link, Now, out var action, out var path, out var id));
        Assert.Equal((NotificationAction.DuplicateDownload, @"C:\x\a.bin", 5L), (action, path, id));

        Assert.False(Links.TryParse(link.Replace("d=5", "d=6"), Now, out _, out _, out _));             // another download's id
        Assert.False(Links.TryParse(link.Replace("duplicatedownload", "duplicateremove"), Now, out _, out _, out _)); // another action
        Assert.False(Links.TryParse(link[..^2] + "AA", Now, out _, out _, out _));                      // bad signature
        Assert.False(Links.TryParse(link, Now.AddDays(2), out _, out _, out _));                        // expired
        Assert.False(new NotificationLinks(new byte[32].Select(_ => (byte)1).ToArray()).TryParse(link, Now, out _, out _, out _)); // another user's secret
    }

    [Fact]
    public void DuplicateToast_HasThreeButtons_NeverOverwrite_AndOpenOnlyIfTheFileIsThere()
    {
        var existing = Path.Combine(_dir, "there.bin");
        File.WriteAllText(existing, "x");
        var notice = new DuplicateNotice(7, "new.bin", new DuplicateMatch(DuplicateReason.SameFile, 3, existing));

        var buttons = ButtonLinks(ActionToasts.BuildDuplicate(notice, Links, Now));

        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, b => Assert.True(Links.TryParse(b, Now, out _, out _, out var id) && id == 7));
        var actions = buttons.Select(b => { Links.TryParse(b, Now, out var a, out _, out _); return a; }).ToArray();
        Assert.Equal([NotificationAction.DuplicateDownload, NotificationAction.DuplicateOpen, NotificationAction.DuplicateRemove], actions);

        var missing = new DuplicateNotice(7, "new.bin", new DuplicateMatch(DuplicateReason.SameFile, null, Path.Combine(_dir, "gone.bin")));
        Assert.Equal(2, ButtonLinks(ActionToasts.BuildDuplicate(missing, Links, Now)).Length);
    }

    [Fact]
    public void ClipboardToast_NeverCarriesTheAddress_OnlyAShortLivedReference()
    {
        var offers = new ClipboardOffers();
        const string url = "https://files.example.com/private/report.pdf?key=SECRET";
        var reference = offers.Add(url, Now);

        var xml = ActionToasts.BuildClipboard("report.pdf", "files.example.com", reference, Links, Now);

        Assert.DoesNotContain("SECRET", xml);
        Assert.DoesNotContain("private", xml);
        Assert.DoesNotContain("example.com/", xml.Replace("files.example.com</text>", ""));
        var buttons = ButtonLinks(xml);
        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, b => Assert.False(Convert.FromBase64String(Pad(b.Split("p=")[1].Split('&')[0])).Length > 40));
        Assert.True(Links.TryParse(buttons[0], Now, out var action, out var path, out _));
        Assert.Equal(NotificationAction.ClipboardDownload, action);
        Assert.Equal(reference, path);
        Assert.False(Links.TryParse(buttons[0], Now + ActionToasts.ClipboardLifetime + TimeSpan.FromSeconds(1), out _, out _, out _));
    }

    private static string Pad(string s) { s = s.Replace('-', '+').Replace('_', '/'); return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='); }

    [Fact]
    public void ClipboardOffers_ExpireAreConsumedOnce_AndUnknownReferencesFail()
    {
        var offers = new ClipboardOffers(TimeSpan.FromMinutes(10));
        var reference = offers.Add("https://example.com/a.zip", Now);

        Assert.False(offers.TryTake("wrong", Now, out _));
        Assert.True(offers.TryTake(reference, Now.AddMinutes(1), out var url));
        Assert.Equal("https://example.com/a.zip", url);
        Assert.False(offers.TryTake(reference, Now.AddMinutes(2), out _));                  // consumed

        var old = offers.Add("https://example.com/b.zip", Now);
        Assert.False(offers.TryTake(old, Now.AddMinutes(11), out _));                       // expired
    }

    [Fact]
    public void IgnoredSites_MatchTheSiteAndItsSubdomains_NothingElse()
    {
        var list = ClipboardLinks.WithIgnored("", "Example.com");
        list = ClipboardLinks.WithIgnored(list, "example.com");
        Assert.Equal("example.com", list);
        Assert.True(ClipboardLinks.IsIgnored("example.com", list));
        Assert.True(ClipboardLinks.IsIgnored("files.example.com", list));
        Assert.False(ClipboardLinks.IsIgnored("notexample.com", list));
        Assert.False(ClipboardLinks.IsIgnored("example.org", list));
    }

    [Fact]
    public async Task LinkActions_ActOnlyOnASignedLink_AndOnADownloadStillWaiting()
    {
        var content = TestFileServer.RandomBytes(200_000, 11);
        var first = _server.Add("la1.bin", new ServedFile { Content = content });
        var second = _server.Add("la2.bin", new ServedFile { Content = content });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var firstId = service.Add(first);
        await WaitFor(() => service.GetSnapshot(firstId)!.Status == DownloadStatus.Completed);
        await service.ClearCompletedAsync();
        DuplicateNotice? notice = null;
        service.DuplicateFound += n => notice = n;
        var id = service.Add(second);
        await WaitFor(() => notice is not null);

        var opened = new List<string>();
        var offered = new List<string>();
        var muted = new List<string>();
        var offers = new ClipboardOffers();
        var actions = new LinkActions(service, Links, offers, opened.Add, offered.Add, muted.Add);
        var expires = service.UtcNow.AddDays(1);

        Assert.False((await actions.HandleAsync("baton:duplicatedownload?p=x&d=1&e=1&s=bad")).Ok);        // forged
        Assert.False((await actions.HandleAsync(Links.Create(NotificationAction.DuplicateRemove, notice!.Match.ExistingPath, id + 1, expires))).Ok); // no such download
        Assert.False((await actions.HandleAsync(Links.Create(NotificationAction.Open, "C:\\x", id, expires))).Ok);  // not an app action
        Assert.Equal(DownloadStatus.Paused, service.GetSnapshot(id)!.Status);

        Assert.True((await actions.HandleAsync(Links.Create(NotificationAction.DuplicateDownload, notice.Match.ExistingPath, id, expires))).Ok);
        await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
        // Used once: the download is not waiting any more, so an old "Remove" leaves it alone (it is not an expired link).
        Assert.True((await actions.HandleAsync(Links.Create(NotificationAction.DuplicateRemove, notice.Match.ExistingPath, id, expires))).Ok);
        Assert.Equal(DownloadStatus.Completed, service.GetSnapshot(id)!.Status);
        // Gone from the list: now it is expired.
        await service.RemoveAsync(id, deleteFile: false);
        Assert.False((await actions.HandleAsync(Links.Create(NotificationAction.DuplicateRemove, notice.Match.ExistingPath, id, expires))).Ok);

        var reference = offers.Add("https://shop.example.com/a.zip", service.UtcNow);
        Assert.True((await actions.HandleAsync(Links.Create(NotificationAction.ClipboardDownload, reference, 0, expires))).Ok);
        Assert.Equal(["https://shop.example.com/a.zip"], offered);
        Assert.False((await actions.HandleAsync(Links.Create(NotificationAction.ClipboardDownload, reference, 0, expires))).Ok);   // reference gone

        var again = offers.Add("https://shop.example.com/b.zip", service.UtcNow);
        Assert.True((await actions.HandleAsync(Links.Create(NotificationAction.ClipboardMute, again, 0, expires))).Ok);
        Assert.Equal(["shop.example.com"], muted);
        var third = offers.Add("https://x.example.com/c.zip", service.UtcNow);
        Assert.True((await actions.HandleAsync(Links.Create(NotificationAction.ClipboardIgnore, third, 0, expires))).Ok);
        Assert.False(offers.TryTake(third, service.UtcNow, out _));
    }

    // ------------------------------------------------------------------ buttons with the app closed

    /// <summary>A duplicate stopped at start and left waiting in the list; the "app" is then closed. Returns its id and the existing file.</summary>
    private async Task<(long Id, string ExistingPath)> WaitingDuplicateThenCloseAppAsync()
    {
        var content = TestFileServer.RandomBytes(200_000, 12);
        var first = _server.Add("cl1.bin", new ServedFile { Content = content });
        var second = _server.Add("cl2.bin", new ServedFile { Content = content });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var firstId = service.Add(first);
        await WaitFor(() => service.GetSnapshot(firstId)!.Status == DownloadStatus.Completed);
        await service.ClearCompletedAsync();
        DuplicateNotice? notice = null;
        service.DuplicateFound += n => notice = n;
        var id = service.Add(second);
        await WaitFor(() => notice is not null);
        return (id, notice!.Match.ExistingPath);
    }

    /// <summary>The closed app: sending finds no pipe; "starting" it opens the same database in a new service and serves the pipe.</summary>
    private sealed class SimulatedApp(ProtocolV4Tests test, string pipe) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private DownloadStore? _store;
        public DownloadService? Service { get; private set; }
        public int Starts { get; private set; }

        public bool Start()
        {
            Starts++;
            _store = test.Store();
            Service = new DownloadService(_store, engineTemplate: Fast, autoTick: false);
            var actions = new LinkActions(Service, Links, new ClipboardOffers(), _ => { }, _ => { }, _ => { });
            _ = AppChannel.ServeAsync(pipe, message => actions.HandleAsync(message.Link!), _stop.Token);
            return true;
        }

        public Task<ChannelReply?> Send(ChannelMessage message) => AppChannel.SendAsync(pipe, message, TimeSpan.FromMilliseconds(300));

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (Service is not null)
                await Service.DisposeAsync();
            _store?.Dispose();
        }
    }

    [Fact]
    public async Task DuplicateButtons_WithTheAppClosed_StartItAndActOnAValidLink()
    {
        var (id, existing) = await WaitingDuplicateThenCloseAppAsync();
        var expires = DateTime.UtcNow.AddDays(1);
        await using var app = new SimulatedApp(this, "baton-test-" + Guid.NewGuid().ToString("N"));

        var link = Links.Create(NotificationAction.DuplicateDownload, existing, id, expires);
        var outcome = await LinkLauncher.ForwardAsync(NotificationAction.DuplicateDownload, link, app.Send, app.Start, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));

        Assert.Equal(LinkOutcome.Done, outcome);
        Assert.Equal(1, app.Starts);
        await WaitFor(() => app.Service!.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        // A second button of the same toast finds the app running: no new start, the settled download is left alone.
        var remove = Links.Create(NotificationAction.DuplicateRemove, existing, id, expires);
        Assert.Equal(LinkOutcome.Done, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateRemove, remove, app.Send, app.Start, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, app.Starts);
        Assert.NotNull(app.Service!.GetSnapshot(id));
    }

    [Fact]
    public async Task RemoveButton_WithTheAppClosed_RemovesTheWaitingDuplicate()
    {
        var (id, existing) = await WaitingDuplicateThenCloseAppAsync();
        await using var app = new SimulatedApp(this, "baton-test-" + Guid.NewGuid().ToString("N"));
        var link = Links.Create(NotificationAction.DuplicateRemove, existing, id, DateTime.UtcNow.AddDays(1));

        Assert.Equal(LinkOutcome.Done, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateRemove, link, app.Send, app.Start, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50)));
        Assert.Null(app.Service!.GetSnapshot(id));
        Assert.True(File.Exists(existing));   // the file already there is never touched
    }

    [Fact]
    public async Task ButtonsWithTheAppClosed_AnswerExpiredOnlyForAnInvalidLinkOrAGoneDownload()
    {
        var (id, existing) = await WaitingDuplicateThenCloseAppAsync();
        var pipe = "baton-test-" + Guid.NewGuid().ToString("N");

        // Bad signature and expired: the app starts, refuses the link, the download stays waiting.
        await using (var app = new SimulatedApp(this, pipe))
        {
            Assert.Equal(LinkOutcome.Expired, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateDownload,
                "baton:duplicatedownload?p=x&d=" + id + "&e=9999999999&s=bad", app.Send, app.Start, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50)));
            var old = Links.Create(NotificationAction.DuplicateDownload, existing, id, DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(LinkOutcome.Expired, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateDownload, old, app.Send, app.Start, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)));
            Assert.Equal(DownloadStatus.Paused, app.Service!.GetSnapshot(id)!.Status);
            // The user removes it in the window; the button is then for a download that is gone.
            await app.Service.RemoveAsync(id, deleteFile: false);
            var valid = Links.Create(NotificationAction.DuplicateDownload, existing, id, DateTime.UtcNow.AddDays(1));
            Assert.Equal(LinkOutcome.Expired, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateDownload, valid, app.Send, app.Start, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)));
        }
    }

    [Fact]
    public async Task ButtonsWithTheAppClosed_ReportAnAppThatDoesNotStart_AndLetCopiedLinkOffersLapse()
    {
        var dead = new SimulatedApp(this, "baton-test-" + Guid.NewGuid().ToString("N"));
        var starts = 0;
        var link = Links.Create(NotificationAction.DuplicateDownload, "C:\\x", 1, DateTime.UtcNow.AddDays(1));
        Assert.Equal(LinkOutcome.AppNotReachable, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateDownload, link, dead.Send, () => { starts++; return false; }, TimeSpan.FromSeconds(1)));
        Assert.Equal(LinkOutcome.AppNotReachable, await LinkLauncher.ForwardAsync(NotificationAction.DuplicateDownload, link, dead.Send, () => { starts++; return true; }, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50)));
        Assert.Equal(2, starts);

        // The offers of copied links live in memory: with the app closed they are gone, and nothing starts the app for them.
        starts = 0;
        foreach (var clipboard in new[] { NotificationAction.ClipboardDownload, NotificationAction.ClipboardIgnore, NotificationAction.ClipboardMute })
        {
            var offer = Links.Create(clipboard, "ref", 0, DateTime.UtcNow.AddDays(1));
            Assert.Equal(LinkOutcome.Done, await LinkLauncher.ForwardAsync(clipboard, offer, dead.Send, () => { starts++; return true; }, TimeSpan.FromSeconds(1)));
        }
        Assert.Equal(0, starts);
        await dead.DisposeAsync();
    }
}
