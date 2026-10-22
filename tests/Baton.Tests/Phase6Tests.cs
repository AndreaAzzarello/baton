using System.Diagnostics;
using System.Net;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.PostDownload;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>
/// Phase 6: checks after the download (hash, antivirus with a simulated scanner), the grabber's synchronization after
/// failed or removed downloads, the speed limit on offline copies, project cookies, rotating logs and disk errors.
/// </summary>
public sealed class Phase6Tests : IAsyncLifetime
{
    private GrabberTestSite _site = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private DownloadStore _store = null!;
    private DownloadService _downloads = null!;
    private GrabberService _grabber = null!;
    private HttpClient _http = null!;

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(20),
        MaxRetries = 1,
        ReadTimeout = TimeSpan.FromSeconds(10),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public async Task InitializeAsync()
    {
        _site = await GrabberTestSite.StartAsync();
        Directory.CreateDirectory(_dir);
        _store = new DownloadStore(Path.Combine(_dir, "state.db"));
        _store.SaveSettings(new AppSettings { DownloadFolder = Path.Combine(_dir, "downloads"), ProxyMode = ProxyMode.None });
        _http = _site.CreateClient(new CookieContainer());
        _downloads = new DownloadService(_store, _http, Fast, autoTick: false);
        await _downloads.StopQueueAsync(); // nothing starts by itself: the tests start what they need
        _grabber = new GrabberService(_downloads, _store, null, (_, cookies) => _site.CreateClient(cookies),
            new GrabberEngineOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(20) }, autoTick: false);
    }

    public async Task DisposeAsync()
    {
        await _grabber.DisposeAsync();
        await _downloads.DisposeAsync();
        _store.Dispose();
        _http.Dispose();
        await _site.DisposeAsync();
        Log.Disable();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<DownloadSnapshot> Finished(long id, Func<DownloadSnapshot, bool>? done = null)
    {
        done ??= s => s.Status is DownloadStatus.Completed or DownloadStatus.Failed && s.Check is not { Pending: true };
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (_downloads.GetSnapshot(id) is { } snapshot && done(snapshot))
                return snapshot;
            await Task.Delay(50);
        }
        throw new TimeoutException("The download did not finish.");
    }

    private void UpdateSettings(Action<AppSettings> change)
    {
        var settings = _downloads.Settings;
        change(settings);
        _downloads.UpdateSettings(settings);
    }

    // ------------------------------------------------------------------ hash

    [Theory]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "MD5")]
    [InlineData("SHA-1: da39a3ee5e6b4b0d3255bfef95601890afd80709", "SHA1")]
    [InlineData("sha256:E3B0C442 98FC1C14 9AFBF4C8 996FB924 27AE41E4 649B934C A495991B 7852B855", "SHA256")]
    public void Hash_IsRecognizedFromWhatUsersPaste(string text, string algorithm)
    {
        Assert.True(FileHash.TryParse(text, out var parsed, out var hex));
        Assert.Equal(algorithm, parsed);
        Assert.Equal(hex.ToLowerInvariant(), hex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("md5:da39a3ee5e6b4b0d3255bfef95601890afd80709")] // SHA-1 length with an MD5 label
    [InlineData("1234")]
    public void Hash_Garbage_IsRefused(string text) => Assert.False(FileHash.TryParse(text, out _, out _));

    [Fact]
    public async Task ExpectedHash_IsVerifiedAtTheEnd_BothWays()
    {
        var content = SiteResource.File(150_000, seed: 3);
        _site.Set("files.test", "/good.bin", content);
        _site.Set("files.test", "/bad.bin", SiteResource.File(150_000, seed: 3));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.Body));
        DownloadSnapshot? announced = null;
        _downloads.DownloadCompleted += s => { if (s.Url.EndsWith("good.bin")) announced = s; };

        var good = await Finished(_downloads.Add("http://files.test/good.bin", expectedHash: "sha256:" + sha));
        var bad = await Finished(_downloads.Add("http://files.test/bad.bin", expectedHash: new string('0', 64)));

        Assert.True(good.Check!.HashMatches);
        Assert.Equal(sha, good.Check.ComputedHash);
        Assert.False(bad.Check!.HashMatches);
        Assert.True(bad.Check.HasProblem);
        Assert.NotNull(announced); // the completion is announced with the outcome
        Assert.True(announced!.Check!.HashMatches);
        Assert.Throws<ArgumentException>(() => _downloads.Add("http://files.test/x.bin", expectedHash: "nope"));
    }

    [Fact]
    public async Task HashOnDemand_AndExpectedHashSetLater()
    {
        _site.Set("files.test", "/later.bin", SiteResource.File(70_000, seed: 4));
        var id = _downloads.Add("http://files.test/later.bin");
        var done = await Finished(id);
        Assert.Null(done.Check);

        var md5 = await _downloads.ComputeHashAsync(id, "MD5");
        Assert.Equal(32, md5!.Length);
        _downloads.SetExpectedHash(id, md5.ToUpperInvariant());
        var verified = await Finished(id, s => s.Check is { Pending: false, HashMatches: not null });
        Assert.True(verified.Check!.HashMatches);
    }

    [Fact]
    public void ScanOutcome_IsDescribedTheSameWay_ShortOrWithTheScannersDetail()
    {
        var threat = new PostCheck { Scan = ScanOutcome.Threat, ScanDetail = "Trojan:Win32/Test" };
        Assert.Null(PostCheck.DescribeScan(new PostCheck(), withDetail: true));
        Assert.Contains("Trojan:Win32/Test", PostCheck.DescribeScan(threat, withDetail: true));
        Assert.DoesNotContain("Trojan", PostCheck.DescribeScan(threat, withDetail: false));
        Assert.Equal(PostCheck.DescribeScan(threat, withDetail: false), PostCheck.Describe(threat));
        var failed = new PostCheck { Scan = ScanOutcome.Failed, ScanDetail = "exit 7" };
        Assert.Equal(PostCheck.DescribeScan(failed, true), PostCheck.DescribeScan(failed, false));
    }

    [Fact]
    public async Task Hash_IsComputed_WhileAnotherProgramHasTheFileOpen()
    {
        // As an antivirus scanning a file just written: open for reading and writing, shared.
        var path = Path.Combine(Path.GetTempPath(), "dm-tests", $"locked-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([1, 2, 3])),
                    await FileHash.ComputeAsync(path, "SHA256", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExpectedHashSetOnAnOldDownload_IsChecked_WithoutANewCompletionNotice()
    {
        var content = SiteResource.File(60_000, seed: 8);
        _site.Set("files.test", "/old.bin", content);
        var first = 0;
        void FirstCompletion(DownloadSnapshot _) => Interlocked.Increment(ref first);
        _downloads.DownloadCompleted += FirstCompletion;
        var id = _downloads.Add("http://files.test/old.bin");
        await Finished(id);
        // The completion event follows the snapshot: counted from here on, it would be the first download's own.
        for (var i = 0; i < 100 && Volatile.Read(ref first) == 0; i++)
            await Task.Delay(20);
        _downloads.DownloadCompleted -= FirstCompletion;
        var completions = 0;
        var checks = 0;
        _downloads.DownloadCompleted += _ => Interlocked.Increment(ref completions);
        _downloads.CheckCompleted += _ => Interlocked.Increment(ref checks);

        _downloads.SetExpectedHash(id, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.Body)));
        var verified = await Finished(id, s => s.Check is { Pending: false, HashMatches: not null });

        Assert.True(verified.Check!.HashMatches);
        for (var i = 0; i < 100 && Volatile.Read(ref checks) == 0; i++)
            await Task.Delay(20); // the events follow the snapshot
        Assert.Equal(1, checks);
        Assert.Equal(0, completions);
    }

    // ------------------------------------------------------------------ antivirus (simulated scanner: cmd.exe /c exit N)

    [Fact]
    public async Task Scanner_Outcomes_FromItsExitCode()
    {
        var file = Path.Combine(_dir, "sample & name.txt");
        File.WriteAllText(file, "x");
        Assert.Equal(ScanOutcome.Clean, (await VirusScanner.ScanAsync(Cmd, "/c exit 0", "2", file, TimeSpan.FromSeconds(20), default)).Outcome);
        Assert.Equal(ScanOutcome.Threat, (await VirusScanner.ScanAsync(Cmd, "/c exit 2", "2", file, TimeSpan.FromSeconds(20), default)).Outcome);
        var failed = await VirusScanner.ScanAsync(Cmd, "/c exit 7", "2", file, TimeSpan.FromSeconds(20), default);
        Assert.Equal(ScanOutcome.Failed, failed.Outcome);
        Assert.Contains("7", failed.Detail);

        var watch = Stopwatch.StartNew();
        var slow = await VirusScanner.ScanAsync(Cmd, "/c ping -n 30 127.0.0.1 & rem {file}", "2", file, TimeSpan.FromSeconds(1), default);
        Assert.Equal(ScanOutcome.TimedOut, slow.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Scanner_IsNeverTheFile_NorAScript_AndTheFileIsOneArgument()
    {
        var file = Path.Combine(_dir, "evil.exe");
        File.Copy(Cmd, file);
        Assert.Equal(ScanOutcome.Failed, (await VirusScanner.ScanAsync(file, "/c exit 0", "2", file, TimeSpan.FromSeconds(5), default)).Outcome);
        var script = Path.Combine(_dir, "scan.cmd");
        File.WriteAllText(script, "@exit 0");
        Assert.NotNull(VirusScanner.Check(script));
        Assert.Equal(["-Scan", "-ScanType", "3", "-File", @"C:\a b\x & y.zip"], VirusScanner.BuildArguments(VirusScanner.DefaultArguments, @"C:\a b\x & y.zip"));
        Assert.Equal(["--quiet", @"C:\f.zip"], VirusScanner.BuildArguments("--quiet", @"C:\f.zip")); // no placeholder: the file comes last
    }

    [Fact]
    public async Task ScanAfterDownload_TheOutcomeIsKeptAndAnnounced()
    {
        _site.Set("files.test", "/scan.zip", SiteResource.File(20_000, seed: 5));
        UpdateSettings(s =>
        {
            s.ScanOnCompletion = true;
            s.ScannerPath = Cmd;
            s.ScannerArguments = "/c exit 2";
        });
        var done = await Finished(_downloads.Add("http://files.test/scan.zip"));
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(ScanOutcome.Threat, done.Check!.Scan);
        Assert.True(done.Check.HasProblem);
        Assert.True(File.Exists(done.FinalPath)); // the scanner decides what to do with it; the app never runs it
        Assert.Equal(ScanOutcome.Threat, _store.LoadAll().Single(r => r.Id == done.Id).Check!.Scan);
    }

    // ------------------------------------------------------------------ grabber: synchronization after failed or removed downloads

    private GrabberProject FilesProject() => _grabber.Save(new GrabberProject
    {
        Name = "sync", StartUrl = "http://site.test/", RequestsPerSecond = 20, RespectRobots = false,
    });

    private async Task<GrabberRun> RunAsync(GrabberProject project)
    {
        var run = _grabber.Start(project.Id);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return run;
    }

    [Fact]
    public async Task Synchronization_OffersAgainFilesWhoseDownloadFailedOrWasRemoved()
    {
        _site.Page("/", """<a href="/ok.zip">a</a> <a href="/broken.zip">b</a> <a href="/removed.zip">c</a> <a href="/waiting.zip">d</a>""");
        foreach (var name in new[] { "/ok.zip", "/broken.zip", "/removed.zip", "/waiting.zip" })
            _site.Set("site.test", name, SiteResource.File(5000, etag: $"\"{name}\""));
        var project = FilesProject();
        var first = await RunAsync(project);
        Assert.Equal(4, _grabber.Send(project.Id, first.Items.ToList(), null, null, null, startQueue: false));
        long Id(string name) => _downloads.GetSnapshots().Single(s => s.Url.EndsWith(name)).Id;

        // ok.zip completes; broken.zip fails (the server lost it, then got it back); removed.zip is removed; waiting.zip waits.
        _downloads.Start(Id("/ok.zip"));
        Assert.Equal(DownloadStatus.Completed, (await Finished(Id("/ok.zip"))).Status);
        var broken = _site.Set("site.test", "/broken.zip", new SiteResource { Status = 410 });
        _downloads.Start(Id("/broken.zip"));
        Assert.Equal(DownloadStatus.Failed, (await Finished(Id("/broken.zip"))).Status);
        _site.Set("site.test", "/broken.zip", SiteResource.File(5000, etag: "\"/broken.zip\""));
        await _downloads.RemoveAsync(Id("/removed.zip"), deleteFile: false);

        var second = await RunAsync(project);
        GrabberItemStatus Status(string name) => second.Items.Single(i => i.Url.EndsWith(name)).Status;
        Assert.Equal(GrabberItemStatus.Unchanged, Status("/ok.zip"));
        Assert.Equal(GrabberItemStatus.New, Status("/broken.zip"));
        Assert.Equal(GrabberItemStatus.New, Status("/removed.zip"));
        Assert.Equal(GrabberItemStatus.Unchanged, Status("/waiting.zip")); // still in its queue: not sent twice
        Assert.True(_grabber.Files(project.Id)["http://site.test/ok.zip"].Obtained);
        Assert.False(_grabber.Files(project.Id)["http://site.test/waiting.zip"].Obtained);
        _ = broken;
    }

    // ------------------------------------------------------------------ grabber: speed limit and cookies

    [Fact]
    public async Task OfflineCopy_FollowsTheGlobalSpeedLimit()
    {
        _site.Page("/", """<img src="/a.png"><img src="/b.png"><img src="/c.png">""");
        foreach (var name in new[] { "/a.png", "/b.png", "/c.png" })
            _site.Set("site.test", name, SiteResource.File(64 * 1024, "image/png"));
        UpdateSettings(s => s.SpeedLimitKBps = 64); // 192 KB at 64 KB/s: about 3 s (the first quarter second is a burst)
        var project = _grabber.Save(new GrabberProject
        {
            Name = "slow", StartUrl = "http://site.test/", Mode = GrabberMode.Mirror, Folder = Path.Combine(_dir, "copy"),
            RequestsPerSecond = 20, RespectRobots = false,
        });
        var watch = Stopwatch.StartNew();
        var run = await RunAsync(project);
        Assert.Equal(3, run.Result!.Files);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cookies_AreForgottenAfterARun_UnlessKept_AndCanBeCleared()
    {
        _site.Page("/", """<a href="/m.zip">m</a>""");
        _site.Set("site.test", "/m.zip", SiteResource.File(10));
        var cookie = new BrowserCookie("s", "secret-value", "site.test", "/", false, true);
        var forget = _grabber.Save(new GrabberProject { Name = "forget", StartUrl = "http://site.test/", Cookies = [cookie], RespectRobots = false });
        Assert.True(forget.ForgetCookiesAfterRun); // the default
        var run = await RunAsync(forget);
        Assert.Contains(_site.Requests, r => r.Cookie.Contains("secret-value"));
        Assert.Empty(_grabber.Find(forget.Id)!.Cookies);
        Assert.Empty(_store.LoadGrabberProjects().Single(p => p.Id == forget.Id).Cookies);
        // Files sent from that run still go with the run's cookies.
        _grabber.Send(run, run.Items.ToList(), null, null, null, startQueue: false);
        Assert.Equal("secret-value", _store.LoadAll().Single(r => r.Url.EndsWith("m.zip")).Context!.Cookies.Single().Value);

        var keep = _grabber.Save(new GrabberProject { Name = "keep", StartUrl = "http://site.test/", Cookies = [cookie], ForgetCookiesAfterRun = false, RespectRobots = false });
        await RunAsync(keep);
        Assert.Single(_grabber.Find(keep.Id)!.Cookies);
        _grabber.ClearCookies(keep.Id);
        Assert.Empty(_grabber.Find(keep.Id)!.Cookies);
    }

    // ------------------------------------------------------------------ logs and disk errors

    [Fact]
    public void Logs_Rotate_AndNeverHoldSecrets()
    {
        var logs = Path.Combine(_dir, "logs");
        Log.Configure(logs, maxBytes: 4096, files: 3);
        for (var i = 0; i < 400; i++)
            Log.Info("test", $"line {i} https://user:pw@host.test/file.zip?token=abc123&sig=zz password=hunter2 Cookie: sessionid=42");
        var files = Directory.GetFiles(logs).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["app.1.log", "app.2.log", "app.log"], files);
        Assert.All(Directory.GetFiles(logs), f => Assert.True(new FileInfo(f).Length < 4096 + 400));
        // Other tests log at the same time (the log is global): read the way the writer shares the file.
        static string Read(string file)
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
        var text = string.Concat(Directory.GetFiles(logs).Select(Read));
        Assert.Contains("https://host.test/file.zip", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("pw@", text);
        Assert.DoesNotContain("sessionid=42", text);
        Assert.Contains("line 399", Read(Path.Combine(logs, "app.log")));
    }

    [Fact]
    public void LongFileNames_AreShortened_KeepingTheExtension()
    {
        var name = FileNames.Sanitize(new string('a', 400) + ".tar.gz");
        Assert.Equal(FileNames.MaxNameLength, name.Length);
        Assert.EndsWith(".gz", name);
        Assert.Equal("short.zip", FileNames.Sanitize("short.zip"));
    }

    [Fact]
    public void DiskErrors_AreSaidInWords_AndNotRetried()
    {
        var full = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        Assert.True(StorageErrors.IsPermanent(full));
        Assert.Contains("Spazio su disco esaurito", StorageErrors.Describe(full, @"D:\dl"));
        Assert.Contains("D:\\dl", StorageErrors.Describe(new UnauthorizedAccessException(), @"D:\dl"));
        Assert.False(StorageErrors.IsPermanent(new IOException("The server closed the connection early.")));
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_FailsAtOnce_WithAClearMessage()
    {
        _site.Set("files.test", "/x.bin", SiteResource.File(1000));
        // A file where the folder should be: the folder cannot be created.
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "not a folder");
        var done = await Finished(_downloads.Add("http://files.test/x.bin", directory: Path.Combine(blocked, "sub")));
        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.NotNull(done.Error);
    }
}
