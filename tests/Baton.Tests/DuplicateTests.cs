using System.Security.Cryptography;
using System.Text;
using Baton.Core;
using Baton.Core.Duplicates;
using Baton.Core.Portable;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Duplicate detection: the three rules, the keyed history, the entries that cannot ask, and the server's answer.</summary>
public sealed class DuplicateTests : IAsyncLifetime
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

    private DownloadStore Store(DuplicateAction action = DuplicateAction.Ask)
    {
        var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None, DuplicateAction = action });
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

    private static Task<DownloadSnapshot> Finished(DownloadService service, long id) =>
        WaitFor(() => service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed).ContinueWith(_ => service.GetSnapshot(id)!);

    private static DownloadRecord Record(string url, string? name = null, string? etag = null, string? lastModified = null, long? size = null,
        DownloadStatus status = DownloadStatus.Completed, long id = 1) =>
        new() { Id = id, Url = url, Directory = @"C:\D", FileName = name, ETag = etag, LastModified = lastModified, TotalSize = size, Status = status };

    // ------------------------------------------------------------------ the rules

    [Theory]
    [InlineData("https://a.test/x.zip#part-2")]
    [InlineData("https://user:secret@A.TEST/x.zip")]
    public void SameAddress_IgnoresFragmentLoginAndHostCase(string again)
    {
        var match = DuplicateDetector.CheckBeforeAdd(again, @"C:\D", null, [Record("https://a.test/x.zip", "x.zip")], null, _ => false);

        Assert.Equal(DuplicateReason.SameUrl, match!.Reason);
        Assert.Equal(1, match.ExistingId);
    }

    [Fact]
    public void AnotherAddress_IsNotADuplicate_AndAnInvalidOneNeverMatches()
    {
        var items = new[] { Record("https://a.test/x.zip", "x.zip") };

        Assert.Null(DuplicateDetector.CheckBeforeAdd("https://a.test/y.zip", @"C:\D", "y.zip", items, null, _ => false));
        Assert.Null(DuplicateDetector.CheckBeforeAdd("not a url", @"C:\D", "x.zip", items, null, _ => true));
    }

    [Fact]
    public void SameNameInTheFolder_IsFound_OnlyWhereTheFileExists()
    {
        var path = Path.Combine(@"C:\D", "movie.mkv");

        var found = DuplicateDetector.CheckBeforeAdd("https://b.test/movie.mkv", @"C:\D", "movie.mkv", [], null, p => p == path);
        var elsewhere = DuplicateDetector.CheckBeforeAdd("https://b.test/movie.mkv", @"C:\Other", "movie.mkv", [], null, p => p == path);

        Assert.Equal(DuplicateReason.SameName, found!.Reason);
        Assert.Equal(path, found.ExistingPath);
        Assert.Null(elsewhere);
    }

    [Fact]
    public void TheServersAnswer_NeedsTheSameSizeAndTheSameEtag_NeverTheSizeAlone()
    {
        var done = new[] { Record("https://a.test/x.zip", "x.zip", etag: "\"v1\"", size: 5000) };
        ProbeResult Answer(long? size, string? etag, string? lm = null) => new(size, true, etag, lm, "y.zip");

        Assert.Equal(DuplicateReason.SameFile, DuplicateDetector.CheckProbe(Answer(5000, "\"v1\""), 99, done, null, _ => true)!.Reason);
        Assert.Null(DuplicateDetector.CheckProbe(Answer(5000, "\"v2\""), 99, done, null, _ => true));   // another version
        Assert.Null(DuplicateDetector.CheckProbe(Answer(5000, null), 99, done, null, _ => true));       // nothing to compare but the size
        Assert.Null(DuplicateDetector.CheckProbe(Answer(5001, "\"v1\""), 99, done, null, _ => true));   // another size
        Assert.Null(DuplicateDetector.CheckProbe(Answer(5000, "\"v1\""), 99, done, null, _ => false));  // the file is gone
        Assert.Null(DuplicateDetector.CheckProbe(Answer(5000, "\"v1\""), 1, done, null, _ => true));    // never itself
    }

    [Fact]
    public void WithoutAnEtag_TheLastModifiedDecides()
    {
        var done = new[] { Record("https://a.test/x.zip", "x.zip", lastModified: "Mon, 01 Jan 2026 00:00:00 GMT", size: 10) };

        Assert.NotNull(DuplicateDetector.CheckProbe(new ProbeResult(10, false, null, "Mon, 01 Jan 2026 00:00:00 GMT", "z"), 9, done, null, _ => true));
        Assert.Null(DuplicateDetector.CheckProbe(new ProbeResult(10, false, null, "Tue, 02 Jan 2026 00:00:00 GMT", "z"), 9, done, null, _ => true));
    }

    // ------------------------------------------------------------------ the history

    [Fact]
    public void History_HoldsAKeyedHash_NotTheAddress_AndNothingInTheClear()
    {
        const string url = "https://secret.example/private/report.pdf";
        string hash;
        using (var store = Store())
        {
            store.AddHistory(url, "report.pdf", @"C:\D\report.pdf", 1234, "\"e\"", null, DateTime.UtcNow);
            hash = store.FindByUrl(url).Single().UrlHash;
            Assert.Equal(64, hash.Length);
            // A hash without a key could be matched against a list of well-known addresses: this one cannot.
            Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))), hash);
        }
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_dir, "state.db*"))
            Assert.DoesNotContain("secret.example", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.OrdinalIgnoreCase);

        using var reopened = new DownloadStore(DbPath);
        Assert.Equal(hash, reopened.FindByUrl(url).Single().UrlHash); // the key survives a restart
    }

    [Fact]
    public void ClearingTheHistory_AlsoReplacesTheKey()
    {
        using var store = Store();
        const string url = "https://a.test/f.zip";
        store.AddHistory(url, "f.zip", @"C:\D\f.zip", 10, null, null, DateTime.UtcNow);
        var before = store.FindByUrl(url).Single().UrlHash;

        store.ClearHistory();
        Assert.Equal(0, store.HistoryCount());
        store.AddHistory(url, "f.zip", @"C:\D\f.zip", 10, null, null, DateTime.UtcNow);

        Assert.NotEqual(before, store.FindByUrl(url).Single().UrlHash);
    }

    [Fact]
    public void Prune_DropsMissingFiles_OldEntries_AndWhatPassesTheLimit()
    {
        using var store = Store();
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        store.AddHistory("https://a.test/gone", "gone", @"C:\D\gone", 1, null, null, now);
        store.AddHistory("https://a.test/old", "old", @"C:\D\old", 1, null, null, now.AddDays(-400));
        store.AddHistory("https://a.test/kept", "kept", @"C:\D\kept", 1, null, null, now);

        var removed = store.PruneHistory(now, path => path != @"C:\D\gone");

        Assert.Equal(2, removed);
        Assert.Equal("kept", store.FindBySize(1).Single().FileName);
    }

    [Fact]
    public void Prune_KeepsTheNewest5000()
    {
        using var store = Store();
        var now = DateTime.UtcNow;
        for (var i = 0; i < DownloadStore.HistoryMaxEntries + 3; i++)
            store.AddHistory($"https://a.test/{i}", $"f{i}", $@"C:\D\f{i}", 7, null, null, now.AddSeconds(i));

        Assert.Equal(3, store.PruneHistory(now.AddHours(1), _ => true));
        Assert.Equal(DownloadStore.HistoryMaxEntries, store.HistoryCount());
        Assert.DoesNotContain(store.FindBySize(7), e => e.FileName == "f0"); // the oldest went
    }

    [Fact]
    public void TheHistory_IsNotPartOfAnExport()
    {
        Assert.DoesNotContain(typeof(PortableFile).GetProperties(), p => p.Name.Contains("History", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ExportedSettings).GetProperties(), p => p.Name.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ the entries that cannot ask

    [Theory]
    [InlineData(DuplicateAction.Ask)]
    [InlineData(DuplicateAction.Overwrite)]
    [InlineData(DuplicateAction.OpenExisting)]
    [InlineData(DuplicateAction.Skip)]
    public async Task AutomaticAdd_SkipsAndReportsADuplicate_NeverOverwrites(DuplicateAction action)
    {
        var url = _server.Add("auto.bin", new ServedFile { Content = TestFileServer.RandomBytes(100_000, 1) });
        using var store = Store(action);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var first = service.AddAutomatic(url);
        var second = service.AddAutomatic(url + "#again");

        Assert.NotNull(first.Id);
        Assert.Null(second.Id);
        Assert.Equal(DuplicateReason.SameUrl, second.Duplicate!.Reason);
        Assert.Equal(first.Id, second.Duplicate.ExistingId); // the reference to the download already there
        Assert.Single(service.GetSnapshots());
    }

    [Fact]
    public async Task AutomaticAdd_AddsAnyway_WhenAskedTo_OrWhenTheRememberedChoiceIsToAddNumbered()
    {
        var url = _server.Add("auto2.bin", new ServedFile { Content = TestFileServer.RandomBytes(100_000, 2) });
        using var store = Store(DuplicateAction.Ask);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        Assert.NotNull(service.AddAutomatic(url).Id);

        var forced = service.AddAutomatic(url, allowDuplicate: true);
        Assert.NotNull(forced.Id);
        Assert.NotNull(forced.Duplicate); // still reported

        service.UpdateSettings(new AppSettings { DownloadFolder = _dir, ProxyMode = ProxyMode.None, DuplicateAction = DuplicateAction.AddNumbered });
        Assert.NotNull(service.AddAutomatic(url).Id);
        Assert.Equal(3, service.GetSnapshots().Count);
    }

    // ------------------------------------------------------------------ the server's answer, before any byte is written

    [Fact]
    public async Task SameSizeAndEtagFromAnotherAddress_StopsBeforeAnyFileIsCreated_AndCanContinue()
    {
        var content = TestFileServer.RandomBytes(200_000, 7);
        var first = _server.Add("first.bin", new ServedFile { Content = content });
        var second = _server.Add("second.bin", new ServedFile { Content = content });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        Assert.Equal(DownloadStatus.Completed, (await Finished(service, service.Add(first))).Status);
        await service.ClearCompletedAsync(); // out of the list, kept in the history
        Assert.Empty(service.GetSnapshots());
        DuplicateNotice? notice = null;
        service.DuplicateFound += n => notice = n;

        var id = service.Add(second);
        await WaitFor(() => notice is not null);

        Assert.Equal(DuplicateReason.SameFile, notice!.Match.Reason);
        Assert.Equal(DownloadStatus.Paused, service.GetSnapshot(id)!.Status);
        Assert.Empty(Directory.GetFiles(_dir, "*.part", SearchOption.AllDirectories)); // nothing was written
        Assert.Contains("first.bin", service.GetSnapshot(id)!.Error);

        service.ContinueDespiteDuplicate(id); // "Download anyway"
        Assert.Equal(DownloadStatus.Completed, (await Finished(service, id)).Status);
        Assert.Equal(2, Directory.GetFiles(_dir, "*.bin", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task SameSizeButAnotherEtag_IsNotADuplicate()
    {
        var content = TestFileServer.RandomBytes(200_000, 8);
        var first = _server.Add("v1.bin", new ServedFile { Content = content, ETag = "\"v1\"" });
        var second = _server.Add("v2.bin", new ServedFile { Content = content, ETag = "\"v2\"" });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        await Finished(service, service.Add(first));

        var done = await Finished(service, service.Add(second));

        Assert.Equal(DownloadStatus.Completed, done.Status);
    }

    [Fact]
    public async Task RememberedChoiceToAddNumbered_DownloadsWithoutStopping_AndSkipRemovesIt()
    {
        var content = TestFileServer.RandomBytes(150_000, 9);
        var first = _server.Add("one.bin", new ServedFile { Content = content });
        var second = _server.Add("two.bin", new ServedFile { Content = content });
        var third = _server.Add("three.bin", new ServedFile { Content = content });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        await Finished(service, service.Add(first));

        service.UpdateSettings(new AppSettings { DownloadFolder = _dir, ProxyMode = ProxyMode.None, DuplicateAction = DuplicateAction.AddNumbered });
        Assert.Equal(DownloadStatus.Completed, (await Finished(service, service.Add(second))).Status);

        service.UpdateSettings(new AppSettings { DownloadFolder = _dir, ProxyMode = ProxyMode.None, DuplicateAction = DuplicateAction.Skip });
        var skipped = service.Add(third);
        await WaitFor(() => service.GetSnapshot(skipped) is null); // not downloaded, not kept in the list
        Assert.Empty(Directory.GetFiles(_dir, "three*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ClearCompleted_RemembersByDefault_AndForgetsOnRequest_OrWhenTheSettingIsOff()
    {
        var content = TestFileServer.RandomBytes(60_000, 10);
        var url = _server.Add("mem.bin", new ServedFile { Content = content });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        await Finished(service, service.Add(url));
        await service.ClearCompletedAsync();
        Assert.Equal(1, store.HistoryCount());
        Assert.NotNull(service.FindDuplicate(url)); // in the history, with the file still there

        await Finished(service, service.Add(url, skipDuplicateCheck: true));
        await service.ClearCompletedAsync(forgetHistory: true);
        Assert.Equal(0, store.HistoryCount());

        service.UpdateSettings(new AppSettings { DownloadFolder = _dir, ProxyMode = ProxyMode.None, RememberRemovedForDuplicates = false });
        await Finished(service, service.Add(url, skipDuplicateCheck: true));
        await service.ClearCompletedAsync();
        Assert.Equal(0, store.HistoryCount());
    }
}
