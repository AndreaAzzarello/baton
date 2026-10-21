using System.Diagnostics;
using System.Net;
using Baton.Core;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Baton.Tests;

/// <summary>Runs alone, after the other tests: memory measured while other tests run would be their noise.</summary>
[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
public sealed class MemoryCollection;

/// <summary>Downloads that finish and are removed must leave nothing behind (jobs, buffers, snapshots, handlers).</summary>
[Collection(nameof(MemoryTests))]
public sealed class MemoryTests(ITestOutputHelper output) : IAsyncLifetime
{
    private GrabberTestSite _site = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private DownloadStore _store = null!;
    private DownloadService _downloads = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _site = await GrabberTestSite.StartAsync();
        Directory.CreateDirectory(_dir);
        _store = new DownloadStore(Path.Combine(_dir, "state.db"));
        _store.SaveSettings(new AppSettings { DownloadFolder = Path.Combine(_dir, "downloads"), ProxyMode = ProxyMode.None });
        _http = _site.CreateClient(new CookieContainer());
        _downloads = new DownloadService(_store, _http, new EngineOptions { MinSegmentSize = 64 * 1024, CheckpointInterval = TimeSpan.FromMilliseconds(100) },
            autoTick: false);
        await _downloads.StopQueueAsync();
    }

    public async Task DisposeAsync()
    {
        await _downloads.DisposeAsync();
        _store.Dispose();
        _http.Dispose();
        await _site.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task HundredDownloads_CompletedAndRemoved_LeaveNoMemoryBehind()
    {
        for (var i = 0; i < 100; i++)
            _site.Set("files.test", $"/m{i}.bin", SiteResource.File(200_000, seed: i));

        // Warm-up: code paths, connection and buffer pools, SQLite caches grow during the first rounds, then stay put
        // (measured: +6, +3, +2 MB, then +0.03 MB per round of 100).
        for (var i = 0; i < 3; i++)
            await CycleAsync();
        // A leak grows with every round; work left behind by other tests only now and then. So of three measured rounds,
        // the smallest growth must be close to zero: a leak of even 5 KB per download would add 500 KB to each one.
        var growth = new List<long>();
        var last = Measure();
        for (var i = 0; i < 3; i++)
        {
            await CycleAsync();
            var now = Measure();
            growth.Add(now - last);
            last = now;
        }
        output.WriteLine($"managed memory {last / 1024} KB; growth per round of 100: {string.Join(", ", growth.Select(g => $"{g / 1024} KB"))}");

        Assert.Empty(_downloads.GetSnapshots());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "downloads"), "*", SearchOption.AllDirectories));
        Assert.True(growth.Min() < 512 * 1024, $"memory grows with every round of 100 downloads completed and removed: {string.Join(", ", growth)} bytes");
    }

    private async Task CycleAsync()
    {
        var ids = Enumerable.Range(0, 100).Select(i => _downloads.Add($"http://files.test/m{i}.bin")).ToList();
        var watch = Stopwatch.StartNew();
        while (ids.Any(id => _downloads.GetSnapshot(id) is not { Status: DownloadStatus.Completed }))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "the downloads did not finish");
            Assert.DoesNotContain(ids, id => _downloads.GetSnapshot(id) is { Status: DownloadStatus.Failed });
            await Task.Delay(50);
        }
        foreach (var id in ids)
            await _downloads.RemoveAsync(id, deleteFile: true);
    }

    private static long Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
