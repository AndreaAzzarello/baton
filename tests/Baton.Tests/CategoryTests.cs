using Baton.Core;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Categories: matching rules, folders, editing and automatic assignment during a download.</summary>
public sealed class CategoryTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "state.db");

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
    };

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Category Named(IReadOnlyList<Category> all, string key) => all.Single(c => c.Key == key);

    [Theory]
    [InlineData("setup.EXE", null, Categories.ProgramsKey)]
    [InlineData("archive.tar.gz", null, Categories.CompressedKey)]
    [InlineData("report.pdf", "application/octet-stream", Categories.DocumentsKey)]
    [InlineData("song.flac", null, Categories.MusicKey)]
    [InlineData("download.php", "application/zip", Categories.CompressedKey)]
    [InlineData("stream", "video/x-unknown-format; codecs=x", Categories.VideoKey)]
    [InlineData("track", "audio/x-something", Categories.MusicKey)]
    [InlineData("data.bin", "application/octet-stream", Categories.GeneralKey)]
    [InlineData(null, null, Categories.GeneralKey)]
    public void Match_UsesExtensionFirstThenMediaType(string? fileName, string? contentType, string expected)
    {
        var categories = Categories.Defaults();
        Assert.Equal(expected, Categories.Match(categories, fileName, contentType).Key);
    }

    [Fact]
    public void Match_ByUrl_IgnoresQueryString()
    {
        var categories = Categories.Defaults();
        Assert.Equal(Categories.VideoKey, Categories.MatchUrl(categories, "https://cdn.example.com/a/b/clip.mkv?token=abc").Key);
        Assert.Equal(Categories.GeneralKey, Categories.MatchUrl(categories, "https://example.com/").Key);
    }

    [Fact]
    public void ParseExtensions_AcceptsCommonUserInput()
    {
        Assert.Equal(["jpg", "png", "gif", "tar"], Categories.ParseExtensions(" .JPG, png;*.gif  png\ttar  ../bad "));
        Assert.Empty(Categories.ParseExtensions(null));
    }

    [Fact]
    public void ResolveFolder_RelativeFollowsDefaultFolder_AbsoluteStays()
    {
        var relative = new Category { Folder = "Video" };
        var absolute = new Category { Folder = @"D:\Film" };
        var general = new Category { Folder = "" };

        Assert.Equal(@"C:\dl\Video", relative.ResolveFolder(@"C:\dl"));
        Assert.Equal(@"E:\x\Video", relative.ResolveFolder(@"E:\x"));
        Assert.Equal(@"D:\Film", absolute.ResolveFolder(@"C:\dl"));
        Assert.Equal(@"C:\dl", general.ResolveFolder(@"C:\dl"));
    }

    [Fact]
    public async Task Service_AddRenameDelete_PersistsAndMovesDownloadsToGeneral()
    {
        using (var store = new DownloadStore(DbPath))
        {
            store.SaveSettings(new AppSettings { DownloadFolder = _dir, QueueRunning = false });
            await using var service = new DownloadService(store, engineTemplate: Fast);

            var images = service.SaveCategory(new Category { Name = " Immagini ", Extensions = ["jpg", "png"], Folder = "Immagini" });
            Assert.Equal("Immagini", images.Name);
            Assert.Null(images.Key);

            var id = service.Add("https://example.test/photo.jpg", startNow: false);
            Assert.Equal(images.Id, service.GetSnapshot(id)!.CategoryId);
            Assert.Equal(Path.Combine(_dir, "Immagini"), service.GetSnapshot(id)!.Directory);

            images.Name = "Foto";
            service.SaveCategory(images);
            Assert.Contains(service.Categories, c => c.Name == "Foto");

            service.DeleteCategory(images.Id);
            Assert.DoesNotContain(service.Categories, c => c.Id == images.Id);
            Assert.Equal(Named(service.Categories, Categories.GeneralKey).Id, service.GetSnapshot(id)!.CategoryId);

            // The general category is the fallback and cannot be removed.
            service.DeleteCategory(Named(service.Categories, Categories.GeneralKey).Id);
            Assert.Contains(service.Categories, c => c.IsGeneral);
        }

        using var reopened = new DownloadStore(DbPath);
        var categories = reopened.LoadCategories();
        Assert.Equal(6, categories.Count);
        Assert.DoesNotContain(categories, c => c.Name == "Foto");
        Assert.Equal(Named(categories, Categories.GeneralKey).Id, Assert.Single(reopened.LoadAll()).CategoryId);
    }

    [Fact]
    public async Task Download_WithoutVisibleExtension_IsFiledByTheServersAnswer()
    {
        // "get.php?id=7" says nothing; the server's file name and type decide the category and the folder.
        var file = new ServedFile
        {
            Content = TestFileServer.RandomBytes(300 * 1024, 80),
            ContentType = "application/zip",
            ContentDisposition = "attachment; filename=\"pack.zip\"",
        };
        var url = _server.Add("get.php", file) + "?id=7";

        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 2 });
        await using var service = new DownloadService(store, engineTemplate: Fast);

        var id = service.Add(url);
        Assert.Equal(Named(service.Categories, Categories.GeneralKey).Id, service.GetSnapshot(id)!.CategoryId); // guessed from the URL
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        var done = service.GetSnapshot(id)!;
        Assert.Equal(Named(service.Categories, Categories.CompressedKey).Id, done.CategoryId);
        Assert.Equal(Path.Combine(_dir, "Compressi", "pack.zip"), done.FinalPath);
        Assert.Equal("application/zip", done.ContentType);
        Assert.True(File.Exists(done.FinalPath));
    }

    [Fact]
    public async Task Download_WithFolderChosenByUser_KeepsThatFolder()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(100 * 1024, 81), ContentType = "video/mp4" };
        var url = _server.Add("clip", file);
        var chosen = Path.Combine(_dir, "scelta");

        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir });
        await using var service = new DownloadService(store, engineTemplate: Fast);

        var id = service.Add(url, directory: chosen);
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        var done = service.GetSnapshot(id)!;
        Assert.Equal(chosen, done.Directory); // the user's folder wins
        Assert.Equal(Named(service.Categories, Categories.VideoKey).Id, done.CategoryId); // but the category is still detected
    }

    [Fact]
    public void Filter_CombinesStatusCategoryAndSearchWords()
    {
        var filter = new DownloadFilter(StatusFilter.Unfinished, 3, "linux ISO");
        Assert.True(filter.Matches(DownloadStatus.Paused, 3, "ubuntu-linux.iso", "https://example.com/x"));
        Assert.False(filter.Matches(DownloadStatus.Completed, 3, "ubuntu-linux.iso", "https://example.com/x"));
        Assert.False(filter.Matches(DownloadStatus.Paused, 4, "ubuntu-linux.iso", "https://example.com/x"));
        Assert.True(filter.Matches(DownloadStatus.Failed, 3, "image.iso", "https://linux.example.com/image.iso"));
        Assert.False(filter.Matches(DownloadStatus.Queued, 3, "image.iso", "https://example.com/image.iso"));

        var finished = new DownloadFilter(StatusFilter.Finished);
        Assert.True(finished.Matches(DownloadStatus.Completed, null, "a", "b"));
        Assert.False(finished.Matches(DownloadStatus.Downloading, null, "a", "b"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(50);
        }
    }
}
