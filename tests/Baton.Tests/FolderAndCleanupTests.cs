using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.PostDownload;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>1.3.4: the default folder chosen by the user always wins (category subfolders optional), and "Clear completed".</summary>
public sealed class FolderAndCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));

    // The test user's Desktop: a folder of its own, never the real one.
    private string Desktop => Path.Combine(_dir, "Desktop");

    public FolderAndCleanupTests() => Directory.CreateDirectory(Desktop);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private DownloadStore Store(AppSettings? settings = null)
    {
        var store = new DownloadStore(Path.Combine(_dir, "state.db"));
        store.SaveSettings(settings ?? new AppSettings { DownloadFolder = Desktop, ProxyMode = ProxyMode.None });
        return store;
    }

    private static Category Of(DownloadService service, string key) => service.Categories.Single(c => c.Key == key);

    [Fact]
    public void ResolveFolder_RelativeFoldersAreSubfoldersOnlyWithTheOption_AbsoluteOnesAlwaysWin()
    {
        var video = new Category { Key = Categories.VideoKey, Name = "Video", Folder = "Video" };
        var own = new Category { Name = "Films", Folder = @"D:\Films" };
        var general = new Category { Key = Categories.GeneralKey, Name = "General", Folder = "" };
        var on = new AppSettings { DownloadFolder = @"C:\Users\x\Desktop" };
        var off = new AppSettings { DownloadFolder = @"C:\Users\x\Desktop", CategorySubfolders = false };

        Assert.Equal(@"C:\Users\x\Desktop\Video", video.ResolveFolder(on));
        Assert.Equal(@"C:\Users\x\Desktop", video.ResolveFolder(off));
        Assert.Equal(@"D:\Films", own.ResolveFolder(on));
        Assert.Equal(@"D:\Films", own.ResolveFolder(off));
        Assert.Equal(@"C:\Users\x\Desktop", general.ResolveFolder(off));
        Assert.True(own.OverridesDefault(on.DownloadFolder));
        Assert.False(video.OverridesDefault(on.DownloadFolder));
        Assert.False(new Category { Folder = @"C:\Users\x\Desktop\" }.OverridesDefault(on.DownloadFolder)); // the same folder
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DesktopAsDefaultFolder_IsUsedForEveryCategory_WithOrWithoutSubfolders(bool subfolders)
    {
        using var store = Store(new AppSettings { DownloadFolder = Desktop, CategorySubfolders = subfolders, ProxyMode = ProxyMode.None });
        await using var service = new DownloadService(store, autoTick: false);
        await service.StopQueueAsync();

        foreach (var (file, key) in new[] { ("a.zip", Categories.CompressedKey), ("b.pdf", Categories.DocumentsKey), ("c.mp4", Categories.VideoKey), ("d", Categories.GeneralKey) })
        {
            var id = service.Add($"https://example.test/{file}", startNow: false);
            var expected = subfolders && Of(service, key).Folder.Length > 0 ? Path.Combine(Desktop, Of(service, key).Folder) : Desktop;
            Assert.Equal(expected, service.GetSnapshot(id)!.Directory);
        }
    }

    [Fact]
    public async Task CategoryWithAnAbsoluteFolder_OverridesTheDefault_UntilResetBringsItBack()
    {
        using var store = Store();
        await using var service = new DownloadService(store, autoTick: false);
        await service.StopQueueAsync();
        // What the 1.0 "move off the Desktop" left in the user's categories.
        var moved = Path.Combine(_dir, "Downloads", "DownloadManager");
        foreach (var key in new[] { Categories.CompressedKey, Categories.VideoKey })
        {
            var category = Of(service, key);
            category.Folder = moved;
            service.SaveCategory(category);
        }
        Assert.Equal(moved, service.GetSnapshot(service.Add("https://example.test/film.mp4", startNow: false))!.Directory);
        Assert.Equal(2, service.Categories.Count(c => c.OverridesDefault(Desktop)));

        Assert.Equal(2, service.ResetCategoryFolders());
        Assert.DoesNotContain(service.Categories, c => c.OverridesDefault(Desktop));
        Assert.Equal(Path.Combine(Desktop, Of(service, Categories.VideoKey).Folder),
            service.GetSnapshot(service.Add("https://example.test/film2.mp4", startNow: false))!.Directory);
        Assert.Equal(Categories.Defaults().Single(c => c.Key == Categories.VideoKey).Folder, Of(service, Categories.VideoKey).Folder);
    }

    [Fact]
    public async Task ChangedSettings_ApplyToTheNextDownloadAndStream_WithoutRestart_AndAreSaved()
    {
        using (var store = Store())
        {
            await using var service = new DownloadService(store, autoTick: false);
            await service.StopQueueAsync();
            var other = Path.Combine(_dir, "Elsewhere");
            var settings = service.Settings;
            settings.DownloadFolder = other;
            settings.CategorySubfolders = false;
            settings.ClearCompletedAfterDays = 999;
            service.UpdateSettings(settings);

            Assert.Equal(other, service.GetSnapshot(service.Add("https://example.test/a.zip", startNow: false))!.Directory);
            var stream = service.AddMedia(new MediaRequest("https://example.test/live/index.m3u8", Title: "Concert", VariantId: "hls:0"), "hls", "720p", startNow: false);
            Assert.Equal(other, service.GetSnapshot(stream)!.Directory);
            Assert.Equal(365, service.Settings.ClearCompletedAfterDays);
        }
        using var reopened = new DownloadStore(Path.Combine(_dir, "state.db"));
        var saved = reopened.LoadSettings();
        Assert.False(saved.CategorySubfolders);
        Assert.Equal(365, saved.ClearCompletedAfterDays);
        Assert.True(new AppSettings().CategorySubfolders); // on by default
        Assert.Equal(0, new AppSettings().ClearCompletedAfterDays); // automatic clean-up off by default
    }

    /// <summary>Completed (two in the main queue, one in another), completed with a check still running, paused, queued, failed.</summary>
    private (DownloadStore Store, long OtherQueue) Seed(DateTime completedUtc)
    {
        var store = Store();
        var categories = store.LoadCategories();
        long Cat(string key) => categories.Single(c => c.Key == key).Id;
        var other = new DownloadQueue { Name = "Night" };
        store.SaveQueue(other);
        void Insert(string name, DownloadStatus status, string category, long? queue = null, PostCheck? check = null)
        {
            var path = Path.Combine(Desktop, name);
            if (status == DownloadStatus.Completed)
                File.WriteAllBytes(path, new byte[1000]);
            store.Insert(new DownloadRecord
            {
                Url = "https://example.test/" + name, Directory = Desktop, FileName = name, TotalSize = 1000, Status = status,
                AddedUtc = completedUtc.AddMinutes(-1), CompletedUtc = status == DownloadStatus.Completed ? completedUtc : null,
                CategoryId = Cat(category), QueueId = queue, Check = check,
            });
        }
        Insert("a.zip", DownloadStatus.Completed, Categories.CompressedKey);
        Insert("b.pdf", DownloadStatus.Completed, Categories.DocumentsKey);
        Insert("c.zip", DownloadStatus.Completed, Categories.CompressedKey, other.Id);
        Insert("scanning.zip", DownloadStatus.Completed, Categories.CompressedKey, check: new PostCheck { Pending = true });
        Insert("paused.zip", DownloadStatus.Paused, Categories.CompressedKey);
        Insert("queued.zip", DownloadStatus.Queued, Categories.CompressedKey);
        Insert("failed.zip", DownloadStatus.Failed, Categories.CompressedKey);
        return (store, other.Id);
    }

    private sealed class Bin : Baton.Core.Platform.IRecycleBin
    {
        public List<string> Received { get; } = [];

        public bool Recycle(string path)
        {
            Received.Add(path);
            return true;
        }
    }

    private static string[] Names(DownloadService service) => service.GetSnapshots().Select(s => s.DisplayName).Order().ToArray();

    [Fact]
    public async Task ClearCompleted_RemovesOnlyFinishedAndCheckedDownloads_FilesStay()
    {
        var (store, _) = Seed(DateTime.UtcNow);
        using var _ = store;
        await using var service = new DownloadService(store, autoTick: false);
        await service.StopQueueAsync();
        var bin = new Bin();
        service.RecycleBin = bin;
        var announced = new List<long>();
        service.CompletedCleared += ids => announced.AddRange(ids); // the app takes their notifications back

        Assert.Equal(3, service.CompletedToClear().Count);
        var (removed, kept) = await service.ClearCompletedAsync();
        Assert.Equal(3, removed);
        Assert.Empty(kept);
        Assert.Equal(["failed.zip", "paused.zip", "queued.zip", "scanning.zip"], Names(service));
        Assert.Empty(bin.Received);
        Assert.All(new[] { "a.zip", "b.pdf", "c.zip" }, n => Assert.True(File.Exists(Path.Combine(Desktop, n))));
        Assert.Equal(4, store.LoadAll().Count); // gone from the database too
        Assert.Equal(3, announced.Count);
    }

    [Fact]
    public async Task ClearCompleted_ByCategoryOrQueue_AndToTheRecycleBin()
    {
        var (store, otherQueue) = Seed(DateTime.UtcNow);
        using var _ = store;
        await using var service = new DownloadService(store, autoTick: false);
        await service.StopQueueAsync();
        var bin = new Bin();
        service.RecycleBin = bin;
        var documents = Of(service, Categories.DocumentsKey).Id;

        Assert.Equal(["c.zip"], service.CompletedToClear(queueId: otherQueue).Select(s => s.DisplayName));
        Assert.Equal(1, (await service.ClearCompletedAsync(queueId: otherQueue, recycleFiles: true)).Removed);
        Assert.Equal([Path.Combine(Desktop, "c.zip")], bin.Received);

        Assert.Equal(1, (await service.ClearCompletedAsync(categoryId: documents)).Removed);
        Assert.Equal([Path.Combine(Desktop, "c.zip")], bin.Received); // without the option nothing goes to the bin
        Assert.Contains("a.zip", Names(service));
        Assert.DoesNotContain("b.pdf", Names(service));
    }

    [Fact]
    public async Task ClearCompleted_RemovesOnlyTheDownloadsTheConfirmationCounted()
    {
        var (store, _) = Seed(DateTime.UtcNow);
        using var _ = store;
        await using var service = new DownloadService(store, autoTick: false);
        await service.StopQueueAsync();
        var shown = service.CompletedToClear().Where(s => s.DisplayName != "b.pdf").Select(s => s.Id).ToList(); // b.pdf "completed later"

        Assert.Equal(2, (await service.ClearCompletedAsync(only: shown)).Removed);
        Assert.Contains("b.pdf", Names(service));
    }

    [Fact]
    public void ExportedSettings_CarryTheNewOptions_OlderFilesKeepTheCurrentOnes()
    {
        var mine = new AppSettings { DownloadFolder = Desktop, CategorySubfolders = false, ClearCompletedAfterDays = 30 };
        var exported = Baton.Core.Portable.PortableData.ExportSettings(mine, [], [], [], DateTime.UtcNow).Settings!;
        var merged = Baton.Core.Portable.PortableImport.MergeSettings(new AppSettings(), exported);
        Assert.False(merged.CategorySubfolders);
        Assert.Equal(30, merged.ClearCompletedAfterDays);

        exported.CategorySubfolders = null; // a file written by 1.3.3
        exported.ClearCompletedAfterDays = null;
        var older = Baton.Core.Portable.PortableImport.MergeSettings(mine, exported);
        Assert.False(older.CategorySubfolders);
        Assert.Equal(30, older.ClearCompletedAfterDays);
    }

    [Fact]
    public async Task AutomaticCleanUp_OffByDefault_RemovesOnlyOlderCompletedDownloads_OnTheInjectedClock()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var (store, _) = Seed(now.AddDays(-10));
        using var _ = store;
        var recent = new DownloadRecord
        {
            Url = "https://example.test/recent.zip", Directory = Desktop, FileName = "recent.zip", Status = DownloadStatus.Completed,
            AddedUtc = now.AddDays(-2), CompletedUtc = now.AddDays(-2),
        };
        store.Insert(recent);
        var clock = new FakeClock(now);
        await using var service = new DownloadService(store, clock: clock, autoTick: false);
        await service.StopQueueAsync();
        var announced = new List<long>();
        service.CompletedCleared += ids => announced.AddRange(ids);
        service.Tick();
        Assert.Equal(8, service.GetSnapshots().Count); // off: nothing leaves the list

        var settings = service.Settings;
        settings.ClearCompletedAfterDays = 7;
        service.UpdateSettings(settings);
        service.Tick();
        Assert.Equal(["failed.zip", "paused.zip", "queued.zip", "recent.zip", "scanning.zip"], Names(service));
        Assert.True(File.Exists(Path.Combine(Desktop, "a.zip"))); // files stay
        Assert.Equal(3, announced.Count);

        // Six days later "recent" is eight days old: the next hourly check removes it.
        clock.UtcNow = now.AddDays(6);
        service.Tick();
        Assert.DoesNotContain("recent.zip", Names(service));
    }
}
