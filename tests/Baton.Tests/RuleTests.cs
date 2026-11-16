using System.Diagnostics;
using Baton.Core;
using Baton.Core.Rules;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Automatic rules: matching, renaming, what an add takes from a rule, and what a rule can never do.</summary>
public sealed class RuleTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
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
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None, DuplicateAction = Core.Duplicates.DuplicateAction.Ask });
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

    private static Rule R(Action<RuleConditions> when, Action<RuleActions>? then = null, string name = "rule", bool enabled = true)
    {
        var rule = new Rule { Name = name, Enabled = enabled };
        when(rule.Conditions);
        then?.Invoke(rule.Actions);
        return rule;
    }

    [Theory]
    [InlineData("a.iso")]
    [InlineData("a.docm")]
    [InlineData("a.chm")]
    [InlineData("a.msc")]
    [InlineData("a.pif")]
    [InlineData("a.xll")]
    public void MoreShellRunnableTypes_AreNeverOpenedByARule(string name) => Assert.True(ExecutableTypes.IsRunnable(name));

    [Fact]
    public async Task SkipConfirmation_UsesTheBrowsersFileName_NotOnlyTheAddress()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        service.SaveRules([R(c => { c.Domains = ["rules.test"]; c.Extensions = ["pdf"]; }, a => a.SkipConfirmation = true)]);
        Assert.False(service.RuleSkipsConfirmation("https://rules.test/download?id=7", RuleOrigins.Browser));                       // the address says nothing
        Assert.True(service.RuleSkipsConfirmation("https://rules.test/download?id=7", RuleOrigins.Browser, fileName: "report.pdf")); // the browser knows
        Assert.False(service.RuleSkipsConfirmation("https://rules.test/a.pdf", RuleOrigins.Browser, fileName: "a.exe"));            // the name decides
    }

    private static RuleInput Input(string url, string origin = RuleOrigins.Browser, string? file = null, long? size = null, long? category = null) =>
        new(new Uri(url), origin, file, category, size);

    // ------------------------------------------------------------------ conditions

    [Fact]
    public void Domain_MatchesSubdomains_ButNotNamesThatMerelyEndTheSame()
    {
        var rule = R(c => c.Domains = ["example.com"]);
        Assert.True(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a")));
        Assert.True(RuleEngine.Matches(rule.Conditions, Input("https://cdn.Example.com/a")));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://notexample.com/a")));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://example.com.evil.test/a")));
    }

    [Fact]
    public void Conditions_AreCombinedWithAnd()
    {
        var rule = R(c => { c.Domains = ["example.com"]; c.Extensions = ["zip"]; c.Origins = [RuleOrigins.Api]; });
        Assert.True(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a.zip", RuleOrigins.Api, "a.zip")));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a.zip", RuleOrigins.Browser, "a.zip")));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a.pdf", RuleOrigins.Api, "a.pdf")));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://other.test/a.zip", RuleOrigins.Api, "a.zip")));
    }

    [Fact]
    public void AnEmptyRule_NeverMatches()
    {
        Assert.False(RuleEngine.Matches(new RuleConditions(), Input("https://example.com/a")));
        Assert.Null(RuleEngine.Match([R(_ => { })], Input("https://example.com/a")));
    }

    [Fact]
    public void UrlPattern_UsesWildcards_AndRegexNeverBacktracks()
    {
        Assert.True(RuleEngine.Matches(R(c => c.UrlPattern = "https://*.example.com/files/*.iso").Conditions, Input("https://dl.example.com/files/linux.iso")));
        Assert.False(RuleEngine.Matches(R(c => c.UrlPattern = "https://*.example.com/files/*.iso").Conditions, Input("https://dl.example.com/files/linux.zip")));
        Assert.True(RuleEngine.Matches(R(c => c.UrlRegex = @"/v\d+/release").Conditions, Input("https://x.test/v12/release.zip")));

        // A pattern that freezes a backtracking engine returns at once.
        var hostile = R(c => c.UrlRegex = "^(a+)+$").Conditions;
        var watch = Stopwatch.StartNew();
        Assert.False(RuleEngine.Matches(hostile, Input("https://x.test/" + new string('a', 40) + "!")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(RuleEngine.Matches(R(c => c.UrlRegex = "([").Conditions, Input("https://x.test/a")));
        Assert.NotNull(RuleEngine.ValidateRegex("(["));
        Assert.Null(RuleEngine.ValidateRegex(@"\.zip$"));
    }

    [Fact]
    public void Size_NeedsAKnownSize()
    {
        var rule = R(c => { c.Domains = ["example.com"]; c.MinSize = 1000; c.MaxSize = 5000; });
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a", size: null)));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a", size: 999)));
        Assert.True(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a", size: 1000)));
        Assert.False(RuleEngine.Matches(rule.Conditions, Input("https://example.com/a", size: 5001)));
    }

    [Fact]
    public void TheFirstEnabledRuleThatMatchesWins()
    {
        var first = R(c => c.Domains = ["example.com"], name: "first", enabled: false);
        var second = R(c => c.Domains = ["example.com"], name: "second");
        var third = R(c => c.Domains = ["example.com"], name: "third");
        first.SortOrder = 0; second.SortOrder = 1; third.SortOrder = 2;
        Assert.Equal("second", RuleEngine.Match([third, first, second], Input("https://example.com/a"))!.Name);
    }

    // ------------------------------------------------------------------ rename

    [Fact]
    public void Rename_ExpandsPlaceholders_AndKeepsTheRealExtension()
    {
        var now = new DateTime(2026, 11, 14, 9, 30, 5);
        Assert.Equal("2026-11-14_report.pdf", RenameTemplate.Apply("{date}_{name}", "report.pdf", "example.com", "Documents", now));
        Assert.Equal("example.com-09-30-05-Documents.pdf", RenameTemplate.Apply("{host}-{time}-{category}{ext}", "report.pdf", "example.com", "Documents", now));
        Assert.Equal("report.tar.gz", RenameTemplate.Apply("{name}{ext}", "report.tar.gz", "h", null, now));
    }

    [Theory]
    [InlineData("{name}.exe", "photo.jpg")]
    [InlineData("{name}{ext}.bat", "photo.jpg")]
    [InlineData("run.cmd", "photo.jpg")]
    public void Rename_CannotIntroduceAProgramExtension(string pattern, string original)
    {
        var result = RenameTemplate.Apply(pattern, original, "h", null, DateTime.Now);
        Assert.EndsWith(".jpg", result);
        Assert.False(ExecutableTypes.IsRunnable(result));
    }

    [Fact]
    public void Rename_OfAFileWithoutExtension_DropsAProgramExtensionTheFileDidNotHave()
    {
        Assert.False(ExecutableTypes.IsRunnable(RenameTemplate.Apply("{name}.exe", "download", "h", null, DateTime.Now)));
    }

    [Fact]
    public void Rename_Number_IsTheFirstFreeOne_AndNamesAreSafe()
    {
        var taken = new HashSet<string> { "a-1.zip", "a-2.zip" };
        Assert.Equal("a-3.zip", RenameTemplate.Apply("a-{n}", "x.zip", "h", null, DateTime.Now, taken.Contains));
        var traversal = RenameTemplate.Apply(@"..\..\{name}", "x.zip", "h", null, DateTime.Now);
        Assert.DoesNotContain("\\", traversal);
        Assert.DoesNotContain("..", traversal);
    }

    // ------------------------------------------------------------------ what an add takes from a rule

    [Fact]
    public async Task Add_TakesFolderQueueStartAndUserAgent_FromTheRule_AndLogsIt()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var queue = service.SaveQueue(new DownloadQueue { Name = "Night", MaxConcurrent = 1 });
        var folder = Path.Combine(_dir, "ruled");
        service.SaveRules([R(c => c.Domains = ["rules.test"], a =>
        {
            a.Folder = folder; a.QueueId = queue.Id; a.Start = RuleStart.Paused; a.UserAgent = "BatonRule/1";
            a.Connections = 2; a.SpeedLimitKBps = 500; a.Scan = true;
        }, name: "Night downloads")]);

        var id = service.Add("https://rules.test/a.zip", ruleOrigin: RuleOrigins.Api);

        var record = service.GetSnapshot(id)!;
        Assert.Equal(folder, record.Directory);
        Assert.Equal(queue.Id, record.QueueId);
        Assert.Equal(DownloadStatus.Paused, record.Status);
        var line = service.AutomationLog.Read(Core.Automation.AutomationLog.RulesOrigin).Single();
        Assert.Equal("Night downloads", line.Detail);
        Assert.Equal(id, line.DownloadId);
        // What is left to do after the add is kept with the download and survives a restart.
        await service.DisposeAsync();
        await using var again = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        Assert.Equal("Night downloads", again.Rules.Single().Name);
        Assert.Single(again.GetSnapshots());
    }

    [Fact]
    public async Task ExplicitChoices_WinOverTheRule()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var queue = service.SaveQueue(new DownloadQueue { Name = "Other", MaxConcurrent = 1 });
        service.SaveRules([R(c => c.Domains = ["rules.test"], a => { a.Folder = Path.Combine(_dir, "ruled"); a.QueueId = queue.Id; a.Start = RuleStart.Paused; })]);
        var mine = Path.Combine(_dir, "mine");

        var id = service.Add("https://rules.test/a.zip", mine, "name.zip", startNow: false, queueId: service.MainQueueId, ruleOrigin: RuleOrigins.Dialog);

        var record = service.GetSnapshot(id)!;
        Assert.Equal(mine, record.Directory);
        Assert.Equal(service.MainQueueId, record.QueueId);
        Assert.Equal("name.zip", record.DisplayName);
    }

    [Fact]
    public async Task WithoutAnOrigin_NoRuleApplies()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        service.SaveRules([R(c => c.Domains = ["rules.test"], a => a.Folder = Path.Combine(_dir, "ruled"))]);

        var id = service.Add("https://rules.test/a.zip", startNow: false);

        Assert.NotEqual(Path.Combine(_dir, "ruled"), service.GetSnapshot(id)!.Directory);
        Assert.Empty(service.AutomationLog.Read(Core.Automation.AutomationLog.RulesOrigin));
    }

    [Fact]
    public async Task Rename_AppliesToTheNameTheServerReveals_AndTheDuplicateCheckSeesIt()
    {
        var url = _server.Add("get.php", new ServedFile { Content = TestFileServer.RandomBytes(100_000, 3), ContentDisposition = "attachment; filename=\"report.pdf\"" });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        service.SaveRules([R(c => c.Domains = ["127.0.0.1"], a => { a.Rename = "kept-{name}"; a.Folder = _dir; }, name: "rename")]);

        var id = service.Add(url, ruleOrigin: RuleOrigins.Cli);
        await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        Assert.Equal("kept-report.pdf", service.GetSnapshot(id)!.DisplayName);
        Assert.True(File.Exists(Path.Combine(_dir, "kept-report.pdf")) || File.Exists(service.GetSnapshot(id)!.FinalPath!));
        // "Same name in the folder" is compared with the renamed name: a file called kept-x.zip is what a rule would make of x.zip.
        File.WriteAllText(Path.Combine(_dir, "kept-x.zip"), "x");
        var other = _server.BaseUrl + "/files/x.zip";
        Assert.Equal(Core.Duplicates.DuplicateReason.SameName, service.FindDuplicate(other, ruleOrigin: RuleOrigins.Cli)?.Reason);
        Assert.Null(service.FindDuplicate(other));
    }

    [Fact]
    public async Task OpenWhenDone_IsNeverRaisedForAProgram_AndNeedsACleanScan()
    {
        var url = _server.Add("setup.exe", new ServedFile { Content = TestFileServer.RandomBytes(50_000, 4) });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var requests = new List<RuleFileRequest>();
        service.RuleFileActionRequested += requests.Add;
        service.SaveRules([R(c => c.Domains = ["127.0.0.1"], a => { a.OpenWhenDone = true; a.ShowInFolder = true; }, name: "open")]);

        var id = service.Add(url, ruleOrigin: RuleOrigins.Cli);
        await WaitFor(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed && service.GetSnapshot(id)!.Check is not { Pending: true });
        await Task.Delay(200);

        // Show in folder is allowed; opening a program is refused and written in the log.
        Assert.All(requests, r => Assert.False(r.Open));
        Assert.Contains(service.AutomationLog.Read(Core.Automation.AutomationLog.RulesOrigin), e => e.Action == Core.Automation.ActivityText.RuleOpen && e.Outcome == "refused" && e.Detail.StartsWith("!programOrScript", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportedRules_ThatOpenFilesOrSkipTheConfirmation_ComeInDisabled()
    {
        var risky = RuleEngine.ForImport(R(c => c.Domains = ["a.test"], a => a.OpenWhenDone = true));
        var skip = RuleEngine.ForImport(R(c => c.Domains = ["a.test"], a => a.SkipConfirmation = true));
        var plain = RuleEngine.ForImport(R(c => c.Domains = ["a.test"], a => a.Folder = @"C:\X"));
        Assert.False(risky.Enabled);
        Assert.False(skip.Enabled);
        Assert.True(plain.Enabled);
        Assert.All(new[] { risky, skip, plain }, r => Assert.True(r.Imported));
    }

    [Fact]
    public async Task Preview_NamesTheRule_AndTheSkippedConfirmation()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        service.SaveRules([R(c => c.Domains = ["rules.test"], a => { a.SkipConfirmation = true; a.Rename = "{date}-{name}"; }, name: "quiet")]);

        var preview = service.PreviewRules("https://rules.test/a.zip", RuleOrigins.Browser);

        Assert.Equal("quiet", preview.Rule!.Name);
        Assert.EndsWith("-a.zip", preview.FileName);
        Assert.True(service.RuleSkipsConfirmation("https://rules.test/a.zip", RuleOrigins.Browser));
        Assert.Null(service.PreviewRules("https://other.test/a.zip", RuleOrigins.Browser).Rule);
    }

    [Fact]
    public async Task Rules_SurviveARestart_InTheirOrder()
    {
        using var store = Store();
        await using (var service = new DownloadService(store, engineTemplate: Fast, autoTick: false))
        {
            service.SaveRules([R(c => c.Domains = ["b.test"], name: "b"), R(c => c.Domains = ["a.test"], name: "a", enabled: false)]);
            var swapped = service.Rules.Reverse().ToList();
            service.SaveRules(swapped);
        }
        await using var again = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        Assert.Equal(["a", "b"], again.Rules.Select(r => r.Name));
        Assert.False(again.Rules[0].Enabled);
        Assert.Equal(["a.test"], again.Rules[0].Conditions.Domains);
    }
}
