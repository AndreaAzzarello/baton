using System.Buffers;
using System.Net;
using DownloadManager.Core.Net;

namespace DownloadManager.Core;

/// <summary>A running download, as the service sees it: a file (<see cref="DownloadJob"/>) or a stream (<see cref="Media.MediaJob"/>).</summary>
public interface IDownloadJob
{
    DownloadRecord Record { get; }
    long BytesDownloaded { get; }
    int ActiveConnections { get; }
    List<SegmentView>? SegmentsView();
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Downloads one file with several range connections into a single preallocated part file, over HTTP(S) or FTP(S).
/// Progress is checkpointed so a cancelled or crashed job can resume where it stopped.
/// </summary>
public sealed class DownloadJob : IDownloadJob
{
    private readonly HttpClient _sharedHttp;
    private readonly NetworkContext? _network;
    private ITransport _transport = null!;
    private readonly RateLimiter _limiter;
    private readonly EngineOptions _options;
    private readonly Func<DownloadRecord, Task> _onCheckpoint;
    private readonly Action<DownloadRecord, ProbeResult>? _onProbed;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private long _downloaded;
    private int _connections;
    private SegmentScheduler? _scheduler;
    private FileStream? _file;

    public DownloadJob(
        DownloadRecord record,
        HttpClient http,
        RateLimiter limiter,
        EngineOptions options,
        Func<DownloadRecord, Task>? onCheckpoint = null,
        Action<DownloadRecord, ProbeResult>? onProbed = null,
        NetworkContext? network = null)
    {
        Record = record;
        _sharedHttp = http;
        _network = network;
        _limiter = limiter;
        _options = options;
        _onCheckpoint = onCheckpoint ?? (_ => Task.CompletedTask);
        _onProbed = onProbed;
        _downloaded = record.Segments.Sum(s => s.Written);
    }

    /// <summary>The job's private copy of the record; up to date after every checkpoint.</summary>
    public DownloadRecord Record { get; }

    public long BytesDownloaded => Interlocked.Read(ref _downloaded);

    public int ActiveConnections => Volatile.Read(ref _connections);

    /// <summary>Live ranges while the transfer runs; null before the first response.</summary>
    public List<SegmentView>? SegmentsView() => _scheduler?.View();

    public async Task RunAsync(CancellationToken ct)
    {
        Record.Error = null;
        using var transport = CreateTransport();
        _transport = transport;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await RunOnceAsync(ct);
                return;
            }
            catch (ResourceChangedException) when (attempt == 0)
            {
                // The file on the server changed since the partial data was written: start over once.
                DiscardPartialData();
            }
        }
    }

    /// <summary>
    /// Browser cookies and a login given for this download get a client of their own, so they never reach other
    /// downloads or other sites.
    /// </summary>
    private ITransport CreateTransport()
    {
        if (_network is not null)
            return _network.CreateTransport(Record, _sharedHttp);
        if (FtpTransport.IsFtp(new Uri(Record.Url)))
            return NetworkContext.Default.CreateTransport(Record);
        var own = Record.Context is { Cookies.Count: > 0 } context
            ? DownloadService.CreateHttpClient(RequestContexts.ToCookieContainer(context.Cookies))
            : null;
        return new HttpTransport(own ?? _sharedHttp, own is not null, Record.Url, Record.Context, _options.ReadTimeout);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var probe = await WithRetriesAsync(() => _transport.ProbeAsync(ct), ct);
        if (!CanResume(probe))
            DiscardPartialData();

        Record.TotalSize = probe.TotalSize;
        Record.SupportsRanges = probe.SupportsRanges;
        Record.ETag = probe.ETag;
        Record.LastModified = probe.LastModified;
        Record.FileName ??= probe.FileName;
        Record.ContentType = probe.ContentType;
        // Nothing is on disk yet, so the folder may still change (e.g. the category is known only now).
        if (Record.Segments.All(s => s.Written == 0))
            _onProbed?.Invoke(Record, probe);
        if (Record.Segments.Count == 0)
            Record.Segments = SegmentScheduler.Plan(probe.TotalSize, probe.SupportsRanges, _options.MaxConnections, _options.MinSegmentSize);

        Directory.CreateDirectory(Record.Directory);
        _scheduler = new SegmentScheduler(Record.Segments, _options.MinSegmentSize);
        Interlocked.Exchange(ref _downloaded, Record.Segments.Sum(s => s.Written));

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _file = new FileStream(Record.PartPath!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 0, FileOptions.Asynchronous);
        try
        {
            if (Record.SupportsRanges && Record.TotalSize is { } size)
            {
                SparseFile.TryMarkSparse(_file.SafeFileHandle);
                if (_file.Length != size)
                    _file.SetLength(size);
            }

            var connections = Record.SupportsRanges ? Math.Max(1, _options.MaxConnections) : 1;
            var checkpoints = CheckpointLoopAsync(jobCts.Token);
            try
            {
                while (true)
                {
                    var before = BytesDownloaded;
                    var workers = Enumerable.Range(0, connections).Select(_ => WorkerAsync(jobCts)).ToArray();
                    try
                    {
                        await Task.WhenAll(workers);
                    }
                    catch when (workers.Any(w => w.IsFaulted) && !ct.IsCancellationRequested)
                    {
                        var errors = workers.Where(w => w.IsFaulted).SelectMany(w => w.Exception!.InnerExceptions).ToList();
                        throw errors.OfType<ResourceChangedException>().FirstOrDefault() ?? errors[0];
                    }

                    if (_scheduler.AllComplete || ct.IsCancellationRequested)
                        break;
                    if (connections == 1 && BytesDownloaded == before)
                        throw new DownloadFailedException("The server refused every connection.");
                    // Retired connections can leave a range nobody picked up: finish it on one connection.
                    connections = 1;
                }
            }
            finally
            {
                jobCts.Cancel();
                await checkpoints.ContinueWith(_ => { }, TaskScheduler.Default);
                await CheckpointAsync();
            }

            ct.ThrowIfCancellationRequested();
            if (!_scheduler.AllComplete)
                throw new DownloadFailedException("The download stopped before all data was received.");

            Record.TotalSize ??= _file.Length;
            if (_file.Length != Record.TotalSize)
                throw new DownloadFailedException($"Size mismatch: expected {Record.TotalSize} bytes, got {_file.Length}.");
        }
        finally
        {
            await _file.DisposeAsync();
            _file = null;
        }

        var finalPath = FileNames.MakeUnique(Record.FinalPath!);
        File.Move(Record.PartPath!, finalPath);
        ZoneIdentifier.TryMark(finalPath, Record.Url, Record.Context?.Referrer);
        Record.FileName = Path.GetFileName(finalPath);
        Record.Status = DownloadStatus.Completed;
        Record.CompletedUtc = DateTime.UtcNow;
        await _onCheckpoint(Record.Clone());
    }

    private bool CanResume(ProbeResult probe)
    {
        if (Record.Segments.Count == 0 || Record.Segments.All(s => s.Written == 0))
            return true; // nothing to lose
        if (!probe.SupportsRanges || !Record.SupportsRanges || probe.TotalSize != Record.TotalSize)
            return false;
        if (Record.ETag is not null)
            return Record.ETag == probe.ETag;
        if (Record.LastModified is not null)
            return Record.LastModified == probe.LastModified;
        return true;
    }

    private void DiscardPartialData()
    {
        Record.Segments = [];
        if (Record.PartPath is { } part && File.Exists(part))
            File.Delete(part);
    }

    private async Task WorkerAsync(CancellationTokenSource jobCts)
    {
        var ct = jobCts.Token;
        try
        {
            while (_scheduler!.Acquire() is { } segment)
            {
                Interlocked.Increment(ref _connections);
                try
                {
                    await DownloadSegmentAsync(segment, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _connections);
                    _scheduler.Release(segment);
                }
            }
        }
        catch (RetireConnectionException)
        {
            // This connection gives up; its range is released for the remaining ones.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            jobCts.Cancel(); // one fatal error stops the other connections
            throw;
        }
    }

    private async Task DownloadSegmentAsync(SegmentState segment, CancellationToken ct)
    {
        var failures = 0;
        while (!_scheduler!.IsComplete(segment))
        {
            var before = _scheduler.PositionOf(segment);
            try
            {
                await TransferAsync(segment, ct);
            }
            catch (Exception ex) when ((IsTransient(ex) || IsExtraLoginRefused(ex)) && !ct.IsCancellationRequested)
            {
                if (_scheduler.PositionOf(segment) > before)
                    failures = 0;

                // While other connections still work, a refused or failing extra connection is dropped
                // instead of failing the download: servers often cap connections per client.
                var othersActive = Volatile.Read(ref _connections) > 1;
                if (!othersActive && !IsTransient(ex))
                    throw; // the only connection was refused its login: the user is asked for one
                var limit = othersActive && IsServerBusy(ex) ? 2 : _options.MaxRetries;
                if (++failures > limit)
                {
                    if (othersActive)
                        throw new RetireConnectionException();
                    throw new DownloadFailedException($"Giving up after {limit} retries: {ex.Message}", ex);
                }
                await Task.Delay(RetryDelay(failures), ct);
            }
        }
    }

    private bool IsServerBusy(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }
            or ServerBusyException
            || IsExtraLoginRefused(ex);

    /// <summary>
    /// FTP servers answer 530 both for a wrong password and for "too many users". When the same login already works on
    /// another connection, it means the latter: handled like HTTP 429.
    /// </summary>
    private bool IsExtraLoginRefused(Exception ex) =>
        ex is AuthenticationRequiredException { FtpCode: "530" } && Volatile.Read(ref _connections) > 1;

    private sealed class RetireConnectionException : Exception;

    private async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var failures = 1; ; failures++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested && failures <= _options.MaxRetries)
            {
                await Task.Delay(RetryDelay(failures), ct);
            }
        }
    }

    private TimeSpan RetryDelay(int failures) => _options.RetryBaseDelay * Math.Pow(2, Math.Min(failures - 1, 5));

    private static bool IsTransient(Exception ex) =>
        // Cancellation not requested by the caller means an internal timeout (e.g. connect timeout).
        ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or ServerBusyException;

    private async Task TransferAsync(SegmentState segment, CancellationToken ct)
    {
        if (!Record.SupportsRanges && _scheduler!.PositionOf(segment) > 0)
        {
            // Without range support a broken stream can only restart from the first byte.
            _scheduler.Reset(segment);
            Interlocked.Exchange(ref _downloaded, 0);
            _file!.SetLength(0);
        }

        var offset = _scheduler!.PositionOf(segment);
        var end = Record.SupportsRanges ? _scheduler.EndOf(segment) : -1;
        await using var body = await _transport.OpenAsync(Record, offset, end, ct);
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            while (true)
            {
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(_options.ReadTimeout);
                    try
                    {
                        read = await body.ReadAsync(buffer.AsMemory(0, _options.BufferSize), idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No data received for {_options.ReadTimeout.TotalSeconds:0} s.");
                    }
                }

                if (read == 0)
                {
                    if (segment.HasKnownEnd)
                        throw new IOException("The server closed the connection early.");
                    _scheduler.CloseOpenEnd(segment);
                    return;
                }

                var usable = _scheduler.Clamp(segment, offset, read);
                if (usable > 0)
                {
                    await _limiter.WaitAsync(usable, ct);
                    await RandomAccess.WriteAsync(_file!.SafeFileHandle, buffer.AsMemory(0, usable), offset, ct);
                    offset += usable;
                    _scheduler.Advance(segment, usable);
                    Interlocked.Add(ref _downloaded, usable);
                }

                // Either the range is done or another connection took over its tail.
                if (usable < read || _scheduler.IsComplete(segment))
                    return;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.CheckpointInterval);
        while (await timer.WaitForNextTickAsync(ct))
            await CheckpointAsync();
    }

    /// <summary>
    /// Snapshot first, then flush: every byte counted in the snapshot was already written,
    /// so the saved state never claims data that is not on disk.
    /// </summary>
    private async Task CheckpointAsync()
    {
        await _checkpointGate.WaitAsync();
        try
        {
            if (_scheduler is null || _file is null)
                return;
            var snapshot = _scheduler.Snapshot();
            _file.Flush(flushToDisk: true);
            Record.Segments = snapshot;
            await _onCheckpoint(Record.Clone());
        }
        finally
        {
            _checkpointGate.Release();
        }
    }
}
