using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AngleSharp;
using AngleSharp.Dom;
using Baton.Core.Resources;
using System.Globalization;

namespace Baton.Core.Grabber;

/// <summary>Timings and sizes of a run; tests make them small.</summary>
public sealed class GrabberEngineOptions
{
    public int Workers { get; set; } = 4;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A site asking to wait longer than this (Retry-After) is not waited for: the address fails.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Pages and style sheets bigger than this are not read.</summary>
    public long MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>
/// One run of a grabber project. Pages are visited level by level (the start page, then the pages it links to...),
/// so the depth limit means the same thing however fast each server answers. Only static HTML and CSS are read;
/// scripts are never run. Every request goes through the per-host throttle, honours robots.txt (unless the project
/// turned that off) and Retry-After, and every address is normalized and checked for traps before it is followed.
/// </summary>
/// <remarks>
/// Files mode: the files found are listed with their size and validators (a HEAD request each) and sent to a queue
/// later. Mirror mode: pages, style sheets, scripts, images and the wanted files are saved under the project's
/// folder, then every saved page and style sheet is rewritten to point at the local copies.
/// </remarks>
public sealed class GrabberEngine
{
    private const int MaxRedirects = 10;

    private readonly GrabberProject _project;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, GrabberFileState> _previous;
    private readonly GrabberEngineOptions _options;
    private readonly GrabberFilters _filters;
    private readonly HostThrottle _throttle;
    private readonly TrapGuard _traps;
    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsTxt>>> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _present = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GrabberFileState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<GrabberItem> _items = new();
    private readonly ConcurrentQueue<(string Relative, Uri Url, bool IsCss)> _saved = new();
    private readonly LocalPaths? _paths;
    private readonly string _userAgent;
    private readonly Func<int, CancellationToken, Task> _consume;
    private int _pagesStarted, _pagesVisited, _pagesQueued, _files, _errors, _robotsBlocked, _skipped;
    private long _bytes;
    private volatile string? _limit;
    private volatile bool _incomplete;
    private volatile string? _current;
    private string? _startPage;

    /// <param name="http">A client that does not follow redirects by itself (each hop is checked here) and holds the project's cookies.</param>
    /// <param name="previous">What earlier runs recorded, by normalized address.</param>
    /// <param name="consume">The app's speed limit and hourly quota, applied to every byte read (null: none).</param>
    public GrabberEngine(GrabberProject project, HttpClient http, IReadOnlyDictionary<string, GrabberFileState> previous, string userAgent,
        GrabberEngineOptions? options = null, Func<int, CancellationToken, Task>? consume = null)
    {
        _consume = consume ?? ((_, _) => Task.CompletedTask);
        _project = project;
        _http = http;
        _previous = previous;
        _options = options ?? new GrabberEngineOptions();
        _filters = new GrabberFilters(project);
        _throttle = new HostThrottle(project.RequestsPerSecond);
        _traps = new TrapGuard(project.MaxPagesPerPattern);
        _userAgent = string.IsNullOrWhiteSpace(project.UserAgent) ? userAgent : project.UserAgent.Trim();
        if (project.Mode == GrabberMode.Mirror)
        {
            _paths = new LocalPaths(project.Folder, previous.Values
                .Where(s => s.LocalPath is not null)
                .Select(s => new KeyValuePair<string, string>(s.Url, s.LocalPath!)));
        }
    }

    /// <summary>Raised on a worker thread for each file found (and in mirror mode each page saved).</summary>
    public event Action<GrabberItem>? ItemFound;

    public IReadOnlyCollection<GrabberItem> Items => _items;

    /// <summary>What to remember for synchronization, by normalized address (mirror: everything saved; files: everything found).</summary>
    public IReadOnlyDictionary<string, GrabberFileState> States => _states;

    /// <summary>Addresses seen on the site in this run (also those filtered out by size).</summary>
    public IReadOnlyCollection<string> Present => _present.Keys.ToList();

    /// <summary>The run saw the whole site within its limits, so a file it did not see is really gone.</summary>
    public bool Complete => !_incomplete && _limit is null;

    public GrabberProgress Progress => new(
        Volatile.Read(ref _pagesVisited), Volatile.Read(ref _pagesQueued), Volatile.Read(ref _files), Interlocked.Read(ref _bytes),
        Volatile.Read(ref _errors), Volatile.Read(ref _robotsBlocked), Volatile.Read(ref _skipped), _current, _limit, false);

    public async Task<GrabberRunResult> RunAsync(CancellationToken ct)
    {
        var cancelled = false;
        try
        {
            await CrawlAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            _incomplete = true;
        }

        // The pages saved so far are rewritten even after a stop, so the copy is always usable.
        if (_paths is not null)
            await RewriteAsync(CancellationToken.None);

        if (!cancelled && Complete)
            ReportRemoved();

        var items = _items.ToList();
        var files = items.Where(i => !i.IsPage).ToList();
        _current = null;
        return new GrabberRunResult(
            Volatile.Read(ref _pagesVisited), files.Count(f => f.Status != GrabberItemStatus.Removed),
            items.Count(i => i.Status == GrabberItemStatus.New), items.Count(i => i.Status == GrabberItemStatus.Changed),
            items.Count(i => i.Status == GrabberItemStatus.Unchanged), items.Count(i => i.Status == GrabberItemStatus.Removed),
            items.Count(i => i.Status == GrabberItemStatus.Failed), Interlocked.Read(ref _bytes), _limit, cancelled, _startPage);
    }

    private async Task CrawlAsync(CancellationToken ct)
    {
        var start = CrawlUrls.Normalize(new Uri(_project.StartUrl.Trim())) ?? throw new ArgumentException("Invalid start address.");
        _seen.TryAdd(start.AbsoluteUri, 0);
        var level = new List<(Uri Url, string Source)> { (start, "") };
        Volatile.Write(ref _pagesQueued, 1);
        for (var depth = 0; level.Count > 0; depth++)
        {
            var next = new ConcurrentQueue<(Uri Url, string Source)>();
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Workers), CancellationToken = ct };
            await Parallel.ForEachAsync(level, parallel, async (page, token) =>
            {
                try
                {
                    await VisitPageAsync(page.Url, page.Source, depth, next, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException or HttpRequestException or FormatException or TimeoutException)
                {
                    // One page that cannot be read or saved does not end the run.
                    Interlocked.Increment(ref _errors);
                    _incomplete = true;
                    Emit(Failed(page.Url, page.Source, ex.Message, isPage: true));
                }
            });
            ct.ThrowIfCancellationRequested();
            if (_limit is not null)
                break;
            level = next.ToList();
            Interlocked.Add(ref _pagesQueued, level.Count);
        }
    }

    // ------------------------------------------------------------------ pages

    private async Task VisitPageAsync(Uri url, string source, int depth, ConcurrentQueue<(Uri, string)> next, CancellationToken ct)
    {
        if (_limit is not null)
            return;
        if (Interlocked.Increment(ref _pagesStarted) > _project.MaxPages)
        {
            SetLimit("pages");
            return;
        }
        if (!await AllowedByRobotsAsync(url, ct))
            return;

        _current = url.AbsoluteUri;
        var (response, final, error) = await FetchAsync(url, HttpMethod.Get, source, null, ct);
        if (response is null)
        {
            Interlocked.Increment(ref _errors);
            _incomplete = true;
            if (depth == 0)
                Emit(Failed(url, source, error, isPage: true));
            return;
        }
        using (response)
        {
            if (Gone(response.StatusCode) || !response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _errors);
                if (!Gone(response.StatusCode))
                    _incomplete = true; // a server error hides what the page links to
                if (depth == 0)
                    Emit(Failed(url, source, $"{(int)response.StatusCode} {response.ReasonPhrase}", isPage: true));
                return;
            }

            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type is not null && !HtmlText.IsHtmlType(type))
            {
                // It looked like a page but is a file (e.g. "download.php?id=7").
                await FileFromResponseAsync(url, final, response, source, ct);
                return;
            }

            var body = await ReadLimitedAsync(response, _options.MaxDocumentBytes, ct);
            if (body is null || (type is null && !HtmlText.LooksLikeHtml(body.AsSpan())))
            {
                if (body is null)
                    Interlocked.Increment(ref _skipped);
                return;
            }
            Interlocked.Increment(ref _pagesVisited);
            var html = HtmlText.Decode(body, body.Length, response.Content.Headers.ContentType?.ToString());
            var document = PageLinks.Parse(html);
            if (_paths is not null && _project.StripScripts)
            {
                // Taken out before the page is saved or its links followed: the scripts are neither kept nor downloaded.
                PageLinks.RemoveScripts(document);
                html = document.ToHtml();
            }
            var pageKey = CrawlUrls.Key(url);
            _present.TryAdd(pageKey, 0);

            if (_paths is not null)
                await SavePageAsync(url, final, html, body, source, response, depth == 0, ct);

            var links = PageLinks.Find(document, final);
            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                if (_limit is not null)
                    return;
                if (CrawlUrls.Normalize(link.Url) is not { } target || !_filters.DomainAllowed(target) || _filters.Excluded(target))
                    continue;
                if (TrapGuard.Inspect(target) is not null)
                {
                    Interlocked.Increment(ref _skipped);
                    continue;
                }
                var finalPage = final.AbsoluteUri;
                if (link.Kind == LinkKind.Page && CrawlUrls.LooksLikePage(target))
                {
                    if (depth >= _project.MaxDepth || _seen.ContainsKey(target.AbsoluteUri))
                        continue;
                    if (!_traps.TryCount(target))
                    {
                        _incomplete = true; // which pages of the shape were left out depends on timing
                        Interlocked.Increment(ref _skipped);
                        continue;
                    }
                    if (_seen.TryAdd(target.AbsoluteUri, 0))
                        next.Enqueue((target, finalPage));
                }
                else if (_paths is null)
                {
                    await FileLinkAsync(target, link.Kind, finalPage, ct);
                }
                else
                {
                    await MirrorResourceAsync(target, link.Kind, finalPage, ct);
                }
            }
        }
    }

    // ------------------------------------------------------------------ files mode

    private static readonly HashSet<string> PageParts = ["css", "js", "mjs"];

    /// <summary>Files mode: a link to a file is checked (HEAD) and listed if it passes the filters.</summary>
    private async Task FileLinkAsync(Uri url, LinkKind kind, string source, CancellationToken ct)
    {
        var extension = CrawlUrls.ExtensionOf(url);
        // Style sheets and scripts are how a page is built, not files the user is after, unless asked for by type.
        if ((kind == LinkKind.Stylesheet || PageParts.Contains(extension)) && !_project.Extensions.Contains(extension))
            return;
        if (!_filters.FileWanted(url, extension))
            return;
        var key = url.AbsoluteUri;
        if (!_seen.TryAdd(key, 0))
            return;
        if (Volatile.Read(ref _files) >= _project.MaxFiles)
        {
            SetLimit("files");
            return;
        }
        if (!await AllowedByRobotsAsync(url, ct))
            return;

        _current = key;
        var (info, gone, error) = await ProbeAsync(url, source, ct);
        if (gone)
        {
            _present.TryRemove(key, out _);
            if (_previous.TryGetValue(key, out var old) && old.RemovedUtc is null)
                Emit(Removed(old));
            return;
        }
        if (info is null)
        {
            Interlocked.Increment(ref _errors);
            Emit(Failed(url, source, error, isPage: false));
            return;
        }
        _present.TryAdd(key, 0);
        AddCandidate(url, info, source);
    }

    /// <summary>Files mode: an address fetched as a page turned out to be a file.</summary>
    private async Task FileFromResponseAsync(Uri url, Uri final, HttpResponseMessage response, string source, CancellationToken ct)
    {
        var info = InfoOf(response, final);
        // The type is the one of the name the server gives ("report.pdf" for "download.php?id=7").
        var extension = Path.GetExtension(info.Name).TrimStart('.').ToLowerInvariant();
        if (!_filters.FileWanted(url, extension))
            return;
        _present.TryAdd(url.AbsoluteUri, 0);
        if (Volatile.Read(ref _files) >= _project.MaxFiles)
        {
            SetLimit("files");
            return;
        }
        if (_paths is null)
            AddCandidate(url, info, source);
        else if (_filters.SizeWanted(info.Size))
            await SaveBodyAsync(url, response, info, source, isCss: false, ct);
    }

    private void AddCandidate(Uri url, FileInfoResult info, string source)
    {
        if (!_filters.SizeWanted(info.Size))
            return;
        if (info.Size is { } size && Interlocked.Add(ref _bytes, size) > _project.MaxTotalMB * 1024 * 1024)
        {
            Interlocked.Add(ref _bytes, -size);
            SetLimit("size");
            return;
        }
        if (Interlocked.Increment(ref _files) > _project.MaxFiles)
        {
            SetLimit("files");
            return;
        }
        var key = url.AbsoluteUri;
        _previous.TryGetValue(key, out var old);
        var status = Compare(old, info.ETag, info.LastModified, info.Size);
        var now = DateTime.UtcNow;
        _states[key] = new GrabberFileState
        {
            Url = key, ETag = info.ETag, LastModified = info.LastModified, Size = info.Size, ContentType = info.ContentType, SourcePage = source,
            FirstSeenUtc = old?.FirstSeenUtc ?? now, LastSeenUtc = now,
        };
        Emit(new GrabberItem(key, info.Name, Path.GetExtension(info.Name).TrimStart('.').ToLowerInvariant(), info.Size, info.ContentType, source,
            status, info.ETag, info.LastModified));
    }

    /// <summary>New, changed or unchanged since the last run: ETag first, then Last-Modified with the size, then the size alone.</summary>
    public static GrabberItemStatus Compare(GrabberFileState? old, string? etag, string? lastModified, long? size)
    {
        if (old is null || old.RemovedUtc is not null)
            return GrabberItemStatus.New;
        if (old.ETag is not null && etag is not null)
            return old.ETag == etag ? GrabberItemStatus.Unchanged : GrabberItemStatus.Changed;
        if (old.LastModified is not null && lastModified is not null)
            return old.LastModified == lastModified && (old.Size is null || size is null || old.Size == size)
                ? GrabberItemStatus.Unchanged
                : GrabberItemStatus.Changed;
        if (old.Size is not null && size is not null)
            return old.Size == size ? GrabberItemStatus.Unchanged : GrabberItemStatus.Changed;
        return GrabberItemStatus.Unchanged; // nothing to compare: not fetched again
    }

    private sealed record FileInfoResult(string Name, long? Size, string? ContentType, string? ETag, string? LastModified);

    private static FileInfoResult InfoOf(HttpResponseMessage response, Uri final)
    {
        var headers = response.Content.Headers;
        var size = response.StatusCode == HttpStatusCode.PartialContent ? headers.ContentRange?.Length : headers.ContentLength;
        return new FileInfoResult(
            FileNames.FromResponse(headers.ContentDisposition, final),
            size,
            headers.ContentType?.MediaType,
            response.Headers.ETag?.ToString(),
            headers.LastModified?.ToString("R"));
    }

    /// <summary>Size, type and validators of a file: HEAD, or the first byte with GET where HEAD is refused.</summary>
    private async Task<(FileInfoResult? Info, bool Gone, string? Error)> ProbeAsync(Uri url, string source, CancellationToken ct)
    {
        var (response, final, error) = await FetchAsync(url, HttpMethod.Head, source, null, ct);
        if (response is not null && response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            (response, final, error) = await FetchAsync(url, HttpMethod.Get, source, r => r.Headers.Range = new RangeHeaderValue(0, 0), ct);
        }
        if (response is null)
            return (null, false, error);
        using (response)
        {
            if (Gone(response.StatusCode))
                return (null, true, null);
            if (!response.IsSuccessStatusCode)
                return (null, false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
            return (InfoOf(response, final), false, null);
        }
    }

    // ------------------------------------------------------------------ mirror mode

    private async Task SavePageAsync(Uri url, Uri final, string html, byte[] body, string source, HttpResponseMessage response, bool isStart,
        CancellationToken ct)
    {
        var key = CrawlUrls.Key(url);
        var relative = _paths!.Map(url, "text/html");
        var full = _paths.FullPath(relative);
        var hash = Convert.ToHexString(SHA256.HashData(body));
        _previous.TryGetValue(key, out var old);
        var status = old is null || old.RemovedUtc is not null ? GrabberItemStatus.New
            : old.ContentHash == hash ? GrabberItemStatus.Unchanged : GrabberItemStatus.Changed;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Saved as UTF-8 text now and rewritten at the end, when every local file is known.
        await WriteAtomicAsync(full, html, ct);
        ZoneIdentifier.TryMark(full, url.AbsoluteUri, source.Length > 0 ? source : null);
        _saved.Enqueue((relative, final, false));
        Interlocked.Add(ref _bytes, body.Length);
        if (isStart)
            _startPage = full;
        var now = DateTime.UtcNow;
        _states[key] = new GrabberFileState
        {
            Url = key, LocalPath = relative, Size = body.Length, ContentType = "text/html", SourcePage = source, ContentHash = hash,
            ETag = response.Headers.ETag?.ToString(), LastModified = response.Content.Headers.LastModified?.ToString("R"),
            FirstSeenUtc = old?.FirstSeenUtc ?? now, LastSeenUtc = now,
        };
        Emit(new GrabberItem(key, Path.GetFileName(relative), "html", body.Length, "text/html", source, status, LocalPath: relative, IsPage: true));
    }

    /// <summary>Mirror: an image, script, style sheet or linked file is saved (only when it changed, for a synchronization).</summary>
    private async Task MirrorResourceAsync(Uri url, LinkKind kind, string source, CancellationToken ct)
    {
        if (_project.StripScripts && CrawlUrls.ExtensionOf(url) is "js" or "mjs")
            return; // a script linked some other way (a plain link) is not copied either
        var key = url.AbsoluteUri;
        var extension = CrawlUrls.ExtensionOf(url);
        // Files the page links to obey the type filter; what the page itself needs to show is always kept.
        if (kind == LinkKind.Page && !_filters.FileWanted(url, extension))
            return;
        if (_seen.ContainsKey(key))
            return;
        if (!_seen.TryAdd(key, 0))
            return;
        if (Volatile.Read(ref _files) >= _project.MaxFiles)
        {
            SetLimit("files");
            return;
        }
        if (!await AllowedByRobotsAsync(url, ct))
            return;

        _current = key;
        _previous.TryGetValue(key, out var old);
        var isCss = kind == LinkKind.Stylesheet || extension == "css";
        var localExists = old?.LocalPath is { } oldPath && File.Exists(_paths!.FullPath(oldPath));
        // Style sheets are always read again: the addresses inside them must be followed.
        var conditional = !isCss && localExists && old!.RemovedUtc is null;
        var (response, final, error) = await FetchAsync(url, HttpMethod.Get, source, request =>
        {
            if (!conditional)
                return;
            if (old!.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            if (old.LastModified is { } modified && DateTimeOffset.TryParse(modified, out var date))
                request.Headers.IfModifiedSince = date;
        }, ct);
        if (response is null)
        {
            Interlocked.Increment(ref _errors);
            Emit(Failed(url, source, error, isPage: false));
            return;
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified && old is not null)
            {
                _present.TryAdd(key, 0);
                Interlocked.Increment(ref _files);
                var now = DateTime.UtcNow;
                var kept = old.Clone();
                kept.LastSeenUtc = now;
                _states[key] = kept;
                Emit(new GrabberItem(key, Path.GetFileName(old.LocalPath!), extension, old.Size, old.ContentType, source, GrabberItemStatus.Unchanged,
                    old.ETag, old.LastModified, old.LocalPath));
                return;
            }
            if (Gone(response.StatusCode))
            {
                if (old is not null && old.RemovedUtc is null)
                    Emit(Removed(old));
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _errors);
                Emit(Failed(url, source, $"{(int)response.StatusCode} {response.ReasonPhrase}", isPage: false));
                return;
            }
            var info = InfoOf(response, final);
            if (kind == LinkKind.Page && !_filters.SizeWanted(info.Size))
            {
                _present.TryAdd(key, 0);
                return;
            }
            if (_project.MaxSizeBytes > 0 && info.Size > _project.MaxSizeBytes)
            {
                _present.TryAdd(key, 0);
                Interlocked.Increment(ref _skipped);
                return;
            }
            await SaveBodyAsync(url, response, info, source, isCss || info.ContentType == "text/css", ct);
        }
    }

    /// <summary>Writes a response to the copy (through a temporary file), then reads a style sheet for its own addresses.</summary>
    private async Task SaveBodyAsync(Uri url, HttpResponseMessage response, FileInfoResult info, string source, bool isCss, CancellationToken ct)
    {
        var key = CrawlUrls.Key(url);
        string relative, full;
        try
        {
            relative = _paths!.Map(url, info.ContentType);
            full = _paths.FullPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No safe place for it (e.g. a folder name taken by a file): this file is skipped, not the page.
            Interlocked.Increment(ref _errors);
            Emit(Failed(url, source, ex.Message, isPage: false));
            return;
        }
        var temp = full + ".dmtmp";
        long written = 0;
        var limit = _project.MaxTotalMB * 1024 * 1024;
        byte[]? cssBytes = null;
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                MemoryStream? css = isCss ? new MemoryStream() : null;
                int read;
                while ((read = await ReadIdleAsync(input, buffer, ct)) > 0)
                {
                    await _consume(read, ct);
                    written += read;
                    if (Interlocked.Add(ref _bytes, read) > limit || (_project.MaxSizeBytes > 0 && written > _project.MaxSizeBytes))
                        throw new LimitException(Interlocked.Read(ref _bytes) > limit ? "size" : null);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (css is not null && css.Length + read <= _options.MaxDocumentBytes)
                        css.Write(buffer, 0, read);
                }
                cssBytes = css?.ToArray();
            }
            if (_project.StripScripts && !await WithoutScriptsAsync(temp, Path.GetExtension(full), ct))
            {
                // A document that would run scripts and cannot be cleaned: not kept, its link stays online.
                Interlocked.Add(ref _bytes, -written);
                TryDelete(temp);
                _present.TryAdd(key, 0);
                Interlocked.Increment(ref _skipped);
                return;
            }
            File.Move(temp, full, overwrite: true);
        }
        catch (LimitException limitHit)
        {
            Interlocked.Add(ref _bytes, -written);
            TryDelete(temp);
            if (limitHit.Limit is { } name)
                SetLimit(name);
            else
                Interlocked.Increment(ref _skipped);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException)
        {
            Interlocked.Add(ref _bytes, -written);
            TryDelete(temp);
            Interlocked.Increment(ref _errors);
            Emit(Failed(url, source, ex.Message, isPage: false));
            return;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Add(ref _bytes, -written);
            TryDelete(temp);
            throw;
        }
        ZoneIdentifier.TryMark(full, url.AbsoluteUri, source.Length > 0 ? source : null);
        Interlocked.Increment(ref _files);
        _present.TryAdd(key, 0);
        _previous.TryGetValue(key, out var old);
        var status = Compare(old, info.ETag, info.LastModified, written);
        var now = DateTime.UtcNow;
        _states[key] = new GrabberFileState
        {
            Url = key, LocalPath = relative, ETag = info.ETag, LastModified = info.LastModified, Size = written, ContentType = info.ContentType,
            SourcePage = source, FirstSeenUtc = old?.FirstSeenUtc ?? now, LastSeenUtc = now,
        };
        Emit(new GrabberItem(key, Path.GetFileName(relative), Path.GetExtension(relative).TrimStart('.').ToLowerInvariant(), written, info.ContentType,
            source, status, info.ETag, info.LastModified, relative));

        if (cssBytes is not null)
        {
            _saved.Enqueue((relative, response.RequestMessage?.RequestUri ?? url, true));
            var text = HtmlText.Decode(cssBytes, cssBytes.Length, response.Content.Headers.ContentType?.ToString());
            // Saved as UTF-8 text (with its @charset saying so), so the rewrite reads it in the encoding it was found in.
            await WriteAtomicAsync(full, HtmlText.CssAsUtf8(text), ct);
            foreach (var link in CssLinks.Find(text, response.RequestMessage?.RequestUri ?? url))
            {
                if (_limit is not null)
                    return;
                if (CrawlUrls.Normalize(link.Url) is { } target && _filters.DomainAllowed(target) && !_filters.Excluded(target)
                    && TrapGuard.Inspect(target) is null)
                    await MirrorResourceAsync(target, link.Kind, url.AbsoluteUri, ct);
            }
        }
    }

    private sealed class LimitException(string? limit) : Exception
    {
        public string? Limit { get; } = limit;
    }

    /// <summary>Points the saved pages and style sheets at the local files; addresses not saved stay absolute (online).</summary>
    private async Task RewriteAsync(CancellationToken ct)
    {
        foreach (var (relative, url, isCss) in _saved.DistinctBy(s => s.Relative, StringComparer.OrdinalIgnoreCase))
        {
            var full = _paths!.FullPath(relative);
            try
            {
                var text = await File.ReadAllTextAsync(full, ct);
                PageLinks.Visitor visit = (_, resolved, _) => LocalTarget(relative, resolved);
                string output;
                if (isCss)
                {
                    output = CssLinks.Walk(text, url, visit);
                }
                else
                {
                    var document = PageLinks.Parse(text);
                    if (_project.StripScripts)
                        PageLinks.RemoveScripts(document);
                    PageLinks.Walk(document, url, visit);
                    foreach (var element in document.QuerySelectorAll("base"))
                        element.Remove();
                    SetUtf8(document);
                    output = document.ToHtml();
                }
                await WriteAtomicAsync(full, output, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _errors);
            }
        }
    }

    /// <summary>
    /// Copy without scripts: a file the browser would open as a document able to run them (judged by the extension it is
    /// saved with, which decides how it opens from disk) is cleaned in place; false when it cannot be, and is not kept.
    /// </summary>
    private static async Task<bool> WithoutScriptsAsync(string path, string extension, CancellationToken ct)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".svg" or ".xml": // an XML file with an XHTML root opens as a page that runs its scripts
                var svg = PageLinks.RemoveSvgScripts(await File.ReadAllTextAsync(path, ct));
                if (svg is null)
                    return false;
                await File.WriteAllTextAsync(path, svg, ct);
                return true;
            case ".html" or ".htm" or ".xhtml" or ".xht" or ".shtml":
                var document = PageLinks.Parse(await File.ReadAllTextAsync(path, ct));
                PageLinks.RemoveScripts(document);
                await File.WriteAllTextAsync(path, document.ToHtml(), ct);
                return true;
            case ".svgz" or ".xsl" or ".xslt" or ".hta" or ".mht" or ".mhtml":
                return false;
            default:
                return true;
        }
    }

    /// <summary>Writes the whole text to a temporary file, then puts it in place: never a half-written page.</summary>
    private static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        var temp = path + ".dmtmp";
        await File.WriteAllTextAsync(temp, text, ct);
        // The replaced file's "downloaded from the Internet" mark goes to the new one.
        string? zone = null;
        try
        {
            if (File.Exists(path + ":Zone.Identifier"))
                zone = await File.ReadAllTextAsync(path + ":Zone.Identifier", ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        File.Move(temp, path, overwrite: true);
        if (zone is not null)
        {
            try
            {
                await File.WriteAllTextAsync(path + ":Zone.Identifier", zone, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
    }

    private string LocalTarget(string fromRelative, Uri resolved)
    {
        var key = CrawlUrls.Normalize(resolved) is { } normalized ? normalized.AbsoluteUri : null;
        var fragment = resolved.Fragment;
        if (key is not null && _present.ContainsKey(key) && _paths!.Existing(key) is { } target && File.Exists(_paths.FullPath(target)))
            return LocalPaths.Relative(fromRelative, target) + fragment;
        return resolved.AbsoluteUri; // not in the copy: the link keeps working online
    }

    /// <summary>The copy is written in UTF-8, so its declared charset must say so.</summary>
    private static void SetUtf8(IDocument document)
    {
        var declared = false;
        foreach (var meta in document.QuerySelectorAll("meta[charset]"))
        {
            meta.SetAttribute("charset", "utf-8");
            declared = true;
        }
        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]"))
        {
            if (string.Equals(meta.GetAttribute("http-equiv"), "content-type", StringComparison.OrdinalIgnoreCase))
            {
                meta.SetAttribute("content", "text/html; charset=utf-8");
                declared = true;
            }
        }
        if (!declared && document.Head is { } head)
        {
            var meta = document.CreateElement("meta");
            meta.SetAttribute("charset", "utf-8");
            head.Prepend(meta);
        }
    }

    // ------------------------------------------------------------------ requests

    private async Task<bool> AllowedByRobotsAsync(Uri url, CancellationToken ct)
    {
        if (!_project.RespectRobots)
            return true;
        var origin = url.GetLeftPart(UriPartial.Authority);
        var robots = await _robots.GetOrAdd(origin, _ => new Lazy<Task<RobotsTxt>>(() => LoadRobotsAsync(url, ct))).Value;
        if (robots.IsAllowed(url))
            return true;
        Interlocked.Increment(ref _robotsBlocked);
        return false;
    }

    private async Task<RobotsTxt> LoadRobotsAsync(Uri url, CancellationToken ct)
    {
        var address = new Uri(url, "/robots.txt");
        var current = address;
        for (var hop = 0; hop <= 5; hop++)
        {
            HttpResponseMessage? response;
            try
            {
                response = await SendWithRetriesAsync(current, HttpMethod.Get, null, null, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                _incomplete = true; // nothing was seen because robots.txt could not be read: nothing is "removed"
                return RobotsTxt.DisallowAll;
            }
            if (response is null)
            {
                _incomplete = true;
                return RobotsTxt.DisallowAll;
            }
            using (response)
            {
                if (IsRedirect(response) && response.Headers.Location is { } location)
                {
                    // Only within the project, as for pages: a robots.txt elsewhere (another site, a local address) is not
                    // fetched, and counts as missing.
                    if (!Uri.TryCreate(current, location, out var target) || CrawlUrls.Normalize(target) is not { } normalized
                        || !_filters.DomainAllowed(normalized) || TrapGuard.Inspect(normalized) is not null)
                        return RobotsTxt.AllowAll;
                    current = normalized;
                    continue;
                }
                var status = (int)response.StatusCode;
                if (status is >= 400 and < 500)
                    return RobotsTxt.AllowAll; // no robots.txt: everything allowed
                if (!response.IsSuccessStatusCode)
                {
                    _incomplete = true;
                    return RobotsTxt.DisallowAll; // unreachable: assume nothing is
                }
                var bytes = await ReadLimitedAsync(response, 512 * 1024, ct) ?? [];
                var robots = RobotsTxt.Parse(HtmlText.Decode(bytes, bytes.Length, "text/plain; charset=utf-8"), _userAgent);
                if (robots.CrawlDelay is { } delay)
                    _throttle.SetCrawlDelay(url, delay);
                return robots;
            }
        }
        return RobotsTxt.AllowAll;
    }

    /// <summary>
    /// Fetches an address, following redirects one hop at a time: a hop to a site outside the project, to an excluded
    /// address or to one robots.txt forbids is not followed.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, Uri Final, string? Error)> FetchAsync(Uri url, HttpMethod method, string referrer,
        Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage? response;
            try
            {
                response = await SendWithRetriesAsync(current, method, referrer, configure, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                return (null, current, ex.Message);
            }
            if (response is null)
                return (null, current, "busy");
            if (!IsRedirect(response) || response.Headers.Location is not { } location)
                return (response, current, null);
            response.Dispose();
            if (hop >= MaxRedirects)
                return (null, current, "too many redirects");
            var target = new Uri(current, location);
            if (CrawlUrls.Normalize(target) is not { } normalized || !_filters.DomainAllowed(normalized) || _filters.Excluded(normalized)
                || TrapGuard.Inspect(normalized) is not null)
                return (null, current, "redirected outside the project");
            if (!await AllowedByRobotsAsync(normalized, ct))
                return (null, current, "robots.txt");
            current = target;
        }
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;

    private static bool Gone(HttpStatusCode status) => status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>
    /// One request through the throttle. 429 and 503 are retried after the server's Retry-After (or a growing pause),
    /// during which the whole host waits; network errors are retried too. Null when the server stays busy.
    /// </summary>
    private async Task<HttpResponseMessage?> SendWithRetriesAsync(Uri url, HttpMethod method, string? referrer, Action<HttpRequestMessage>? configure,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _throttle.WaitAsync(url, ct);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
            if (Uri.TryCreate(referrer, UriKind.Absolute, out var from) && from.Scheme is "http" or "https")
                request.Headers.Referrer = from;
            configure?.Invoke(request);
            HttpResponseMessage response;
            try
            {
                response = await HttpErrors.SendAsync(_http, request, _options.RequestTimeout, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException && attempt < _options.MaxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }
            if (!HostThrottle.IsBusy(response.StatusCode))
                return response;
            var wait = HostThrottle.RetryAfter(response, DateTimeOffset.UtcNow) ?? Backoff(attempt);
            response.Dispose();
            if (attempt >= _options.MaxRetries || wait > _options.MaxRetryAfter)
                return null;
            _throttle.Pause(url, wait); // every request to this host waits, not only this one
        }
    }

    private TimeSpan Backoff(int attempt) => _options.RetryBaseDelay * Math.Pow(2, Math.Min(attempt, 5));

    /// <summary>
    /// One read of a body, given up when no data arrives within the request timeout: a server that sends the headers
    /// and then stalls must not hold the run forever.
    /// </summary>
    private async Task<int> ReadIdleAsync(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_options.RequestTimeout);
        try
        {
            return await input.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(CultureInfo.CurrentCulture, Strings.ErrorNoData, _options.RequestTimeout.TotalSeconds));
        }
    }

    private async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit)
            return null;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await ReadIdleAsync(input, chunk, ct)) > 0)
        {
            await _consume(read, ct);
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // ------------------------------------------------------------------ results

    private void ReportRemoved()
    {
        foreach (var old in _previous.Values)
        {
            if (old.RemovedUtc is null && !_present.ContainsKey(old.Url) && !_states.ContainsKey(old.Url))
                Emit(Removed(old));
        }
    }

    private static GrabberItem Removed(GrabberFileState old)
    {
        var name = old.LocalPath is { } local ? Path.GetFileName(local) : FileNames.FromUrl(new Uri(old.Url));
        var isPage = HtmlText.IsHtmlType(old.ContentType);
        return new GrabberItem(old.Url, name, Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), old.Size, old.ContentType, old.SourcePage ?? "",
            GrabberItemStatus.Removed, old.ETag, old.LastModified, old.LocalPath, IsPage: isPage);
    }

    private static GrabberItem Failed(Uri url, string source, string? error, bool isPage)
    {
        var name = FileNames.Sanitize(FileNames.FromUrl(url));
        return new GrabberItem(url.AbsoluteUri, name, CrawlUrls.ExtensionOf(url), null, null, source, GrabberItemStatus.Failed, Error: error, IsPage: isPage);
    }

    private readonly ConcurrentDictionary<string, byte> _emitted = new(StringComparer.Ordinal);

    private void Emit(GrabberItem item)
    {
        // An address is listed once, whatever the number of pages that link to it.
        if (!_emitted.TryAdd(item.Url, 0))
            return;
        _items.Enqueue(item);
        ItemFound?.Invoke(item);
    }

    private void SetLimit(string limit) => _limit ??= limit;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
