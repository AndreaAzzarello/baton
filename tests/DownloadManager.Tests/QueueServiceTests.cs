using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>Named queues in the service: limits, moving and ordering, schedules, retries, quotas and the end of a run.</summary>
public sealed class QueueServiceTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "state.db");

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(20),
        MaxRetries = 1,
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

    private DownloadStore NewStore(int maxConnections = 2)
    {
        var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = maxConnections });
        return store;
    }

    private string SlowFile(string name, int kb = 1024, int bytesPerSecond = 128 * 1024) =>
        _server.Add(name, new ServedFile { Content = TestFileServer.RandomBytes(kb * 1024, name.Length), Throttle = _ => bytesPerSecond });

    [Fact]
    public async Task Queues_CreateRenameDelete_MainQueueStays_AndDownloadsMoveToIt()
    {
        using (var store = NewStore())
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
            var main = service.Queues.Single();
            Assert.True(main.IsMain);

            var night = service.SaveQueue(new DownloadQueue { Name = " Notte ", MaxConcurrent = 2 });
            Assert.Equal("Notte", night.Name);
            Assert.False(night.Running);
            var id = service.Add("https://example.test/a.iso", startNow: false, queueId: night.Id);
            Assert.Equal(night.Id, service.GetSnapshot(id)!.QueueId);

            night.Name = "Notturna";
            service.SaveQueue(night);
            Assert.Contains(service.Queues, q => q.Name == "Notturna");

            service.DeleteQueue(main.Id); // refused
            Assert.Equal(2, service.Queues.Count);
            service.DeleteQueue(night.Id);
            Assert.Equal(main.Id, service.GetSnapshot(id)!.QueueId);
        }

        using var reopened = new DownloadStore(DbPath);
        Assert.Single(reopened.LoadQueues());
        Assert.Equal(reopened.LoadQueues()[0].Id, Assert.Single(reopened.LoadAll()).QueueId);
    }

    [Fact]
    public async Task MoveToQueue_AndReorder_StayWithinTheQueue()
    {
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        await service.StopQueueAsync();
        var other = service.SaveQueue(new DownloadQueue { Name = "Altra" });

        var a = service.Add("https://example.test/a", startNow: false);
        var b = service.Add("https://example.test/b", startNow: false);
        var c = service.Add("https://example.test/c", startNow: false);
        var d = service.Add("https://example.test/d", startNow: false);

        service.MoveToQueue([b, d], other.Id);
        IEnumerable<long> Order(long queue) => service.GetSnapshots().Where(s => s.QueueId == queue).OrderBy(s => s.SortOrder).Select(s => s.Id);
        Assert.Equal([a, c], Order(service.MainQueueId));
        Assert.Equal([b, d], Order(other.Id));

        // "Down" on a skips over b and d, which belong to the other queue.
        service.Move(a, 1);
        Assert.Equal([c, a], Order(service.MainQueueId));
        Assert.Equal([b, d], Order(other.Id));
        service.Move(a, 1); // already last in its queue
        Assert.Equal([c, a], Order(service.MainQueueId));
        service.Move(d, -1);
        Assert.Equal([d, b], Order(other.Id));
    }

    [Fact]
    public async Task EachQueue_HasItsOwnLimit_UnderTheGlobalOne()
    {
        using var store = NewStore(maxConnections: 1);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var main = service.Queues.Single();
        main.MaxConcurrent = 1;
        service.SaveQueue(main);
        var second = service.SaveQueue(new DownloadQueue { Name = "Seconda", MaxConcurrent = 2 });
        service.StartQueue(second.Id);

        for (var i = 0; i < 3; i++)
        {
            service.Add(SlowFile($"m{i}.bin"), startNow: false);
            service.Add(SlowFile($"s{i}.bin"), startNow: false, queueId: second.Id);
        }
        await Task.Delay(500);
        var running = service.GetSnapshots().Where(s => s.Status == DownloadStatus.Downloading).ToList();
        Assert.Equal(1, running.Count(s => s.QueueId == service.MainQueueId));
        Assert.Equal(2, running.Count(s => s.QueueId == second.Id));

        var settings = service.Settings;
        settings.MaxConcurrentDownloads = 2; // the global limit wins over the queues' own
        service.UpdateSettings(settings);
        await service.StopQueueAsync(second.Id);
        await service.StopQueueAsync(service.MainQueueId);
        service.StartQueue(service.MainQueueId);
        service.StartQueue(second.Id);
        await Task.Delay(300);
        Assert.Equal(2, service.GetSnapshots().Count(s => s.Status == DownloadStatus.Downloading));
    }

    [Fact]
    public async Task ScheduledQueue_StartsInItsWindow_AndStopsAtTheEnd()
    {
        var clock = new FakeClock(FakeClock.Local(2026, 10, 14, 21, 58));
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);
        var night = service.SaveQueue(new DownloadQueue
        {
            Name = "Notte",
            Schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(22, 0), Stop = new TimeOnly(23, 0) },
        });
        var id = service.Add(SlowFile("night.bin", bytesPerSecond: 64 * 1024), startNow: false, queueId: night.Id);

        service.Tick();
        Assert.Equal(DownloadStatus.Queued, service.GetSnapshot(id)!.Status);
        Assert.Equal(QueueState.Scheduled, service.GetQueueStatuses().Single(q => q.QueueId == night.Id).State);

        clock.UtcNow = FakeClock.Local(2026, 10, 14, 22, 0);
        service.Tick();
        Assert.Equal(DownloadStatus.Downloading, service.GetSnapshot(id)!.Status);
        Assert.Equal(QueueState.Running, service.GetQueueStatuses().Single(q => q.QueueId == night.Id).State);

        await WaitUntil(() => service.GetSnapshot(id)!.Downloaded > 0);
        clock.UtcNow = FakeClock.Local(2026, 10, 14, 23, 0);
        service.Tick();
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Queued);
        Assert.True(service.GetSnapshot(id)!.Downloaded > 0); // stopped at the end of the window, data kept
        Assert.False(service.Queues.Single(q => q.Id == night.Id).Running);
    }

    [Fact]
    public async Task AppStartedLate_InsideTheWindow_StartsTheQueue_AfterTheStop_DoesNot()
    {
        var schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(9, 0), Stop = new TimeOnly(10, 0) };
        long queueId;
        using (var store = NewStore())
        {
            await using var setup = new DownloadService(store, engineTemplate: Fast, clock: new FakeClock(FakeClock.Local(2026, 10, 14, 8, 0)), autoTick: false);
            queueId = setup.SaveQueue(new DownloadQueue { Name = "Mattina", Schedule = schedule }).Id;
        }

        using (var store = new DownloadStore(DbPath))
        {
            // Started at 10:05: the window is over.
            await using var late = new DownloadService(store, engineTemplate: Fast, clock: new FakeClock(FakeClock.Local(2026, 10, 14, 10, 5)), autoTick: false);
            Assert.False(late.Queues.Single(q => q.Id == queueId).Running);
        }

        using (var store = new DownloadStore(DbPath))
        {
            // Started at 09:40: the queue starts right away.
            await using var inside = new DownloadService(store, engineTemplate: Fast, clock: new FakeClock(FakeClock.Local(2026, 10, 15, 9, 40)), autoTick: false);
            Assert.True(inside.Queues.Single(q => q.Id == queueId).Running);
        }
    }

    [Fact]
    public async Task RestartInsideTheWindow_DoesNotRetryFailures_SoAShutdownCannotLoop()
    {
        var clock = new FakeClock(FakeClock.Local(2026, 10, 14, 21, 59));
        var url = _server.Add("always-fails.bin", new ServedFile { Content = new byte[10], Authorize = _ => false });
        long queueId;
        long id;
        using (var store = NewStore())
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);
            queueId = service.SaveQueue(new DownloadQueue
            {
                Name = "Notte",
                EndAction = EndAction.Shutdown,
                Schedule = new QueueSchedule { Enabled = true, Start = new TimeOnly(22, 0), Stop = new TimeOnly(23, 0) },
            }).Id;
            id = service.Add(url, startNow: false, queueId: queueId);
            var finished = 0;
            service.QueueFinished += _ => Interlocked.Increment(ref finished);

            clock.UtcNow = FakeClock.Local(2026, 10, 14, 22, 0);
            service.Tick(); // the window starts: the download runs and fails, the run ends with the end action
            await WaitUntil(() => Volatile.Read(ref finished) == 1);
            Assert.Equal(DownloadStatus.Failed, service.GetSnapshot(id)!.Status);
        }

        // The PC restarts at 22:05 and the app starts with Windows, still inside the window.
        clock.UtcNow = FakeClock.Local(2026, 10, 14, 22, 5);
        using (var store = new DownloadStore(DbPath))
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);
            var finished = 0;
            service.QueueFinished += _ => Interlocked.Increment(ref finished);
            service.Tick();
            await Task.Delay(300);
            Assert.Equal(DownloadStatus.Failed, service.GetSnapshot(id)!.Status); // not retried
            Assert.Equal(0, finished); // so no second end action
        }
    }

    [Fact]
    public async Task FinishedRun_StopsTheQueue_AndReportsItOnce()
    {
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var queue = service.SaveQueue(new DownloadQueue { Name = "Con spegnimento", EndAction = EndAction.Shutdown, MaxConcurrent = 2 });
        var finished = new List<DownloadQueue>();
        service.QueueFinished += q => { lock (finished) finished.Add(q); };

        service.Add(SlowFile("f1.bin", kb: 200, bytesPerSecond: 0), startNow: false, queueId: queue.Id);
        service.Add(SlowFile("f2.bin", kb: 200, bytesPerSecond: 0), startNow: false, queueId: queue.Id);
        service.StartQueue(queue.Id);

        await WaitUntil(() => { lock (finished) return finished.Count > 0; });
        service.Tick();
        Assert.Equal(EndAction.Shutdown, Assert.Single(finished).EndAction);
        Assert.All(service.GetSnapshots(), s => Assert.Equal(DownloadStatus.Completed, s.Status));
        Assert.False(service.Queues.Single(q => q.Id == queue.Id).Running);

        // The main queue has no end action: it stays started for downloads added later.
        Assert.True(service.QueueRunning);
    }

    [Fact]
    public async Task CancelledShutdown_LetsTheQueuesCarryOn()
    {
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var running = service.Add(SlowFile("before.bin"), startNow: false);
        await WaitUntil(() => service.GetSnapshot(running)!.Status == DownloadStatus.Downloading);

        // The end action saved everything before shutting down, but the PC did not turn off.
        await service.ShutdownAsync();
        Assert.Equal(DownloadStatus.Queued, service.GetSnapshot(running)!.Status);
        var added = service.Add(SlowFile("after.bin"), startNow: false);
        service.Tick();
        Assert.Equal(DownloadStatus.Queued, service.GetSnapshot(added)!.Status); // nothing starts while shutting down

        service.ResumeAfterShutdown();
        Assert.Equal(DownloadStatus.Downloading, service.GetSnapshot(running)!.Status);
        Assert.Equal(DownloadStatus.Downloading, service.GetSnapshot(added)!.Status);
    }

    [Fact]
    public async Task FailedDownload_IsRetriedByItsQueue_AfterTheDelay()
    {
        var clock = new FakeClock(new DateTime(2026, 10, 14, 10, 0, 0));
        var file = new ServedFile { Content = TestFileServer.RandomBytes(100 * 1024, 5), Authorize = _ => false };
        var url = _server.Add("flaky.bin", file);
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);
        var queue = service.SaveQueue(new DownloadQueue { Name = "Riprova", RetryCount = 2, RetryDelaySeconds = 60 });
        var id = service.Add(url, startNow: false, queueId: queue.Id);
        service.StartQueue(queue.Id);

        await WaitUntil(() => service.GetSnapshot(id)!.RetryAtUtc is not null);
        var waiting = service.GetSnapshot(id)!;
        Assert.Equal(DownloadStatus.Queued, waiting.Status);
        Assert.Equal(clock.UtcNow.AddSeconds(60), waiting.RetryAtUtc);
        Assert.Contains("12:01", waiting.Error); // tells when the next attempt happens, in local time (10:01 UTC is 12:01 in Rome)

        service.Tick();
        Assert.Equal(DownloadStatus.Queued, service.GetSnapshot(id)!.Status); // not before the delay

        file.Authorize = null; // the server works again
        clock.Advance(TimeSpan.FromSeconds(61));
        service.Tick();
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
    }

    [Fact]
    public async Task FailedDownload_GivesUpAfterTheRetries()
    {
        var clock = new FakeClock(new DateTime(2026, 10, 14, 10, 0, 0));
        var url = _server.Add("broken.bin", new ServedFile { Content = new byte[10], Authorize = _ => false });
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);
        var queue = service.SaveQueue(new DownloadQueue { Name = "Due tentativi", RetryCount = 1, RetryDelaySeconds = 10 });
        var id = service.Add(url, startNow: false, queueId: queue.Id);
        service.StartQueue(queue.Id);

        await WaitUntil(() => service.GetSnapshot(id)!.RetryAtUtc is not null);
        clock.Advance(TimeSpan.FromSeconds(11));
        service.Tick();
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Failed);
        Assert.Equal(2, _server.Requests.Count(r => r.Path == "broken.bin"));
    }

    [Fact]
    public async Task QueueQuota_PausesTheQueue_UntilTheHourFreesUp()
    {
        var clock = new FakeClock(new DateTime(2026, 10, 14, 10, 0, 0));
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1536 * 1024, 7), Throttle = _ => 512 * 1024 };
        var url = _server.Add("quota.bin", file);
        using var store = NewStore(maxConnections: 1);
        await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);
        var queue = service.SaveQueue(new DownloadQueue { Name = "Limitata", QuotaMBPerHour = 1 });
        var id = service.Add(url, startNow: false, queueId: queue.Id);
        service.StartQueue(queue.Id);

        // Tick as the app would, until the quota stops the download.
        await WaitUntil(() =>
        {
            service.Tick();
            return service.GetSnapshot(id)!.Status == DownloadStatus.Queued && service.QuotaUsed(queue.Id) > 0;
        });
        var paused = service.GetSnapshot(id)!;
        Assert.InRange(paused.Downloaded, 1024 * 1024, 1400 * 1024); // stopped soon after 1 MB
        Assert.Contains("Quota", paused.Error);
        service.Tick();
        Assert.Equal(DownloadStatus.Queued, service.GetSnapshot(id)!.Status); // still waiting

        // An hour later the quota is free again and the download resumes where it stopped.
        file.Throttle = _ => 0;
        clock.Advance(TimeSpan.FromMinutes(62));
        service.Tick();
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
        Assert.Equal(file.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_dir, "quota.bin")))));
    }

    [Fact]
    public async Task GlobalQuota_AppliesToEveryQueue_ButNotToManualStarts()
    {
        var clock = new FakeClock(new DateTime(2026, 10, 14, 10, 0, 0));
        using var store = NewStore();
        var settings = store.LoadSettings();
        settings.GlobalQuotaMBPerHour = 1;
        store.SaveSettings(settings);
        await using var service = new DownloadService(store, engineTemplate: Fast, clock: clock, autoTick: false);

        var manual = service.Add(_server.Add("manual.bin", new ServedFile { Content = TestFileServer.RandomBytes(1536 * 1024, 8) }));
        await WaitUntil(() => service.GetSnapshot(manual)!.Status == DownloadStatus.Completed);
        service.Tick(); // counts the 1.5 MB

        var queued = service.Add(SlowFile("later.bin", kb: 100), startNow: false);
        service.Tick();
        Assert.Equal(DownloadStatus.Queued, service.GetSnapshot(queued)!.Status);
        Assert.Contains("Quota", service.GetSnapshot(queued)!.Error);
    }

    [Fact]
    public async Task Extension_WithoutQueue_UsesTheMainQueue()
    {
        using var store = NewStore();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var night = service.SaveQueue(new DownloadQueue { Name = "Notte" });

        // A message from a 0.3 extension has no "queue" field.
        var old = JsonSerializer.Deserialize<ChannelMessage>("""{"type":"add","download":{"url":"https://example.test/f.zip"}}""", Messages.Json)!;
        Assert.Null(old.Download!.Queue);
        Assert.Equal(service.MainQueueId, service.ResolveQueue(old.Download.Queue));

        var named = JsonSerializer.Deserialize<ChannelMessage>("""{"type":"add","download":{"url":"https://example.test/f.zip","queue":"notte"}}""", Messages.Json)!;
        Assert.Equal(night.Id, service.ResolveQueue(named.Download!.Queue));
        Assert.Equal(service.MainQueueId, service.ResolveQueue("non esiste"));
    }

    [Fact]
    public void Migration_FromVersion2_PutsEveryDownloadInTheMainQueue()
    {
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            // The schema written by version 0.3.
            cmd.CommandText = """
                CREATE TABLE downloads (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, directory TEXT NOT NULL, file_name TEXT,
                    total_size INTEGER, etag TEXT, last_modified TEXT, supports_ranges INTEGER NOT NULL, status INTEGER NOT NULL, error TEXT,
                    sort_order INTEGER NOT NULL, added_utc TEXT NOT NULL, completed_utc TEXT, request_context TEXT,
                    category_id INTEGER, auto_category INTEGER NOT NULL DEFAULT 0, content_type TEXT);
                CREATE TABLE segments (download_id INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE, idx INTEGER NOT NULL,
                    start INTEGER NOT NULL, end_pos INTEGER NOT NULL, written INTEGER NOT NULL, PRIMARY KEY (download_id, idx));
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE categories (id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT UNIQUE, name TEXT NOT NULL, extensions TEXT NOT NULL,
                    folder TEXT NOT NULL, sort_order INTEGER NOT NULL);
                INSERT INTO categories (key, name, extensions, folder, sort_order) VALUES ('general', 'Generale', '', '', 0), (NULL, 'Mie', 'abc', 'Mie', 1);
                INSERT INTO downloads (url, directory, file_name, total_size, supports_ranges, status, sort_order, added_utc, category_id)
                    VALUES ('https://example.com/a.zip', 'D:\dl', 'a.zip', 2000, 1, 2, 0, '2026-10-01T10:00:00.0000000Z', 2),
                           ('https://example.com/b.iso', 'D:\dl', 'b.iso', 10, 1, 3, 1, '2026-10-01T11:00:00.0000000Z', 1);
                INSERT INTO segments VALUES (1, 0, 0, 999, 700), (1, 1, 1000, 1999, 50);
                INSERT INTO settings VALUES ('MaxConcurrentDownloads', '4'), ('QueueRunning', '0'), ('Theme', 'Dark');
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new DownloadStore(DbPath);
        Assert.Equal(3, store.Version);
        Assert.EndsWith(".v2.bak", store.BackupPath);

        var main = Assert.Single(store.LoadQueues());
        Assert.True(main.IsMain);
        Assert.Equal("Coda principale", main.Name);
        Assert.Equal(4, main.MaxConcurrent); // the old limit
        Assert.False(main.Running); // the old stopped state

        var downloads = store.LoadAll().OrderBy(d => d.Id).ToList();
        Assert.All(downloads, d => Assert.Equal(main.Id, d.QueueId));
        Assert.Equal(750, downloads[0].DownloadedBytes);
        Assert.Equal(2, downloads[0].CategoryId);
        Assert.Equal(DownloadStatus.Completed, downloads[1].Status);
        Assert.Equal(2, store.LoadCategories().Count);

        var settings = store.LoadSettings();
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(10, settings.MaxConcurrentDownloads); // the overall limit across all queues
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(25);
        }
    }
}
