using System.Buffers;
using System.Globalization;
using System.Net;
using DownloadManager.Core.Net;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core.Media;

/// <summary>
/// Downloads an HLS or DASH stream: its segments in parallel (through the same network, speed limit and queues as
/// any download) into a folder next to the target, each kept once complete so a pause or crash resumes where it
/// stopped. Then the segments are joined into one file per track, and ffmpeg, if available, copies the tracks into
/// one MP4. Without ffmpeg the joined tracks are kept as they are and the download says so.
/// </summary>
public sealed class MediaJob : IDownloadJob
{
    private readonly HttpClient _sharedHttp;
    private readonly NetworkContext? _network;
    private readonly RateLimiter _limiter;
    private readonly EngineOptions _options;
    private readonly Func<DownloadRecord, Task> _onCheckpoint;
    private readonly Func<string?> _ffmpeg;
    private long _downloaded;
    private int _connections;
    private int _done;
    private int _total;

    public MediaJob(DownloadRecord record, HttpClient http, RateLimiter limiter, EngineOptions options, Func<DownloadRecord, Task>? onCheckpoint,
        NetworkContext? network, Func<string?> ffmpeg)
    {
        if (record.Media is null)
            throw new ArgumentException("Not a stream download.", nameof(record));
        Record = record;
        _sharedHttp = http;
        _network = network;
        _limiter = limiter;
        _options = options;
        _onCheckpoint = onCheckpoint ?? (_ => Task.CompletedTask);
        _ffmpeg = ffmpeg;
        _downloaded = record.Segments.Sum(s => s.Written);
    }

    public DownloadRecord Record { get; }

    public long BytesDownloaded => Interlocked.Read(ref _downloaded);

    public int ActiveConnections => Volatile.Read(ref _connections);

    public List<SegmentView>? SegmentsView() => null;

    /// <summary>Segments finished and in total, for tests.</summary>
    public (int Done, int Total) Progress => (Volatile.Read(ref _done), Volatile.Read(ref _total));

    public static string PartsFolderOf(DownloadRecord record) =>
        Path.Combine(record.Directory, $"{Path.GetFileNameWithoutExtension(record.FileName ?? "video")}.{record.Id}.parts");

    public async Task RunAsync(CancellationToken ct)
    {
        Record.Error = null;
        var context = Record.Context;
        HttpClient? own = null;
        if (context is { Cookies.Count: > 0 })
            own = _network?.CreateHttpClient(RequestContexts.ToCookieContainer(context.Cookies)) ??
                  DownloadService.CreateHttpClient(RequestContexts.ToCookieContainer(context.Cookies));
        var http = own ?? _sharedHttp;
        try
        {
            var media = Record.Media!;
            var plan = await WithRetriesAsync(() => MediaCatalog.ResolveAsync(http, Record.Url, media.VariantId, context, ct, media.AudioId, media.SubtitleIds), ct);
            await DownloadAsync(http, plan, ct);
            await FinishAsync(http, plan, ct);
        }
        finally
        {
            own?.Dispose();
        }
    }

    private async Task DownloadAsync(HttpClient http, MediaPlan plan, CancellationToken ct)
    {
        var folder = PartsFolderOf(Record);
        Directory.CreateDirectory(folder);
        var work = new List<(string Path, MediaSegment Segment)>();
        for (var t = 0; t < plan.Tracks.Count; t++)
        {
            var track = plan.Tracks[t];
            if (track.Init is { } init)
                work.Add((Path.Combine(folder, $"t{t}_init.seg"), init));
            for (var i = 0; i < track.Segments.Count; i++)
                work.Add((Path.Combine(folder, $"t{t}_{i:D6}.seg"), track.Segments[i]));
        }

        // Finished segments from an earlier run are kept: only the missing ones are downloaded.
        long existing = 0;
        var pending = new Queue<(string Path, MediaSegment Segment)>();
        foreach (var item in work)
        {
            if (File.Exists(item.Path))
                existing += new FileInfo(item.Path).Length;
            else
                pending.Enqueue(item);
        }
        Interlocked.Exchange(ref _downloaded, existing);
        Volatile.Write(ref _total, work.Count);
        Volatile.Write(ref _done, work.Count - pending.Count);
        UpdateEstimate(plan);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var checkpoints = CheckpointLoopAsync(plan, jobCts.Token);
        try
        {
            var gate = new object();
            var workers = Enumerable.Range(0, Math.Clamp(_options.MaxConnections, 1, 16)).Select(async _ =>
            {
                while (true)
                {
                    (string Path, MediaSegment Segment) next;
                    lock (gate)
                    {
                        if (!pending.TryDequeue(out next))
                            return;
                    }
                    try
                    {
                        await DownloadSegmentAsync(http, next.Segment, next.Path, jobCts.Token);
                    }
                    catch (Exception) when (!jobCts.IsCancellationRequested)
                    {
                        jobCts.Cancel(); // one failed segment stops the others
                        throw;
                    }
                    Interlocked.Increment(ref _done);
                }
            }).ToArray();
            try
            {
                await Task.WhenAll(workers);
            }
            catch when (!ct.IsCancellationRequested && workers.Any(w => w.IsFaulted))
            {
                throw workers.Where(w => w.IsFaulted).SelectMany(w => w.Exception!.InnerExceptions)
                    .FirstOrDefault(e => e is not OperationCanceledException) ?? new OperationCanceledException();
            }
        }
        finally
        {
            jobCts.Cancel();
            await checkpoints.ContinueWith(_ => { }, TaskScheduler.Default);
            UpdateEstimate(plan);
            await _onCheckpoint(Record.Clone());
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task DownloadSegmentAsync(HttpClient http, MediaSegment segment, string path, CancellationToken ct)
    {
        for (var failures = 1; ; failures++)
        {
            var written = 0L;
            try
            {
                Interlocked.Increment(ref _connections);
                try
                {
                    written = await FetchAsync(http, segment, path + ".tmp", ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _connections);
                }
                File.Move(path + ".tmp", path, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested && failures <= _options.MaxRetries)
            {
                Interlocked.Add(ref _downloaded, -written);
                await Task.Delay(_options.RetryBaseDelay * Math.Pow(2, Math.Min(failures - 1, 5)), ct);
            }
            catch
            {
                Interlocked.Add(ref _downloaded, -written);
                throw;
            }
        }
    }

    /// <summary>Writes one segment to <paramref name="path"/>; returns the bytes counted, to undo them if it fails.</summary>
    private async Task<long> FetchAsync(HttpClient http, MediaSegment segment, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, segment.Url);
        Record.Context.ApplyTo(request);
        var range = MediaCatalog.RangeOf(segment);
        if (range is not null)
            request.Headers.Range = range;
        using var response = await HttpErrors.SendAsync(http, request, _options.ReadTimeout, ct);
        if (!response.IsSuccessStatusCode)
            throw HttpErrors.FromResponse(response);

        // A server that ignores the range sends the whole resource: the wanted part is cut out of it.
        var skip = range is not null && response.StatusCode == HttpStatusCode.OK ? segment.Offset!.Value : 0;
        var limit = range is not null ? segment.Length!.Value : long.MaxValue;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous);
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        long counted = 0;
        try
        {
            while (counted < limit)
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
                    break;
                var start = 0;
                if (skip > 0)
                {
                    var dropped = (int)Math.Min(skip, read);
                    skip -= dropped;
                    start = dropped;
                }
                var usable = (int)Math.Min(read - start, limit - counted);
                if (usable <= 0)
                    continue;
                await _limiter.WaitAsync(usable, ct);
                await file.WriteAsync(buffer.AsMemory(start, usable), ct);
                counted += usable;
                Interlocked.Add(ref _downloaded, usable);
            }
        }
        catch
        {
            Interlocked.Add(ref _downloaded, -counted);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (range is not null && counted != limit)
        {
            Interlocked.Add(ref _downloaded, -counted);
            throw new IOException("The server closed the connection early.");
        }
        if (response.Content.Headers.ContentLength is { } expected && range is null && counted != expected)
        {
            Interlocked.Add(ref _downloaded, -counted);
            throw new IOException("The server closed the connection early.");
        }
        return 0; // counted bytes stay: the segment is complete
    }

    /// <summary>The size shown while downloading: the manifest's estimate, then the average of the finished segments.</summary>
    private void UpdateEstimate(MediaPlan plan)
    {
        var done = Volatile.Read(ref _done);
        var total = Volatile.Read(ref _total);
        var downloaded = BytesDownloaded;
        long? estimate = done >= Math.Min(3, total) && done > 0 ? downloaded + (long)((double)downloaded / done * (total - done)) : plan.EstimatedBytes;
        if (estimate is { } size && size < downloaded)
            estimate = downloaded;
        Record.TotalSize = estimate;
        Record.SupportsRanges = true; // a paused stream resumes from its finished segments
        Record.Segments = [new SegmentState { Start = 0, End = (estimate ?? downloaded) - 1, Written = downloaded }];
    }

    private async Task CheckpointLoopAsync(MediaPlan plan, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.CheckpointInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            UpdateEstimate(plan);
            await _onCheckpoint(Record.Clone());
        }
    }

    /// <summary>Joins the segments of each track, then makes the final file (or files).</summary>
    private async Task FinishAsync(HttpClient http, MediaPlan plan, CancellationToken ct)
    {
        var folder = PartsFolderOf(Record);
        var joined = new List<string>();
        for (var t = 0; t < plan.Tracks.Count; t++)
        {
            var track = plan.Tracks[t];
            var target = Path.Combine(folder, $"track{t}{track.Extension}");
            var parts = new List<string>();
            if (track.Init is not null)
                parts.Add(Path.Combine(folder, $"t{t}_init.seg"));
            parts.AddRange(Enumerable.Range(0, track.Segments.Count).Select(i => Path.Combine(folder, $"t{t}_{i:D6}.seg")));
            await JoinAsync(parts, target, ct);
            joined.Add(target);
        }

        // Subtitles, after the video: a track that cannot be downloaded is left out and named, the video completes.
        var subtitles = new List<(string Path, string? Language, string Label, List<SubtitleCue> Cues)>();
        var missing = new List<string>();
        var plans = plan.Subtitles ?? [];
        for (var s = 0; s < plans.Count; s++)
        {
            var texts = new List<(string, TimeSpan)>();
            try
            {
                for (var i = 0; i < plans[s].Segments.Count; i++)
                {
                    var segment = plans[s].Segments[i];
                    var part = SubtitlePart(folder, s, i);
                    if (!File.Exists(part))
                        await DownloadSegmentAsync(http, segment, part, ct);
                    texts.Add((await File.ReadAllTextAsync(part, ct), TimeSpan.FromSeconds(segment.TimeShift)));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (IsTransient(ex) || ex is DownloadFailedException or AuthenticationRequiredException))
            {
                missing.Add(plans[s].Label);
                continue;
            }
            var cues = WebVtt.Cues(texts);
            if (cues.Count == 0)
                continue;
            var path = Path.Combine(folder, $"sub{s}.vtt");
            await File.WriteAllTextAsync(path, WebVtt.ToVtt(cues), ct);
            subtitles.Add((path, plans[s].Language, plans[s].Label, cues));
        }

        var baseName = Path.GetFileNameWithoutExtension(Record.FileName ?? "video");
        var ffmpeg = _ffmpeg();
        var needsMux = plan.Tracks.Count > 1 || plan.Tracks[0].Extension == ".ts";
        var embed = Record.Media!.SubtitleMode == "embed" && subtitles.Count > 0;
        var embedded = false;
        string? note = null;
        var outputs = new List<string>();

        if ((needsMux || embed) && ffmpeg is not null)
        {
            var container = plan.Tracks.Any(t => t.Extension == ".webm") ? ".mkv" : ".mp4";
            var muxed = Path.Combine(folder, "output" + container);
            var error = await Ffmpeg.MuxAsync(ffmpeg, joined, muxed, ct, embed ? subtitles.Select(s => (s.Path, s.Language, s.Label)).ToList() : null);
            if (error is null)
            {
                outputs.Add(Place(muxed, baseName + container));
                embedded = embed;
            }
            else
            {
                note = string.Format(CultureInfo.CurrentCulture, Strings.MediaMuxFailed, error);
            }
        }
        else if (needsMux)
        {
            note = plan.Tracks.Count > 1 ? Strings.MediaNoFfmpegTracks : Strings.MediaNoFfmpegTs;
        }
        if (embed && ffmpeg is null)
            note = note is null ? Strings.MediaSubtitlesNoFfmpeg : $"{note} {Strings.MediaSubtitlesNoFfmpeg}";
        if (missing.Count > 0)
        {
            var failed = string.Format(CultureInfo.CurrentCulture, Strings.MediaSubtitlesFailed, string.Join(", ", missing));
            note = note is null ? failed : $"{note} {failed}";
        }

        if (outputs.Count == 0)
        {
            // Kept as downloaded: one file, or the video and the audio side by side.
            if (joined.Count == 1)
            {
                outputs.Add(Place(joined[0], baseName + plan.Tracks[0].Extension));
            }
            else
            {
                for (var t = 0; t < joined.Count; t++)
                    outputs.Add(Place(joined[t], $"{baseName}.{plan.Tracks[t].Kind}{plan.Tracks[t].Extension}"));
            }
        }

        // Not inside the video: next to it, "Name.it.vtt" or "Name.it.srt", as players look for them.
        if (!embedded)
        {
            var srt = Record.Media.SubtitleMode == "srt";
            for (var s = 0; s < subtitles.Count; s++)
            {
                var (path, language, _, cues) = subtitles[s];
                var tag = LanguageTag(language) ?? $"sub{s + 1}";
                if (srt)
                    await File.WriteAllTextAsync(path, WebVtt.ToSrt(cues), ct);
                // Named after the video as saved ("Name (1).mp4" → "Name (1).it.vtt"), as players look for them.
                var videoName = outputs.Count == 1 ? Path.GetFileNameWithoutExtension(outputs[0]) : baseName;
                var file = Place(path, $"{videoName}.{tag}{(srt ? ".srt" : ".vtt")}");
                ZoneIdentifier.TryMark(file, Record.Url, Record.Context?.Referrer);
            }
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        foreach (var output in outputs)
            ZoneIdentifier.TryMark(output, Record.Url, Record.Context?.Referrer);
        Record.FileName = Path.GetFileName(outputs[0]);
        Record.TotalSize = outputs.Sum(o => new FileInfo(o).Length);
        Record.Segments = [new SegmentState { Start = 0, End = Record.TotalSize.Value - 1, Written = Record.TotalSize.Value }];
        Record.Media!.Note = note is null ? null : outputs.Count > 1 ? $"{note} ({string.Join(", ", outputs.Select(Path.GetFileName))})" : note;
        Record.Status = DownloadStatus.Completed;
        Record.CompletedUtc = DateTime.UtcNow;
        await _onCheckpoint(Record.Clone());
    }

    private static string SubtitlePart(string folder, int track, int index) => Path.Combine(folder, $"s{track}_{index:D6}.seg");

    /// <summary>A language code safe in a file name ("pt-BR"), or null.</summary>
    private static string? LanguageTag(string? language)
    {
        var tag = new string((language ?? "").Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(16).ToArray());
        return tag.Length > 0 ? tag : null;
    }

    /// <summary>Moves a finished file to the download folder under a name not taken yet.</summary>
    private string Place(string source, string name)
    {
        var target = FileNames.MakeUnique(Path.Combine(Record.Directory, FileNames.Sanitize(name)));
        File.Move(source, target);
        return target;
    }

    /// <summary>Concatenates the segment files in order (TS and fragmented MP4 segments are designed to be joined).</summary>
    public static async Task JoinAsync(IEnumerable<string> parts, string target, CancellationToken ct)
    {
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        foreach (var part in parts)
        {
            await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
            await input.CopyToAsync(output, ct);
        }
    }

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
                await Task.Delay(_options.RetryBaseDelay * Math.Pow(2, Math.Min(failures - 1, 5)), ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or ServerBusyException;
}
