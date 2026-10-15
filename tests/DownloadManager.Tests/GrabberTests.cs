using DownloadManager.Core;
using DownloadManager.Core.Grabber;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>
/// The grabber against a small local site: depth, domains, filters, robots.txt, request rate and 429, offline copies
/// with rewritten links, cycles and traps, synchronization and scheduling.
/// </summary>
public sealed class GrabberTests : IAsyncLifetime
{
    private GrabberTestSite _site = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private DownloadStore _store = null!;
    private DownloadService _downloads = null!;
    private GrabberService _grabber = null!;
    private FakeClock _clock = null!;

    private static readonly GrabberEngineOptions Fast = new()
    {
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        RequestTimeout = TimeSpan.FromSeconds(10),
    };

    public async Task InitializeAsync()
    {
        _site = await GrabberTestSite.StartAsync();
        Directory.CreateDirectory(_dir);
        _store = new DownloadStore(Path.Combine(_dir, "state.db"));
        _store.SaveSettings(new AppSettings { DownloadFolder = Path.Combine(_dir, "downloads"), ProxyMode = ProxyMode.None });
        _clock = new FakeClock(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        _downloads = new DownloadService(_store, clock: _clock, autoTick: false);
        // The queue stays stopped: files sent by the tests are listed, never downloaded.
        await _downloads.StopQueueAsync();
        _grabber = new GrabberService(_downloads, _store, _clock, (_, cookies) => _site.CreateClient(cookies), Fast, autoTick: false);
    }

    public async Task DisposeAsync()
    {
        await _grabber.DisposeAsync();
        await _downloads.DisposeAsync();
        _store.Dispose();
        await _site.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static GrabberProject Project(GrabberMode mode = GrabberMode.Files, string start = "http://site.test/") => new()
    {
        Name = "test",
        StartUrl = start,
        Mode = mode,
        RequestsPerSecond = 20,
        MaxDepth = 3,
    };

    private async Task<(GrabberRun Run, GrabberRunResult Result)> RunAsync(GrabberProject project)
    {
        var saved = project.Id == 0 ? _grabber.Save(project) : project;
        var run = _grabber.Start(saved.Id);
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        return (run, result);
    }

    private static string[] Names(GrabberRun run, GrabberItemStatus? status = null) =>
        run.Items.Where(i => !i.IsPage && (status is null || i.Status == status)).Select(i => new Uri(i.Url).AbsolutePath).Order().ToArray();

    // ------------------------------------------------------------------ depth, domains, filters

    [Fact]
    public async Task Depth_FollowsPagesUpToTheLimit_AndListsTheFilesOfEveryVisitedPage()
    {
        _site.Page("/", """<a href="a.html">a</a> <a href="/f/p0.pdf">p0</a>""");
        _site.Page("/a.html", """<a href="b.html">b</a> <a href="/f/p1.pdf">p1</a>""");
        _site.Page("/b.html", """<a href="c.html">c</a> <a href="/f/p2.pdf">p2</a>""");
        _site.Page("/c.html", """<a href="/f/p3.pdf">p3</a>""");
        foreach (var i in Enumerable.Range(0, 4))
            _site.Set("site.test", $"/f/p{i}.pdf", SiteResource.File(1000 + i, "application/pdf"));

        var project = Project();
        project.MaxDepth = 2;
        var (run, result) = await RunAsync(project);

        Assert.Equal(["/f/p0.pdf", "/f/p1.pdf", "/f/p2.pdf"], Names(run));
        Assert.Empty(_site.To("/c.html"));
        Assert.Equal(3, result.Pages);
        Assert.All(run.Items, i => Assert.Equal(GrabberItemStatus.New, i.Status));
        Assert.Equal(1001, run.Items.Single(i => i.Url.EndsWith("p1.pdf")).Size);
        Assert.Equal("http://site.test/a.html", run.Items.Single(i => i.Url.EndsWith("p1.pdf")).SourcePage);
        Assert.All(_site.To("/f/p1.pdf"), r => Assert.Equal("HEAD", r.Method)); // listed without downloading
    }

    [Fact]
    public async Task Domains_SameSiteOnly_OrTheListedOnes()
    {
        _site.Page("/", """
            <a href="http://files.site.test/sub.zip">sub</a>
            <a href="http://cdn.test/cdn.zip">cdn</a>
            <a href="http://other.test/other.zip">other</a>
            <a href="http://other.test/page.html">page</a>
            """);
        _site.Set("files.site.test", "/sub.zip", SiteResource.File(10));
        _site.Set("cdn.test", "/cdn.zip", SiteResource.File(10));
        _site.Set("other.test", "/other.zip", SiteResource.File(10));
        _site.Page("/page.html", "<p>other</p>", "other.test");

        var (sameSite, _) = await RunAsync(Project());
        Assert.Equal(["/sub.zip"], Names(sameSite));

        var listed = Project();
        listed.SameDomainOnly = false;
        listed.AllowedDomains = ["cdn.test"];
        var (run, _) = await RunAsync(listed);
        Assert.Equal(["/cdn.zip", "/sub.zip"], Names(run));
        Assert.DoesNotContain(_site.Requests, r => r.Host == "other.test");
    }

    [Fact]
    public async Task Filters_TypeSizeIncludeAndExclude()
    {
        _site.Page("/", """
            <a href="/report-2025.pdf">1</a> <a href="/report-draft.pdf">2</a> <a href="/small-report.pdf">3</a>
            <a href="/huge-report.pdf">4</a> <a href="/report.zip">5</a> <a href="/skip/inner.html">6</a>
            <img src="/pic-report.png">
            """);
        _site.Set("site.test", "/report-2025.pdf", SiteResource.File(5000, "application/pdf"));
        _site.Set("site.test", "/report-draft.pdf", SiteResource.File(5000, "application/pdf"));
        _site.Set("site.test", "/small-report.pdf", SiteResource.File(100, "application/pdf"));
        _site.Set("site.test", "/huge-report.pdf", SiteResource.File(90_000, "application/pdf"));
        _site.Set("site.test", "/report.zip", SiteResource.File(5000, "application/zip"));
        _site.Set("site.test", "/pic-report.png", SiteResource.File(5000, "image/png"));
        _site.Page("/skip/inner.html", """<a href="/inner-report.pdf">x</a>""");

        var project = Project();
        project.Extensions = ["pdf", "png"];
        project.MinSizeBytes = 1000;
        project.MaxSizeBytes = 50_000;
        project.IncludePattern = "report";
        project.ExcludePattern = "draft|/skip/";
        var (run, _) = await RunAsync(project);

        Assert.Equal(["/pic-report.png", "/report-2025.pdf"], Names(run));
        Assert.Empty(_site.To("/skip/inner.html"));
        Assert.Empty(_site.To("/report-draft.pdf"));
    }

    [Fact]
    public async Task UnsafeExpressions_AreRefused_AndTheEngineCannotHangOnThem()
    {
        Assert.NotNull(GrabberFilters.CheckPattern("(a)\\1")); // backreferences need backtracking: refused
        Assert.NotNull(GrabberFilters.CheckPattern("[unclosed"));
        Assert.Null(GrabberFilters.CheckPattern("(a+)+$")); // fine with the non-backtracking engine
        var project = Project();
        project.IncludePattern = "(a+)+$";
        _site.Page("/", $"""<a href="/{new string('a', 5000)}!.bin">x</a>""");
        var (_, result) = await RunAsync(project);
        Assert.Equal(0, result.Files);
        project = Project();
        project.IncludePattern = "(a)\\1";
        Assert.Throws<ArgumentException>(() => _grabber.Save(project));
    }

    [Fact]
    public async Task APageLinkThatIsReallyAFile_IsListedUnderTheServersName()
    {
        _site.Page("/", """<a href="/download.php?id=7">get</a>""");
        _site.Set("site.test", "/download.php?id=7", new SiteResource
        {
            Body = new byte[4321], ContentType = "application/pdf", ContentDisposition = "attachment; filename=\"guide.pdf\"",
        });
        var project = Project();
        project.Extensions = ["pdf"];
        var (run, _) = await RunAsync(project);
        var item = Assert.Single(run.Items);
        Assert.Equal("guide.pdf", item.Name);
        Assert.Equal(4321, item.Size);
    }

    // ------------------------------------------------------------------ robots.txt, rate, 429

    [Fact]
    public async Task RobotsTxt_IsRespected_UnlessTurnedOff()
    {
        _site.Set("site.test", "/robots.txt", new SiteResource
        {
            Body = "User-agent: *\nDisallow: /private/\nAllow: /private/open\n"u8.ToArray(), ContentType = "text/plain",
        });
        _site.Page("/", """<a href="/private/secret.html">s</a> <a href="/private/data.zip">d</a> <a href="/private/open.zip">o</a> <a href="/pub.zip">p</a>""");
        _site.Page("/private/secret.html", """<a href="/deep.zip">x</a>""");
        foreach (var name in new[] { "/private/data.zip", "/private/open.zip", "/pub.zip", "/deep.zip" })
            _site.Set("site.test", name, SiteResource.File(10));

        var (polite, result) = await RunAsync(Project());
        Assert.Equal(["/private/open.zip", "/pub.zip"], Names(polite));
        Assert.Empty(_site.To("/private/secret.html"));
        Assert.Empty(_site.To("/private/data.zip"));
        Assert.True(polite.Progress.RobotsBlocked >= 2);

        var rude = Project();
        rude.RespectRobots = false;
        var (run, _) = await RunAsync(rude);
        Assert.Equal(["/deep.zip", "/private/data.zip", "/private/open.zip", "/pub.zip"], Names(run));
        Assert.Single(_site.To("/robots.txt")); // not even asked the second time
        Assert.Equal(0, result.Failed);
    }

    [Theory]
    [InlineData("User-agent: DownloadManager\nDisallow: /a\n\nUser-agent: *\nDisallow: /\n", "/a/x", false)]
    [InlineData("User-agent: DownloadManager\nDisallow: /a\n\nUser-agent: *\nDisallow: /\n", "/b", true)]
    [InlineData("User-agent: other\nDisallow: /\n", "/anything", true)]
    [InlineData("User-agent: *\nDisallow: /*.pdf$\n", "/doc.pdf", false)]
    [InlineData("User-agent: *\nDisallow: /*.pdf$\n", "/doc.pdf?x=1", true)]
    [InlineData("User-agent: *\nDisallow: /shop\nAllow: /shop/public\n", "/shop/public/item", true)]
    [InlineData("User-agent: *\nDisallow: /shop\nAllow: /shop/public\n", "/shop/cart", false)]
    [InlineData("User-agent: *\nDisallow: /p%2fq\n", "/p%2Fq", false)]
    [InlineData("User-agent: *\nDisallow:\n", "/x", true)]
    public void RobotsTxt_Rules(string text, string path, bool allowed) =>
        Assert.Equal(allowed, RobotsTxt.Parse(text, "DownloadManager/0.7").IsAllowed(new Uri("http://site.test" + path)));

    [Fact]
    public void RobotsTxt_CrawlDelay_AndTheTokenOfACustomUserAgent()
    {
        var robots = RobotsTxt.Parse("User-agent: MyBot\nCrawl-delay: 3\nDisallow: /x\n", "MyBot/2.0 (+https://example.test)");
        Assert.Equal(3, robots.CrawlDelay);
        Assert.False(robots.IsAllowed(new Uri("http://site.test/x")));
    }

    [Fact]
    public async Task RequestsPerHost_AreSpacedByTheRate()
    {
        var links = string.Join(" ", Enumerable.Range(0, 8).Select(i => $"<a href=\"/r{i}.bin\">{i}</a>"));
        _site.Page("/", links);
        foreach (var i in Enumerable.Range(0, 8))
            _site.Set("site.test", $"/r{i}.bin", SiteResource.File(10));
        var project = Project();
        project.RequestsPerSecond = 5;
        await RunAsync(project);

        var times = _site.Requests.Where(r => r.Host == "site.test").Select(r => r.At).Order().ToList();
        Assert.Equal(10, times.Count); // robots.txt, the page, 8 files
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        Assert.All(gaps, gap => Assert.True(gap >= TimeSpan.FromMilliseconds(180), $"gap {gap.TotalMilliseconds} ms"));
    }

    [Fact]
    public async Task TooManyRequests_WaitsForRetryAfter_ThenSucceeds()
    {
        _site.Page("/", """<a href="/busy.zip">b</a>""");
        _site.Set("site.test", "/busy.zip", SiteResource.File(2000)).BusyRequests = 2;
        var (run, _) = await RunAsync(Project());

        var item = Assert.Single(run.Items);
        Assert.Equal(GrabberItemStatus.New, item.Status);
        Assert.Equal(2000, item.Size);
        var attempts = _site.To("/busy.zip").OrderBy(r => r.At).ToList();
        Assert.Equal([429, 429, 200], attempts.Select(r => r.Status));
        Assert.True(attempts[1].At - attempts[0].At >= TimeSpan.FromMilliseconds(900));
        Assert.True(attempts[2].At - attempts[1].At >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task ARetryAfterTooLong_GivesUpOnThatFile()
    {
        _site.Page("/", """<a href="/later.zip">b</a>""");
        var later = _site.Set("site.test", "/later.zip", SiteResource.File(10));
        later.BusyRequests = 10;
        later.RetryAfter = "3600";
        var (run, result) = await RunAsync(Project());
        Assert.Equal(GrabberItemStatus.Failed, Assert.Single(run.Items).Status);
        Assert.Single(_site.To("/later.zip"));
        Assert.Equal(1, result.Failed);
    }

    // ------------------------------------------------------------------ cycles and traps

    [Fact]
    public async Task Cycles_SessionParameters_AndRepeatedPaths_AreVisitedOnce()
    {
        _site.Page("/", """
            <a href="/a.html">a</a> <a href="/a.html#top">a again</a> <a href="/a.html?PHPSESSID=1">sess1</a>
            <a href="/a.html?utm_source=x&amp;PHPSESSID=2">sess2</a> <a href="HTTP://SITE.TEST:80/a.html">upper</a>
            <a href="/loop/loop/loop/x.html">loop</a>
            """);
        _site.Page("/a.html", """<a href="/">home</a> <a href="/b.html">b</a>""");
        _site.Page("/b.html", """<a href="/a.html">a</a> <a href="/">home</a>""");
        _site.Page("/loop/loop/loop/x.html", "x");
        var project = Project();
        project.MaxDepth = 10;
        await RunAsync(project);

        Assert.Single(_site.To("/"));
        Assert.Single(_site.To("/a.html"));
        Assert.Single(_site.To("/b.html"));
        Assert.DoesNotContain(_site.Requests, r => r.Path.Contains("PHPSESSID") || r.Path.Contains("loop"));
    }

    [Fact]
    public async Task EndlessCalendar_StopsAtTheLimitOfPagesOfTheSameShape()
    {
        _site.Page("/", """<a href="/cal?month=1">calendar</a>""");
        for (var month = 1; month <= 60; month++)
            _site.Page($"/cal?month={month}", $"""<a href="/cal?month={month + 1}">next</a> <a href="/f{month}.zip">f</a>""");
        var project = Project();
        project.MaxDepth = 50;
        project.MaxPagesPerPattern = 5;
        await RunAsync(project);

        Assert.Equal(5, _site.Requests.Count(r => r.Path.StartsWith("/cal")));
    }

    [Fact]
    public async Task Limits_StopTheRun_AndSayWhich()
    {
        var links = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"<a href=\"/p{i}.html\">{i}</a>"));
        _site.Page("/", links);
        for (var i = 0; i < 30; i++)
            _site.Page($"/p{i}.html", $"<a href=\"/x{i}.zip\">x</a>");
        var project = Project();
        project.MaxPages = 5;
        var (_, result) = await RunAsync(project);
        Assert.Equal("pages", result.LimitReached);
        Assert.True(_site.Requests.Count(r => r.Path.StartsWith("/p")) <= 4);
        Assert.Contains("pagine", GrabberService.Summary(result));
    }

    [Theory]
    [InlineData("http://site.test/a/b/c/d/e/f/g/h/i/j/k/l/m/n/o/p/q/r/s/t/u/v/w/x/y/z/0/1/2/3/4/5.html", "path too deep")]
    [InlineData("http://site.test/x/y/x/y/x/z.html", "path repeats itself")]
    [InlineData("http://site.test/ok/page.html", null)]
    public void TrapGuard_RecognizesShapesWithoutEnd(string url, string? reason) => Assert.Equal(reason, TrapGuard.Inspect(new Uri(url)));

    [Fact]
    public void Normalize_DropsFragmentsSessionsAndDefaultPorts_AndOrdersParameters()
    {
        Assert.Equal("http://site.test/a?a=1&b=2",
            CrawlUrls.Normalize(new Uri("HTTP://Site.Test:80/a;jsessionid=XYZ?b=2&utm_medium=m&a=1&sid=9#frag"))!.AbsoluteUri);
        Assert.Null(CrawlUrls.Normalize(new Uri("ftp://site.test/a")));
        Assert.Null(CrawlUrls.Resolve(new Uri("http://site.test/"), "javascript:alert(1)"));
        Assert.Null(CrawlUrls.Resolve(new Uri("http://site.test/"), "mailto:a@b.c"));
    }

    // ------------------------------------------------------------------ offline copy

    private void OfflineSite()
    {
        _site.Page("/", """
            <!doctype html><html><head><meta charset="windows-1252"><title>Home</title>
            <base href="http://site.test/">
            <link rel="stylesheet" href="css/site.css" integrity="sha384-abc" crossorigin="anonymous">
            <style>@import "css/extra.css"; .hero { background: url('img/hero.jpg') }</style>
            <script src="js/app.js"></script>
            </head><body>
            <img src="img/a.png" srcset="img/a.png 1x, img/a-2x.png 2x" alt="a">
            <picture><source srcset="img/b.webp" type="image/webp"><img src="img/b.png"></picture>
            <div style="background-image:url(img/bg.png)">x</div>
            <a href="sub/page.html#part">sub</a> <a href="docs/guide.pdf">pdf</a>
            <a href="http://other.test/elsewhere.html">out</a> <a href="notsaved.zip">zip</a>
            </body></html>
            """);
        _site.Page("/sub/page.html", """<a href="../">home</a> <img src="/img/a.png"> <a href="/dir with space/x.html">s</a>""");
        _site.Page("/dir with space/x.html", """<p>spaced</p>""");
        _site.Set("site.test", "/css/site.css", new SiteResource
        {
            Body = "@font-face{src:url(\"../fonts/f.woff2\")} body{background:url(../img/body.png)} /* url(../img/commented.png) */"u8.ToArray(),
            ContentType = "text/css",
        });
        _site.Set("site.test", "/css/extra.css", new SiteResource { Body = ".x{background:url(/img/extra.png)}"u8.ToArray(), ContentType = "text/css" });
        foreach (var name in new[] { "/img/a.png", "/img/a-2x.png", "/img/b.webp", "/img/b.png", "/img/bg.png", "/img/hero.jpg",
                     "/img/body.png", "/img/extra.png", "/fonts/f.woff2", "/js/app.js", "/docs/guide.pdf", "/notsaved.zip" })
            _site.Set("site.test", name, SiteResource.File(300, "application/octet-stream", etag: $"\"{name}\""));
    }

    [Fact]
    public async Task Mirror_SavesTheSiteWithItsFolders_AndPointsEveryLinkAtTheLocalCopy()
    {
        OfflineSite();
        var root = Path.Combine(_dir, "copy");
        var project = Project(GrabberMode.Mirror);
        project.Folder = root;
        project.Extensions = ["pdf"]; // linked files: only PDFs; what pages need is always kept
        var (run, result) = await RunAsync(project);

        var site = Path.Combine(root, "site.test");
        Assert.Equal(Path.Combine(site, "index.html"), result.StartPage);
        foreach (var file in new[] { @"css\site.css", @"css\extra.css", @"img\a.png", @"img\a-2x.png", @"img\b.webp", @"img\b.png", @"img\bg.png",
                     @"img\hero.jpg", @"img\body.png", @"img\extra.png", @"fonts\f.woff2", @"js\app.js", @"docs\guide.pdf", @"sub\page.html",
                     @"dir with space\x.html" })
            Assert.True(File.Exists(Path.Combine(site, file)), file);
        Assert.False(File.Exists(Path.Combine(site, "notsaved.zip")));
        Assert.False(File.Exists(Path.Combine(site, @"img\commented.png")));

        var index = File.ReadAllText(Path.Combine(site, "index.html"));
        Assert.Contains("href=\"css/site.css\"", index);
        Assert.DoesNotContain("integrity", index);
        Assert.DoesNotContain("<base", index);
        Assert.Contains("charset=\"utf-8\"", index);
        Assert.Contains("@import \"css/extra.css\"", index);
        Assert.Contains("url('img/hero.jpg')", index);
        Assert.Contains("srcset=\"img/a.png 1x, img/a-2x.png 2x\"", index);
        Assert.Contains("srcset=\"img/b.webp\"", index);
        Assert.Contains("url(img/bg.png)", index);
        Assert.Contains("href=\"sub/page.html#part\"", index);
        Assert.Contains("href=\"docs/guide.pdf\"", index);
        Assert.Contains("href=\"http://other.test/elsewhere.html\"", index); // outside the project: stays online
        Assert.Contains("href=\"http://site.test/notsaved.zip\"", index); // not saved: absolute, still works online

        var css = File.ReadAllText(Path.Combine(site, @"css\site.css"));
        Assert.Contains("url(\"../fonts/f.woff2\")", css);
        Assert.Contains("url(../img/body.png)", css);
        Assert.Contains("/* url(../img/commented.png) */", css);

        var sub = File.ReadAllText(Path.Combine(site, @"sub\page.html"));
        Assert.Contains("href=\"../index.html\"", sub);
        Assert.Contains("src=\"../img/a.png\"", sub);
        Assert.Contains("href=\"../dir%20with%20space/x.html\"", sub);
        Assert.Equal(0, result.Failed);
        Assert.Contains(run.Items, i => i.IsPage && i.LocalPath == "site.test\\index.html");
    }

    [Fact]
    public async Task Mirror_Synchronization_FetchesOnlyChanges_AndKeepsRemovedFiles()
    {
        OfflineSite();
        var root = Path.Combine(_dir, "copy");
        var project = Project(GrabberMode.Mirror);
        project.Folder = root;
        project.Extensions = ["pdf"];
        var saved = _grabber.Save(project);
        await RunAsync(saved);

        // The site changes: one image is new, one is changed, the PDF is gone.
        _site.Set("site.test", "/img/b.png", SiteResource.File(400, "image/png", etag: "\"b2\"", seed: 9));
        _site.Remove("site.test", "/docs/guide.pdf");
        _site.Page("/sub/page.html", """<a href="../">home</a> <img src="/img/a.png"> <img src="/img/new.png">""");
        _site.Set("site.test", "/img/new.png", SiteResource.File(50, "image/png"));
        var before = _site.Requests.Count;
        var (run, result) = await RunAsync(saved);

        var second = _site.Requests.Skip(before).ToList();
        var a = second.Single(r => r.Path == "/img/a.png");
        Assert.Equal(304, a.Status); // asked with its ETag, not downloaded again
        Assert.Equal("\"/img/a.png\"", a.IfNoneMatch);
        Assert.Equal(GrabberItemStatus.Changed, run.Items.Single(i => i.Url.EndsWith("/img/b.png")).Status);
        Assert.Equal(GrabberItemStatus.New, run.Items.Single(i => i.Url.EndsWith("/img/new.png")).Status);
        Assert.Equal(GrabberItemStatus.Unchanged, run.Items.Single(i => i.Url.EndsWith("/img/a.png")).Status);
        Assert.Equal(GrabberItemStatus.Removed, run.Items.Single(i => i.Url.EndsWith("/docs/guide.pdf")).Status);
        Assert.True(File.Exists(Path.Combine(root, "site.test", "docs", "guide.pdf"))); // never deleted
        Assert.Equal(400, new FileInfo(Path.Combine(root, "site.test", "img", "b.png")).Length);
        Assert.Equal(2, result.Removed); // the PDF, and the page the new version of sub/page.html no longer links to
        Assert.NotNull(_grabber.Files(saved.Id)["http://site.test/docs/guide.pdf"].RemovedUtc);
        Assert.True(File.Exists(Path.Combine(root, "site.test", "dir with space", "x.html")));
        Assert.Contains("src=\"../img/a.png\"", File.ReadAllText(Path.Combine(root, "site.test", "sub", "page.html"))); // unchanged files stay linked
    }

    [Fact]
    public async Task Mirror_HostileAddresses_NeverLeaveTheCopy()
    {
        _site.Page("/", """
            <a href="/..%2f..%2fevil.html">1</a> <a href="/%2e%2e/%2e%2e/evil2.html">2</a> <a href="/con/aux.html">3</a>
            <a href="/a:b|c*d.html">4</a> <a href="/trailing.%20.html">5</a>
            """);
        foreach (var path in new[] { "/..%2f..%2fevil.html", "/evil2.html", "/con/aux.html", "/a:b%7Cc*d.html", "/trailing.%20.html" })
            _site.Page(path, "<p>x</p>");
        var root = Path.Combine(_dir, "copy", "inner");
        var project = Project(GrabberMode.Mirror);
        project.Folder = root;
        await RunAsync(project);

        var written = Directory.GetFiles(Path.Combine(_dir, "copy"), "*", SearchOption.AllDirectories);
        Assert.All(written, f => Assert.StartsWith(root + Path.DirectorySeparatorChar, f));
        Assert.Empty(Directory.GetFiles(_dir, "evil*", SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(Path.Combine(root, "site.test", "_con", "_aux.html")));
    }

    [Theory]
    [InlineData("http://site.test/%2e%2e/%2e%2e/x.html", @"site.test\x.html")]
    [InlineData("http://site.test/..%5C..%5Cx.html", @"site.test\x.html")]
    [InlineData("http://site.test/CON", @"site.test\_CON.html")]
    [InlineData("http://site.test/lpt1.txt.html", @"site.test\_lpt1.txt.html")]
    [InlineData("http://site.test/dir/", @"site.test\dir\index.html")]
    [InlineData("http://site.test:8080/a.html", @"site.test_8080\a.html")]
    [InlineData("http://site.test/a.php", @"site.test\a.php.html")]
    public void LocalPaths_AreValidAndInsideTheRoot(string url, string expected)
    {
        var root = Path.Combine(_dir, "root");
        var paths = new LocalPaths(root);
        var relative = paths.Map(new Uri(url), "text/html");
        Assert.Equal(expected, relative);
        Assert.True(paths.IsInside(relative));
    }

    [Fact]
    public void LocalPaths_LongNames_QueriesAndCaseCollisions()
    {
        var root = Path.Combine(_dir, "root");
        var paths = new LocalPaths(root);
        var longName = paths.Map(new Uri("http://site.test/" + string.Join('/', Enumerable.Repeat(new string('d', 70), 6)) + "/" + new string('f', 300) + ".png"), "image/png");
        Assert.True(Path.Combine(root, longName).Length <= LocalPaths.MaxFullPath);
        Assert.EndsWith(".png", longName);
        Assert.All(longName.Split('\\'), s => Assert.True(s.Length <= LocalPaths.MaxSegment));

        var q1 = paths.Map(new Uri("http://site.test/list.html?page=1"), "text/html");
        var q2 = paths.Map(new Uri("http://site.test/list.html?page=2"), "text/html");
        Assert.NotEqual(q1, q2);
        var upper = paths.Map(new Uri("http://site.test/Photo.JPG"), "image/jpeg");
        var lower = paths.Map(new Uri("http://site.test/photo.jpg"), "image/jpeg");
        Assert.False(string.Equals(upper, lower, StringComparison.OrdinalIgnoreCase));
        Assert.False(paths.IsInside(@"..\x"));
        Assert.False(paths.IsInside(@"C:\x"));
        Assert.False(paths.IsInside(@"a\b:stream"));
    }

    [Fact]
    public void SrcSet_And_Css_Parsing()
    {
        var candidates = SrcSet.Parse(" a.png 1x,b.png  2x , data:image/png;base64,AAAA 3x");
        Assert.Equal(["a.png", "b.png", "data:image/png;base64,AAAA"], candidates.Select(c => c.Url));
        Assert.Equal("2x", candidates[1].Descriptor);
        var css = CssLinks.Find("@import url('x.css'); a{b:url( \"y.png\" )} /* url(z.png) */ c{d:url(data:image/png;base64,AA)}", new Uri("http://site.test/css/"));
        Assert.Equal(["http://site.test/css/x.css", "http://site.test/css/y.png"], css.Select(l => l.Url.AbsoluteUri));
        Assert.Equal(LinkKind.Stylesheet, css[0].Kind);
    }

    // ------------------------------------------------------------------ synchronization and schedule (files mode)

    [Fact]
    public async Task Files_Synchronization_SendsNewAndChanged_ReportsRemoved()
    {
        _site.Page("/", """<a href="/1.zip">1</a> <a href="/2.zip">2</a> <a href="/3.zip">3</a> <a href="/4.zip">4</a>""");
        _site.Set("site.test", "/1.zip", SiteResource.File(100, etag: "\"1a\""));
        _site.Set("site.test", "/2.zip", SiteResource.File(200, etag: "\"2a\""));
        _site.Set("site.test", "/3.zip", new SiteResource { Body = new byte[300], LastModified = "Mon, 05 Oct 2026 10:00:00 GMT" });
        _site.Set("site.test", "/4.zip", new SiteResource { Body = new byte[400] }); // no validators: the size decides
        var project = _grabber.Save(Project());

        var (first, _) = await RunAsync(project);
        Assert.Equal(4, first.Items.Count(i => i.Status == GrabberItemStatus.New));
        var queue = _downloads.MainQueueId;
        Assert.Equal(4, _grabber.Send(project.Id, first.Items.ToList(), queue, null, null, startQueue: false));
        Assert.Equal(4, _downloads.GetSnapshots().Count);
        Assert.All(_downloads.GetSnapshots(), s => Assert.Equal(DownloadStatus.Queued, s.Status));

        _site.Set("site.test", "/1.zip", SiteResource.File(100, etag: "\"1b\"")); // new ETag
        _site.Remove("site.test", "/2.zip");
        _site.Set("site.test", "/3.zip", new SiteResource { Body = new byte[300], LastModified = "Mon, 05 Oct 2026 10:00:00 GMT" });
        _site.Set("site.test", "/4.zip", new SiteResource { Body = new byte[401] });
        _site.Page("/", """<a href="/1.zip">1</a> <a href="/2.zip">2</a> <a href="/3.zip">3</a> <a href="/4.zip">4</a> <a href="/5.zip">5</a>""");
        _site.Set("site.test", "/5.zip", SiteResource.File(500));
        var (second, result) = await RunAsync(project);

        Assert.Equal(["/5.zip"], Names(second, GrabberItemStatus.New));
        Assert.Equal(["/1.zip", "/4.zip"], Names(second, GrabberItemStatus.Changed));
        Assert.Equal(["/3.zip"], Names(second, GrabberItemStatus.Unchanged));
        Assert.Equal(["/2.zip"], Names(second, GrabberItemStatus.Removed));
        Assert.Equal(1, result.Removed);
        Assert.NotNull(_grabber.Files(project.Id)["http://site.test/2.zip"].RemovedUtc);
        Assert.Contains("1 rimossi", _grabber.Find(project.Id)!.LastResult);
    }

    [Fact]
    public async Task Schedule_RunsTheProjectOncePerSlot_AndQueuesWhatIsNew()
    {
        _site.Page("/", """<a href="/daily.zip">d</a>""");
        _site.Set("site.test", "/daily.zip", SiteResource.File(100, etag: "\"d1\""));
        var queue = _downloads.SaveQueue(new DownloadQueue { Name = "Notturna", MaxConcurrent = 1 });
        var project = Project();
        project.QueueId = queue.Id;
        project.Schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(2, 0), Mode = ScheduleMode.Weekly };
        project = _grabber.Save(project);
        _clock.UtcNow = FakeClock.Local(2026, 10, 13, 1, 59);

        _grabber.Tick();
        Assert.Null(_grabber.ActiveRun(project.Id));

        _clock.UtcNow = FakeClock.Local(2026, 10, 13, 2, 0).AddSeconds(30);
        _grabber.Tick();
        var run = _grabber.LastRun(project.Id);
        Assert.NotNull(run);
        Assert.True(run.Automatic);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var download = Assert.Single(_downloads.GetSnapshots());
        Assert.Equal(queue.Id, download.QueueId);
        Assert.Equal("http://site.test/daily.zip", download.Url);

        // The same slot does not run again, nor after a restart of the app within it.
        _clock.UtcNow = FakeClock.Local(2026, 10, 13, 2, 30);
        _grabber.Tick();
        Assert.Same(run, _grabber.LastRun(project.Id));
        await using (var restarted = new GrabberService(_downloads, _store, _clock, (_, c) => _site.CreateClient(c), Fast, autoTick: false))
        {
            restarted.Tick();
            Assert.Null(restarted.LastRun(project.Id));
        }

        // The next day: a new run, and an unchanged file is not queued again.
        _clock.UtcNow = FakeClock.Local(2026, 10, 14, 2, 1);
        _grabber.Tick();
        var next = _grabber.LastRun(project.Id)!;
        Assert.NotSame(run, next);
        await next.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(_downloads.GetSnapshots());
        Assert.Equal(GrabberItemStatus.Unchanged, Assert.Single(next.Items).Status);
    }

    [Fact]
    public async Task Cookies_AreSentToTheSite_AndStoredEncrypted()
    {
        _site.Page("/", """<a href="/members.zip">m</a>""");
        _site.Set("site.test", "/members.zip", SiteResource.File(10));
        var project = Project();
        project.Cookies = [new BrowserCookie("session", "s3cret-value", "site.test", "/", false, true)];
        var saved = _grabber.Save(project);
        await RunAsync(saved);
        Assert.All(_site.Requests.Where(r => r.Path != "/robots.txt"), r => Assert.Contains("session=s3cret-value", r.Cookie));

        using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "state.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT settings || ifnull(secrets, '') FROM grabber_projects";
        var raw = (string)cmd.ExecuteScalar()!;
        Assert.DoesNotContain("s3cret-value", raw);
        Assert.Equal("s3cret-value", _grabber.Find(saved.Id)!.Cookies.Single().Value);

        var without = _grabber.Find(saved.Id)!;
        without.UseCookies = false;
        _grabber.Save(without);
        var before = _site.Requests.Count;
        await RunAsync(_grabber.Find(saved.Id)!);
        Assert.All(_site.Requests.Skip(before), r => Assert.DoesNotContain("s3cret", r.Cookie));
    }

    [Fact]
    public async Task UserAgent_IsTheProjectsOwn_WhenSet()
    {
        _site.Page("/", "<p>x</p>");
        var project = Project();
        project.UserAgent = "TestBot/1.0";
        await RunAsync(project);
        Assert.All(_site.Requests, r => Assert.Equal("TestBot/1.0", r.UserAgent));
    }

    [Fact]
    public async Task Redirects_OutsideTheProject_AreNotFollowed()
    {
        _site.Page("/", """<a href="/go.html">go</a> <a href="/move.zip">m</a>""");
        _site.Set("site.test", "/go.html", new SiteResource { Location = "http://other.test/landing.html" });
        _site.Set("site.test", "/move.zip", new SiteResource { Location = "/real.zip" });
        _site.Set("site.test", "/real.zip", SiteResource.File(77));
        _site.Page("/landing.html", "<a href=\"/x.zip\">x</a>", "other.test");
        var (run, _) = await RunAsync(Project());
        Assert.DoesNotContain(_site.Requests, r => r.Host == "other.test");
        Assert.Equal(77, Assert.Single(run.Items).Size);
    }

    // ------------------------------------------------------------------ hostile servers (security review)

    [Fact]
    public void HostileCss_IsHandledQuickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var openUrls = string.Concat(Enumerable.Repeat("url(", 500_000));
        Assert.Empty(CssLinks.Find(openUrls, new Uri("http://site.test/")));
        var openComments = string.Concat(Enumerable.Repeat("/* ", 500_000)) + "a{b:url(x.png)}";
        Assert.Empty(CssLinks.Find(openComments, new Uri("http://site.test/")));
        Assert.Equal(["http://site.test/y.png"],
            CssLinks.Find("a{b:url(y.png)} /* never closed url(z.png)", new Uri("http://site.test/")).Select(l => l.Url.AbsoluteUri));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("Infinity")]
    public void HugeRetryAfter_DoesNotOverflow(string value)
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);
        Assert.Equal(TimeSpan.FromDays(1), HostThrottle.RetryAfter(response, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task RobotsTxt_RedirectedOutsideTheProject_IsNotFollowed()
    {
        _site.Set("site.test", "/robots.txt", new SiteResource { Location = "http://internal.test/admin/robots.txt" });
        _site.Set("internal.test", "/admin/robots.txt", new SiteResource { Body = "User-agent: *\nDisallow: /\n"u8.ToArray(), ContentType = "text/plain" });
        _site.Page("/", """<a href="/a.zip">a</a>""");
        _site.Set("site.test", "/a.zip", SiteResource.File(10));
        var (run, _) = await RunAsync(Project());
        Assert.DoesNotContain(_site.Requests, r => r.Host == "internal.test");
        Assert.Single(run.Items); // treated as no robots.txt
    }

    [Fact]
    public void APageSavedEarlierAsAnotherType_GetsAnHtmlName()
    {
        var root = Path.Combine(_dir, "root");
        var saved = new LocalPaths(root).Map(new Uri("http://site.test/x.hta"), "application/octet-stream");
        Assert.Equal(@"site.test\x.hta", saved);
        var previous = new[] { new KeyValuePair<string, string>("http://site.test/x.hta", saved) };
        Assert.Equal(@"site.test\x.hta.html", new LocalPaths(root, previous).Map(new Uri("http://site.test/x.hta"), "text/html"));
        Assert.Equal(@"site.test\x.hta", new LocalPaths(root, previous).Map(new Uri("http://site.test/x.hta"), "application/octet-stream"));
    }

    // ------------------------------------------------------------------ findings of the code review

    [Fact]
    public async Task RobotsTxtUnreachable_DoesNotReportFilesAsRemoved()
    {
        _site.Page("/", """<a href="/1.zip">1</a>""");
        _site.Set("site.test", "/1.zip", SiteResource.File(10, etag: "\"a\""));
        var project = _grabber.Save(Project());
        var (first, _) = await RunAsync(project);
        _grabber.Send(project.Id, first.Items.ToList(), null, null, null, startQueue: false);

        _site.Set("site.test", "/robots.txt", new SiteResource { Status = 503 });
        var (second, result) = await RunAsync(project);
        Assert.Equal(0, result.Removed);
        Assert.Null(_grabber.Files(project.Id)["http://site.test/1.zip"].RemovedUtc);
    }

    [Fact]
    public async Task ABodyThatStalls_FailsAfterTheTimeout_AndTheRunEnds()
    {
        _site.Page("/", """<a href="/stuck.html">s</a> <a href="/ok.zip">o</a>""");
        _site.Set("site.test", "/stuck.html", new SiteResource { Body = new byte[5000], ContentType = "text/html", Stall = true });
        _site.Set("site.test", "/ok.zip", SiteResource.File(10));
        var project = Project();
        project.RespectRobots = false;
        var engine = new GrabberEngine(project, _site.CreateClient(new System.Net.CookieContainer()), new Dictionary<string, GrabberFileState>(),
            "Test/1.0", new GrabberEngineOptions { RequestTimeout = TimeSpan.FromSeconds(1), RetryBaseDelay = TimeSpan.FromMilliseconds(10) });
        var result = await engine.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, result.Files);
        Assert.Contains(engine.Items, i => i.Url.EndsWith("/stuck.html") && i.Status == GrabberItemStatus.Failed);
    }

    [Fact]
    public void Css_IsSavedAsUtf8_WithItsCharsetRuleToo()
    {
        Assert.Equal("@charset \"utf-8\"; p{content:\"é\"}", HtmlText.CssAsUtf8("@charset \"iso-8859-1\"; p{content:\"é\"}"));
        Assert.Equal("p{}", HtmlText.CssAsUtf8("p{}"));
        Assert.Equal("é", HtmlText.Decode([0xE9], 1, "text/css; charset=iso-8859-1"));
    }
}
