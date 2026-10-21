using System.Security.Cryptography;
using Baton.Core;

namespace Baton.Tests;

public sealed class ServiceTests : IAsyncLifetime
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public void Store_RoundTripsRecordsSegmentsAndSettings()
    {
        using (var store = new DownloadStore(DbPath))
        {
            var r = new DownloadRecord
            {
                Url = "https://example.com/a.zip", Directory = @"C:\x", FileName = "a.zip", TotalSize = 300,
                ETag = "\"e\"", SupportsRanges = true, Status = DownloadStatus.Paused, AddedUtc = DateTime.UtcNow,
                Segments = [new() { Start = 0, End = 149, Written = 150 }, new() { Start = 150, End = 299, Written = 20 }],
            };
            store.Insert(r);
            store.SaveSettings(new AppSettings { MaxConnections = 5, SpeedLimitKBps = 123, DownloadFolder = @"D:\dl", QueueRunning = false });
        }

        using var reopened = new DownloadStore(DbPath);
        var loaded = Assert.Single(reopened.LoadAll());
        Assert.Equal("a.zip", loaded.FileName);
        Assert.Equal(170, loaded.DownloadedBytes);
        Assert.Equal(2, loaded.Segments.Count);
        var settings = reopened.LoadSettings();
        Assert.Equal(5, settings.MaxConnections);
        Assert.Equal(123, settings.SpeedLimitKBps);
        Assert.Equal(@"D:\dl", settings.DownloadFolder);
        Assert.False(settings.QueueRunning);
    }

    [Fact]
    public async Task Queue_RespectsConcurrencyLimitAndCompletesInOrder()
    {
        var files = Enumerable.Range(0, 4).Select(i => new ServedFile
        {
            Content = TestFileServer.RandomBytes(400 * 1024, 10 + i),
            Throttle = _ => 400 * 1024,
        }).ToList();
        var urls = files.Select((f, i) => _server.Add($"q{i}.bin", f)).ToList();

        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { MaxConcurrentDownloads = 2, MaxConnections = 2, DownloadFolder = _dir });
        await using var service = new DownloadService(store, engineTemplate: Fast);

        foreach (var url in urls)
            service.Add(url, startNow: false);

        var maxActive = 0;
        await WaitUntil(() =>
        {
            var snaps = service.GetSnapshots();
            maxActive = Math.Max(maxActive, snaps.Count(s => s.Status == DownloadStatus.Downloading));
            return snaps.All(s => s.Status == DownloadStatus.Completed);
        }, TimeSpan.FromSeconds(30));

        Assert.Equal(2, maxActive);
        for (var i = 0; i < files.Count; i++)
            Assert.Equal(files[i].Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_dir, $"q{i}.bin")))));
    }

    [Fact]
    public async Task StoppedQueue_DoesNotStartDownloads_AndStateSurvivesRestart()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 20), Throttle = _ => 256 * 1024 };
        var url = _server.Add("persist.bin", file);

        using (var store = new DownloadStore(DbPath))
        {
            store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 2 });
            await using var service = new DownloadService(store, engineTemplate: Fast);
            await service.StopQueueAsync();
            service.Add(url, startNow: false);
            await Task.Delay(300);
            Assert.Equal(DownloadStatus.Queued, Assert.Single(service.GetSnapshots()).Status);

            var id = service.GetSnapshots()[0].Id;
            service.Start(id); // manual start ignores the stopped queue
            await WaitUntil(() => service.GetSnapshots()[0].Downloaded > 200 * 1024, TimeSpan.FromSeconds(10));
            await service.PauseAsync(id);
            Assert.Equal(DownloadStatus.Paused, service.GetSnapshots()[0].Status);
        }

        file.Throttle = _ => 0;
        using (var store = new DownloadStore(DbPath))
        {
            await using var service = new DownloadService(store, engineTemplate: Fast);
            var snap = Assert.Single(service.GetSnapshots());
            Assert.Equal(DownloadStatus.Paused, snap.Status);
            Assert.True(snap.Downloaded > 0);
            Assert.False(service.QueueRunning);

            service.Start(snap.Id);
            await WaitUntil(() => service.GetSnapshots()[0].Status == DownloadStatus.Completed, TimeSpan.FromSeconds(15));
        }

        Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_dir, "persist.bin")))));
    }

    [Fact]
    public async Task Remove_DeletesPartialFile()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 30), Throttle = _ => 128 * 1024 };
        var url = _server.Add("remove.bin", file);
        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir });
        await using var service = new DownloadService(store, engineTemplate: Fast);

        var id = service.Add(url);
        await WaitUntil(() => Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.part").Length == 1, TimeSpan.FromSeconds(10));
        await service.RemoveAsync(id, deleteFile: true);

        Assert.Empty(service.GetSnapshots());
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    private sealed class FakeRecycleBin(bool works) : Baton.Core.Platform.IRecycleBin
    {
        public List<string> Received { get; } = [];

        public bool Recycle(string path)
        {
            Received.Add(path);
            return works;
        }
    }

    [Fact]
    public async Task DeleteFromDisk_SendsFinishedFilesToTheRecycleBin_NeverDeletesThem()
    {
        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir });
        await using var service = new DownloadService(store, engineTemplate: Fast);
        var ids = new[] { "a.bin", "b.bin" }.Select(n => service.Add(_server.Add(n, new ServedFile { Content = TestFileServer.RandomBytes(64 * 1024, n.Length) }))).ToList();
        await WaitUntil(() => service.GetSnapshots().All(s => s.Status == DownloadStatus.Completed), TimeSpan.FromSeconds(15));
        var files = ids.Select(id => service.GetSnapshot(id)!.FinalPath!).ToList();

        var bin = new FakeRecycleBin(works: true);
        service.RecycleBin = bin;
        Assert.True(await service.RemoveAsync(ids[0], deleteFile: true));
        Assert.Equal([files[0]], bin.Received);
        Assert.True(File.Exists(files[0])); // the service itself never deletes a finished file: the Recycle Bin moves it

        service.RecycleBin = new FakeRecycleBin(works: false); // no Recycle Bin on that disk
        Assert.False(await service.RemoveAsync(ids[1], deleteFile: true));
        Assert.True(File.Exists(files[1]));
        Assert.Empty(service.GetSnapshots()); // removed from the list in both cases
    }

    [Fact]
    public async Task Add_RejectsUnsupportedUrls()
    {
        using var store = new DownloadStore(DbPath);
        await using var service = new DownloadService(store);

        Assert.Throws<ArgumentException>(() => service.Add("gopher://example.com/file"));
        Assert.Throws<ArgumentException>(() => service.Add("file:///C:/Windows/notepad.exe"));
        Assert.Throws<ArgumentException>(() => service.Add("not a url"));
    }
}
