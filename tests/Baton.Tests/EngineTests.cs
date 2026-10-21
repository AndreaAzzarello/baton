using System.Diagnostics;
using System.Security.Cryptography;
using Baton.Core;

namespace Baton.Tests;

public sealed class EngineTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly HttpClient _http = DownloadService.CreateHttpClient();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private long _nextId = 1;

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static EngineOptions Fast(int connections = 4) => new()
    {
        MaxConnections = connections,
        MinSegmentSize = 64 * 1024,
        BufferSize = 16 * 1024,
        ReadTimeout = TimeSpan.FromSeconds(5),
        MaxRetries = 5,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
    };

    private DownloadRecord NewRecord(string url) => new() { Id = _nextId++, Url = url, Directory = _dir };

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Phase 1: probe and single connection

    [Fact]
    public async Task Probe_DetectsSizeRangesValidatorAndFileName()
    {
        var url = _server.Add("x.bin", new ServedFile
        {
            Content = TestFileServer.RandomBytes(1000),
            ContentDisposition = "attachment; filename=\"report 2026.pdf\"",
        });

        var probe = await HttpProbe.ProbeAsync(_http, url, CancellationToken.None);

        Assert.Equal(1000, probe.TotalSize);
        Assert.True(probe.SupportsRanges);
        Assert.Equal("\"v1\"", probe.ETag);
        Assert.Equal("report 2026.pdf", probe.FileName);
    }

    [Fact]
    public async Task Download_ServerWithoutRanges_UsesOneConnection()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(700_000), SupportsRanges = false };
        var url = _server.Add("noranges.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(8));

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "noranges.bin")));
        Assert.False(job.Record.SupportsRanges);
        Assert.Equal(2, _server.Requests.Count); // probe + one full GET
    }

    [Fact]
    public async Task Download_UnknownLength_StreamsUntilEnd()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(300_000), SupportsRanges = false, SendContentLength = false };
        var url = _server.Add("chunked.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast());

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "chunked.bin")));
        Assert.Equal(300_000, job.Record.TotalSize);
    }

    [Fact]
    public async Task Download_EmptyFile_Completes()
    {
        var url = _server.Add("empty.bin", new ServedFile { Content = [] });
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast());

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, new FileInfo(Path.Combine(_dir, "empty.bin")).Length);
    }

    // Phase 2: segmentation

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task Download_Segmented_ProducesIdenticalFile(int connections)
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(3 * 1024 * 1024 + 17, seed: connections) };
        var url = _server.Add($"seg{connections}.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(connections));

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, $"seg{connections}.bin")));
        Assert.Equal(DownloadStatus.Completed, job.Record.Status);
        var ranged = _server.Requests.Count(r => r.Path == $"seg{connections}.bin" && r.To != 0);
        Assert.True(ranged >= connections, $"expected at least {connections} range requests, got {ranged}");
        Assert.False(File.Exists(job.Record.PartPath));
    }

    [Fact]
    public async Task Download_ExistingFileName_GetsNumberedCopy()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "dup.bin"), "old");
        var url = _server.Add("dup.bin", new ServedFile { Content = TestFileServer.RandomBytes(1000) });
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast());

        await job.RunAsync(CancellationToken.None);

        Assert.Equal("dup (1).bin", job.Record.FileName);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_dir, "dup.bin")));
    }

    // Phase 3: pause / resume

    [Fact]
    public async Task PauseAndResume_ContinuesFromCheckpointWithoutRedownloading()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(2 * 1024 * 1024), Throttle = _ => 300 * 1024 };
        var url = _server.Add("resume.bin", file);
        DownloadRecord? saved = null;
        var first = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(4), r => { saved = r; return Task.CompletedTask; });

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.2)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(cts.Token));

        Assert.NotNull(saved);
        var done = saved!.Segments.Sum(s => s.Written);
        Assert.InRange(done, 1, file.Content.Length - 1);

        // Every byte the checkpoint claims must really be on disk.
        var part = await File.ReadAllBytesAsync(saved.PartPath!);
        foreach (var s in saved.Segments.Where(s => s.Written > 0))
            Assert.True(part.AsSpan((int)s.Start, (int)s.Written).SequenceEqual(file.Content.AsSpan((int)s.Start, (int)s.Written)));

        file.Throttle = _ => 0;
        var before = _server.Requests.Count;
        var second = new DownloadJob(saved.Clone(), _http, new RateLimiter(), Fast(4));
        await second.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "resume.bin")));
        var resumed = _server.Requests.Skip(before).Where(r => r.To != 0).ToList();
        Assert.All(resumed, r => Assert.True(r.HasIfRange));
        Assert.DoesNotContain(resumed, r => r.From == 0);
    }

    [Fact]
    public async Task Resume_WhenServerFileChanged_RestartsFromScratch()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 1), Throttle = _ => 200 * 1024 };
        var url = _server.Add("changed.bin", file);
        DownloadRecord? saved = null;
        var first = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(2), r => { saved = r; return Task.CompletedTask; });
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(0.8)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(cts.Token));

        file.Content = TestFileServer.RandomBytes(1024 * 1024, 2);
        file.ETag = "\"v2\"";
        file.Throttle = _ => 0;
        var second = new DownloadJob(saved!.Clone(), _http, new RateLimiter(), Fast(2));
        await second.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "changed.bin")));
        Assert.Equal("\"v2\"", second.Record.ETag);
    }

    // Phase 4: retries and dynamic segmentation

    [Fact]
    public async Task DroppedConnections_AreRetried()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 3), FailRequests = 4, FailAfterBytes = 100_000 };
        var url = _server.Add("flaky.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(2));

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "flaky.bin")));
    }

    [Fact]
    public async Task TooManyRequests_OnProbeAndRanges_IsRetried()
    {
        // Seen on a real server: after a pause, the reconnect burst was refused with 429.
        var file = new ServedFile { Content = TestFileServer.RandomBytes(512 * 1024, 6), RejectRequests = 3 };
        var url = _server.Add("busy.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(4));

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "busy.bin")));
    }

    [Fact]
    public async Task ServerCappingConnections_DownloadFinishesWithFewerConnections()
    {
        var file = new ServedFile
        {
            Content = TestFileServer.RandomBytes(2 * 1024 * 1024, 7),
            MaxConcurrent = 2,
            Throttle = _ => 1024 * 1024,
        };
        var url = _server.Add("capped.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(8));

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "capped.bin")));
    }

    [Fact]
    public async Task SlowConnection_HasItsTailTakenOver()
    {
        // The connection that starts at byte 0 crawls at 32 KB/s; alone it would need 32 s.
        var file = new ServedFile
        {
            Content = TestFileServer.RandomBytes(2 * 1024 * 1024, 4),
            Throttle = from => from == 0 ? 32 * 1024 : 0,
        };
        var url = _server.Add("steal.bin", file);
        var job = new DownloadJob(NewRecord(url), _http, new RateLimiter(), Fast(2));

        var sw = Stopwatch.StartNew();
        await job.RunAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "steal.bin")));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        var stolen = _server.Requests.Where(r => r.Path == "steal.bin" && r.From is > 0 and < 1024 * 1024).ToList();
        Assert.NotEmpty(stolen); // a request started inside the first half
    }

    [Fact]
    public async Task NotFound_FailsWithoutRetrying()
    {
        var job = new DownloadJob(NewRecord($"{_server.BaseUrl}/files/missing.bin"), _http, new RateLimiter(), Fast());

        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() => job.RunAsync(CancellationToken.None));
        Assert.Contains("404", ex.Message);
    }

    // Phase 7: speed limit

    [Fact]
    public async Task RateLimiter_CapsThroughput()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 5) };
        var url = _server.Add("limited.bin", file);
        var limiter = new RateLimiter { BytesPerSecond = 512 * 1024 };
        var job = new DownloadJob(NewRecord(url), _http, limiter, Fast(4));

        var sw = Stopwatch.StartNew();
        await job.RunAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(file.Sha256, HashOf(Path.Combine(_dir, "limited.bin")));
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.6, 3.5); // ~2 s at 512 KB/s
    }
}
