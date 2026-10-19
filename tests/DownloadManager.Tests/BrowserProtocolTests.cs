using System.Diagnostics;
using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Platform;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>
/// Protocol 2 with the extension: strict validation, compatibility with 0.5, the connection check, Firefox,
/// downloads started by forms, "download all", the schema upgrade, and the extension's own tests (Node).
/// </summary>
public sealed class BrowserProtocolTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "dm-test-" + Guid.NewGuid().ToString("N");
    private string DbPath => Path.Combine(_dir, "state.db");

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
        var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None });
        return store;
    }

    private static async Task<DownloadSnapshot> Finished(DownloadService service, long id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (service.GetSnapshot(id)!.Status is not (DownloadStatus.Completed or DownloadStatus.Failed))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(30);
        }
        return service.GetSnapshot(id)!;
    }

    // ------------------------------------------------------------------ validation

    public static TheoryData<string, string> InvalidMessages => new()
    {
        { """{"type":"format-disk"}""", "Unknown message type" },
        { """{"type":"add"}""", "Missing download" },
        { """{"type":"add","download":{"url":"javascript:alert(1)"}}""", "Only http" },
        { """{"type":"add","download":{"url":"file:///C:/Windows/win.ini"}}""", "Only http" },
        { """{"type":"add","download":{"url":"https://a.test/x","fileName":"a\u0000b"}}""", "control characters" },
        { """{"type":"add","download":{"url":"https://a.test/x","referrer":"chrome://settings"}}""", "referrer" },
        { """{"type":"add","download":{"url":"https://a.test/x","method":"DELETE"}}""", "GET and POST" },
        { """{"type":"add","download":{"url":"https://a.test/x","body":"a=1"}}""", "only allowed with POST" },
        { """{"type":"add","download":{"url":"https://a.test/x","method":"POST","body":"a=1","bodyType":"application/json"}}""", "encoding" },
        { """{"type":"add","download":{"url":"ftp://a.test/x","method":"POST","body":"a=1","bodyType":"application/x-www-form-urlencoded"}}""", "http or https" },
        { """{"type":"add","download":{"url":"https://a.test/x","cookies":[{"name":"","value":"v","domain":"a.test","path":"/"}]}}""", "cookie" },
        { """{"type":"probeMedia","media":{"url":"file:///etc/passwd"}}""", "http or https" },
        { """{"type":"addMedia","media":{"url":"https://a.test/v.m3u8"}}""", "quality" },
        { """{"type":"batch","batch":{"pageUrl":"https://a.test/","items":[]}}""", "No links" },
        { """{"type":"batch","batch":{"pageUrl":"https://a.test/","items":[{"url":"https://a.test/f","kind":"script"}]}}""", "kind" },
        { """{"type":"ping","version":-3}""", "version" },
    };

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Validator_RefusesInvalidMessages(string json, string reason)
    {
        var message = JsonSerializer.Deserialize<ChannelMessage>(json, Messages.Json);
        var error = MessageValidator.Validate(message);
        Assert.NotNull(error);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RefusesOversizedInput()
    {
        var longUrl = "https://a.test/" + new string('x', MessageValidator.MaxUrlLength);
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add, new BrowserDownload(longUrl))));
        var cookies = Enumerable.Range(0, MessageValidator.MaxCookies + 1).Select(i => new BrowserCookie($"c{i}", "v", "a.test", "/", false, true)).ToList();
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add, new BrowserDownload("https://a.test/x", Cookies: cookies))));
        var items = Enumerable.Range(0, MessageValidator.MaxBatchItems + 1).Select(i => new BatchItem($"https://a.test/{i}")).ToList();
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Batch, Batch: new BatchRequest("https://a.test/", null, items))));
        var body = new string('a', MessageValidator.MaxBodyLength + 1);
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add,
            new BrowserDownload("https://a.test/x", Method: "POST", Body: body, BodyType: "application/x-www-form-urlencoded"))));
    }

    [Theory]
    // Exactly what extension 0.5 sends (protocol 1, no version): still accepted.
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"queues"}""")]
    [InlineData("""{"type":"add","download":{"url":"https://a.test/f.zip","fileName":"f.zip","referrer":"https://a.test/","userAgent":"UA","cookies":[{"name":"s","value":"1","domain":"a.test","path":"/","secure":true,"hostOnly":true}],"queue":"Notte"}}""")]
    [InlineData("""{"type":"add","download":{"url":"ftpes://user@ftp.test/f.iso"}}""")]
    // Protocol 2.
    [InlineData("""{"type":"hello","version":2}""")]
    [InlineData("""{"type":"add","version":2,"download":{"url":"https://a.test/export","method":"POST","body":"y=2026&f=csv","bodyType":"multipart/form-data"}}""")]
    [InlineData("""{"type":"probeMedia","version":2,"media":{"url":"https://cdn.test/master.m3u8","pageUrl":"https://a.test/watch","title":"Clip"}}""")]
    [InlineData("""{"type":"addMedia","version":2,"media":{"url":"https://cdn.test/master.m3u8","variantId":"hls:2","label":"720p"}}""")]
    [InlineData("""{"type":"batch","version":2,"batch":{"pageUrl":"https://a.test/","pageTitle":"Files","items":[{"url":"https://a.test/a.zip","text":"A","kind":"link"},{"url":"mailto:x@a.test"}]}}""")]
    [InlineData("""{"type":"add","version":7,"download":{"url":"https://a.test/x"},"newField":true}""")] // a newer extension: unknown fields are ignored
    public void Validator_AcceptsProtocol1And2(string json) =>
        Assert.Null(MessageValidator.Validate(JsonSerializer.Deserialize<ChannelMessage>(json, Messages.Json)));

    // ------------------------------------------------------------------ native host

    [Fact]
    public async Task Hello_IsAnsweredWithTheProtocol_WithoutStartingTheApp()
    {
        var launches = 0;
        var replies = await RunHostAsync([new ChannelMessage(Messages.Hello, Version: 2)], () => { launches++; return false; });
        Assert.True(replies[0].Ok);
        Assert.False(replies[0].Running);
        Assert.Equal(Messages.ProtocolVersion, replies[0].Protocol);
        Assert.Equal("9.9.9", replies[0].AppVersion);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task InvalidMessages_NeverReachOrStartTheApp()
    {
        var launches = 0;
        var replies = await RunHostAsync(
            [new ChannelMessage(Messages.Add, new BrowserDownload("javascript:alert(1)")), new ChannelMessage("shutdown")],
            () => { launches++; return false; });
        Assert.All(replies, r => Assert.False(r.Ok));
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task TheAppChannel_ValidatesToo_AndAnswersHello()
    {
        using var cts = new CancellationTokenSource();
        var handled = new List<string>();
        var server = AppChannel.ServeAsync(_pipe, m =>
        {
            handled.Add(m.Type);
            return Task.FromResult(new ChannelReply(true, Protocol: Messages.ProtocolVersion, AppVersion: "0.6.0"));
        }, cts.Token);

        // Any program of the user can open the pipe: it gets the same checks as the host.
        var refused = await AppChannel.SendAsync(_pipe, new ChannelMessage(Messages.Add, new BrowserDownload("file:///C:/x")), TimeSpan.FromSeconds(5));
        var hello = await AppChannel.SendAsync(_pipe, new ChannelMessage(Messages.Hello, Version: 2), TimeSpan.FromSeconds(5));

        Assert.False(refused!.Ok);
        Assert.Equal(Messages.ProtocolVersion, hello!.Protocol); // an extension speaking 2 learns the app speaks 3
        Assert.Equal([Messages.Hello], handled);
        cts.Cancel();
        await server;
    }

    [Theory]
    [InlineData(new[] { "chrome-extension://fngmjmgaglmoebphcjohnhlaceogneno/" }, true)]
    [InlineData(new[] { @"C:\Users\x\AppData\Local\DownloadManager\com.downloadmanager.host.firefox.json", "downloadmanager@extension.local" }, true)]
    [InlineData(new[] { @"C:\x\host.json", "someone-else@example.org" }, false)]
    [InlineData(new[] { "--tray" }, false)]
    public void HostInvocation_FromChromiumAndFirefox(string[] args, bool expected) =>
        Assert.Equal(expected, NativeHost.IsHostInvocation(args));

    [Fact]
    public void Registration_AlsoRegistersFirefox_InTheInjectedRegistryOnly()
    {
        var memory = new MemoryUserRegistry();
        var data = Path.Combine(_dir, "data");
        new SystemRegistration(memory, @"C:\Apps\DownloadManager.exe").RegisterBrowserHost(data);

        var firefoxManifest = memory.GetString($@"{SystemRegistration.FirefoxHostKey}\{NativeHost.Name}", null);
        Assert.Equal(Path.Combine(data, "com.downloadmanager.host.firefox.json"), firefoxManifest);
        using var json = JsonDocument.Parse(File.ReadAllText(firefoxManifest!));
        Assert.Equal(NativeHost.FirefoxExtensionId, json.RootElement.GetProperty("allowed_extensions")[0].GetString());
        Assert.Equal(@"C:\Apps\DownloadManager.exe", json.RootElement.GetProperty("path").GetString());
        Assert.False(json.RootElement.TryGetProperty("allowed_origins", out _)); // Firefox rejects Chromium's key
        Assert.NotNull(memory.GetString($@"{SystemRegistration.BrowserHostKeys[0]}\{NativeHost.Name}", null));
    }

    [Fact]
    public void TheFirefoxBuild_UsesTheIdTheHostAllows()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "build-extension.mjs"));
        Assert.Contains($"FIREFOX_ID = \"{NativeHost.FirefoxExtensionId}\"", script);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "extension", "manifest.json")));
        // The extension and the app are released together, with the same version.
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.App", "DownloadManager.App.csproj"));
        var appVersion = System.Text.RegularExpressions.Regex.Match(project, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(appVersion, manifest.RootElement.GetProperty("version").GetString());
    }

    private async Task<List<ChannelReply>> RunHostAsync(IEnumerable<ChannelMessage> messages, Func<bool> launchApp)
    {
        var input = new MemoryStream();
        foreach (var m in messages)
            await NativeHost.WriteMessageAsync(input, JsonSerializer.SerializeToUtf8Bytes(m, Messages.Json));
        input.Position = 0;
        var output = new MemoryStream();
        await NativeHost.RunAsync(input, output, _pipe, launchApp, TimeSpan.FromSeconds(2), "9.9.9");
        output.Position = 0;
        var replies = new List<ChannelReply>();
        while (await NativeHost.ReadMessageAsync(output) is { } json)
            replies.Add(JsonSerializer.Deserialize<ChannelReply>(json, Messages.Json)!);
        return replies;
    }

    // ------------------------------------------------------------------ forms (POST)

    [Fact]
    public async Task FormDownload_IsSentAsPost_OncePerAttempt_WithFieldsCookiesAndReferrer()
    {
        var content = TestFileServer.RandomBytes(400_000, 31);
        var url = _server.Add("export.csv", new ServedFile
        {
            Content = content,
            ContentDisposition = "attachment; filename=\"report-2026.csv\"",
            RequireForm = form => form["year"] == "2026" && form["q"] == "a b&c" && form["tag"].Count == 2,
            Authorize = r => r.Headers.Cookie.ToString().Contains("session=s1"),
        });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var download = new BrowserDownload(url, Referrer: "https://site.test/form", Method: "POST",
            Cookies: [new BrowserCookie("session", "s1", "127.0.0.1", "/", false, true)],
            Body: "year=2026&q=a+b%26c&tag=x&tag=y", BodyType: "application/x-www-form-urlencoded");
        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Add, download)));

        var done = await Finished(service, service.Add(download.Url, context: download.ToContext()));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal("report-2026.csv", Path.GetFileName(done.FinalPath));
        Assert.Equal(content, File.ReadAllBytes(done.FinalPath!));
        var requests = _server.Requests.Where(r => r.Path == "export.csv").ToList();
        Assert.Single(requests); // the probe's response was the download: the form was not sent twice
        Assert.Equal("POST", requests[0].Method);
        Assert.Equal("https://site.test/form", requests[0].Referer);
        Assert.Null(store.LoadAll().Single().Context); // the form data is not kept after completion
    }

    [Fact]
    public async Task MultipartForm_IsRepeatedAsMultipart()
    {
        var url = _server.Add("convert.bin", new ServedFile
        {
            Content = TestFileServer.RandomBytes(50_000, 32),
            RequireForm = form => form["format"] == "zip" && form["name"] == "Relazione finale",
        });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var context = new RequestContext(null, null, []) { Method = "POST", Body = "format=zip&name=Relazione+finale", BodyType = "multipart/form-data" };

        var done = await Finished(service, service.Add(url, context: context));

        Assert.Equal(DownloadStatus.Completed, done.Status);
    }

    [Fact]
    public async Task FormDownload_InterruptedMidway_IsSubmittedAgainFromTheStart()
    {
        var content = TestFileServer.RandomBytes(300_000, 33);
        var url = _server.Add("flaky.bin", new ServedFile
        {
            Content = content,
            FailAfterBytes = 100_000,
            FailRequests = 1,
            RequireForm = form => form["id"] == "7",
        });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var context = new RequestContext(null, null, []) { Method = "POST", Body = "id=7", BodyType = "application/x-www-form-urlencoded" };

        var done = await Finished(service, service.Add(url, context: context));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(content, File.ReadAllBytes(done.FinalPath!));
        Assert.All(_server.Requests.Where(r => r.Path == "flaky.bin"), r => Assert.Equal("POST", r.Method));
        Assert.Equal(2, _server.Requests.Count(r => r.Path == "flaky.bin"));
    }

    // ------------------------------------------------------------------ download all

    [Fact]
    public void BatchLinks_KeepDownloadableAddressesOnce_AndTickFiles()
    {
        var links = BatchLinks.Build([
            new BatchItem("https://a.test/files/setup.exe", " Installer  "),
            new BatchItem("https://a.test/files/setup.exe#top", "duplicate"),
            new BatchItem("https://a.test/about.html", "About"),
            new BatchItem("https://a.test/", "Home"),
            new BatchItem("mailto:info@a.test", "Mail"),
            new BatchItem("javascript:void(0)", "Menu"),
            new BatchItem("https://cdn.a.test/img/photo.JPG", "Photo", "image"),
            new BatchItem("ftp://ftp.a.test/pub/data.tar.gz", "Data"),
            new BatchItem("https://a.test/get.php?id=4", "Get"),
        ]);

        Assert.Equal(6, links.Count);
        Assert.Equal(["setup.exe", "about.html", "a.test", "photo.JPG", "data.tar.gz", "get.php"], links.Select(l => l.Name));
        Assert.Equal([true, false, false, true, true, false], links.Select(l => l.Preselected));
        Assert.Equal("Installer", links[0].Text);
        Assert.True(links.Single(l => l.Name == "photo.JPG").IsImage);
        Assert.Equal("jpg", links.Single(l => l.IsImage).Extension);

        Assert.Equal([("exe", 1), ("gz", 1), ("html", 1), ("jpg", 1), ("php", 1)], BatchLinks.Extensions(links));
        Assert.Equal(["photo.JPG"], links.Where(l => BatchLinks.Matches(l, 2, null, null)).Select(l => l.Name));
        Assert.Equal(["setup.exe"], links.Where(l => BatchLinks.Matches(l, 0, "exe", null)).Select(l => l.Name));
        Assert.Equal(["data.tar.gz"], links.Where(l => BatchLinks.Matches(l, 1, null, "DATA")).Select(l => l.Name));
    }

    // ------------------------------------------------------------------ schema 5

    [Fact]
    public void Migration_FromVersion4_AddsTheMediaColumn_WithoutLosingDownloads()
    {
        using (var store = new DownloadStore(DbPath))
        {
            store.Insert(new DownloadRecord { Url = "https://example.com/a.zip", Directory = _dir, AddedUtc = DateTime.UtcNow, QueueId = 1 });
            store.Insert(new DownloadRecord { Url = "ftp://example.com/b.iso", Directory = _dir, AddedUtc = DateTime.UtcNow, QueueId = 1 });
        }
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            // As 0.5 left it: no media column, version 4.
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "ALTER TABLE downloads DROP COLUMN media; PRAGMA user_version = 4;";
            cmd.ExecuteNonQuery();
        }

        using var upgraded = new DownloadStore(DbPath);
        Assert.Equal(DownloadStore.SchemaVersion, upgraded.Version);
        Assert.EndsWith(".v4.bak", upgraded.BackupPath);
        Assert.Equal(2, upgraded.LoadAll().Count);
        Assert.All(upgraded.LoadAll(), r => Assert.Null(r.Media));

        var stream = new DownloadRecord
        {
            Url = "https://cdn.test/master.m3u8", Directory = _dir, AddedUtc = DateTime.UtcNow, QueueId = 1, FileName = "Clip.mp4",
            Media = new MediaSource { Kind = "hls", VariantId = "hls:2", Label = "720p" },
        };
        upgraded.Insert(stream);
        var loaded = upgraded.LoadAll().Single(r => r.Id == stream.Id);
        Assert.Equal(("hls", "hls:2", "720p"), (loaded.Media!.Kind, loaded.Media.VariantId, loaded.Media.Label));
    }

    [Fact]
    public void FfmpegSetting_RoundTrips_AndTheOldDefaultUserAgentFollowsTheVersion()
    {
        foreach (var (saved, expected) in new[]
                 {
                     ("DownloadManager/0.5", AppSettings.DefaultUserAgent), ("DownloadManager/1.0", AppSettings.DefaultUserAgent),
                     ("DownloadManager/1.0 (custom)", "DownloadManager/1.0 (custom)"), ("Mozilla/5.0", "Mozilla/5.0"),
                 })
        {
            using (var store = new DownloadStore(DbPath))
                store.SaveSettings(new AppSettings { FfmpegPath = @"D:\tools\ffmpeg.exe", UserAgent = saved });
            using var reopened = new DownloadStore(DbPath);
            var settings = reopened.LoadSettings();
            Assert.Equal(@"D:\tools\ffmpeg.exe", settings.FfmpegPath);
            Assert.Equal(expected, settings.UserAgent); // an earlier default follows the version, the user's own value stays
        }
    }

    // ------------------------------------------------------------------ the extension's own tests

    [Fact]
    public async Task ExtensionTests_PassInNode()
    {
        var root = RepoRoot();
        var node = FindOnPath("node.exe");
        if (node is null)
        {
            Assert.Fail("Node.js is needed for the extension's tests (node --test tests/extension).");
            return;
        }
        var info = new ProcessStartInfo(node) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "--test", "--test-reporter=tap", "tests/extension/*.test.mjs" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        var (stdout, stderr) = (await output, await errors);
        Assert.True(process.ExitCode == 0, stdout + stderr);
        Assert.Contains("# fail 0", stdout);
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), exe))
            .FirstOrDefault(File.Exists);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DownloadManager.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
