using System.Security.Cryptography;
using Baton.Core;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>"Update address": a new link for the same file keeps the partial data; a different file starts again.</summary>
public sealed class UrlUpdateTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "state.db");

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Starts a throttled download of <paramref name="file"/> and pauses it part way.</summary>
    private async Task<(DownloadStore Store, DownloadService Service, long Id)> PartialDownloadAsync(ServedFile file, string name)
    {
        var url = _server.Add(name, file);
        var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 2 });
        var service = new DownloadService(store, engineTemplate: Fast);
        var id = service.Add(url, fileName: "target.bin");
        await WaitUntil(() => service.GetSnapshot(id)!.Downloaded > 200 * 1024);
        await service.PauseAsync(id);
        return (store, service, id);
    }

    [Fact]
    public async Task SameFileAtNewAddress_KeepsDownloadedData_AndCompletesCorrectly()
    {
        var content = TestFileServer.RandomBytes(1024 * 1024, 90);
        var original = new ServedFile { Content = content, ETag = "\"same\"", Throttle = _ => 512 * 1024 };
        var (store, service, id) = await PartialDownloadAsync(original, "old-link.bin");
        await using (service)
        using (store)
        {
            var kept = service.GetSnapshot(id)!.Downloaded;
            var mirror = _server.Add("new-link.bin", new ServedFile { Content = content, ETag = "\"same\"" });

            var check = await service.CheckNewUrlAsync(id, mirror);
            Assert.Equal(UrlMatch.Same, check.Match);

            await service.ChangeUrlAsync(id, check, keepData: true);
            Assert.Equal(mirror, service.GetSnapshot(id)!.Url);
            Assert.Equal(kept, service.GetSnapshot(id)!.Downloaded);

            service.Start(id);
            await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

            // The new address only served what was missing.
            Assert.DoesNotContain(_server.Requests, r => r.Path == "new-link.bin" && r.From == 0 && r.To != 0);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)),
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_dir, "target.bin")))));
        }
    }

    [Fact]
    public async Task DifferentFile_IsDetected_AndRestartsFromZero()
    {
        var original = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 91), ETag = "\"v1\"", Throttle = _ => 512 * 1024 };
        var (store, service, id) = await PartialDownloadAsync(original, "a.bin");
        await using (service)
        using (store)
        {
            var other = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 92), ETag = "\"v2\"" };
            var otherUrl = _server.Add("b.bin", other);

            var check = await service.CheckNewUrlAsync(id, otherUrl);
            Assert.Equal(UrlMatch.Different, check.Match);

            await service.ChangeUrlAsync(id, check, keepData: false);
            Assert.Equal(0, service.GetSnapshot(id)!.Downloaded);
            Assert.Empty(Directory.GetFiles(_dir, "*.part"));

            service.Start(id);
            await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
            Assert.Equal(other.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_dir, "target.bin")))));
        }
    }

    [Fact]
    public void Compare_CoversSizeValidatorsAndRanges()
    {
        var record = new DownloadRecord
        {
            TotalSize = 100, ETag = "\"x\"", LastModified = "Mon, 01 Jun 2026 10:00:00 GMT", SupportsRanges = true, Status = DownloadStatus.Paused,
            Segments = [new SegmentState { Start = 0, End = 99, Written = 40 }],
        };
        UrlMatch Check(ProbeResult p) => UrlCheck.Compare(record, "https://new.test/f", p).Match;

        Assert.Equal(UrlMatch.Same, Check(new ProbeResult(100, true, "\"x\"", null, "f")));
        Assert.Equal(UrlMatch.Different, Check(new ProbeResult(100, true, "\"y\"", null, "f")));
        Assert.Equal(UrlMatch.Different, Check(new ProbeResult(101, true, "\"x\"", null, "f")));
        Assert.Equal(UrlMatch.Different, Check(new ProbeResult(100, false, "\"x\"", null, "f")));
        // Without an ETag on the new server, the modification date decides.
        Assert.Equal(UrlMatch.Same, Check(new ProbeResult(100, true, null, "Mon, 01 Jun 2026 10:00:00 GMT", "f")));
        Assert.Equal(UrlMatch.Different, Check(new ProbeResult(100, true, null, "Tue, 02 Jun 2026 10:00:00 GMT", "f")));
        // Same size and nothing else to compare: the user decides.
        Assert.Equal(UrlMatch.Unverifiable, Check(new ProbeResult(100, true, null, null, "f")));

        record.Segments[0].Written = 0;
        Assert.Equal(UrlMatch.NothingToKeep, Check(new ProbeResult(5, false, null, null, "f")));
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
