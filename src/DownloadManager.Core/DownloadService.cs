using System.Net;

namespace DownloadManager.Core;

/// <summary>
/// Owns the download list, the categories and the queue. "Start" begins a download immediately;
/// "Queued" downloads are started in list order while the queue is running,
/// never exceeding the configured number of simultaneous downloads.
/// </summary>
public sealed class DownloadService : IAsyncDisposable
{
    private sealed class ActiveJob(DownloadJob job, bool fromQueue)
    {
        public DownloadJob Job { get; } = job;
        public bool FromQueue { get; } = fromQueue;
        public CancellationTokenSource Cts { get; } = new();
        public DownloadStatus StopStatus { get; set; } = DownloadStatus.Paused;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly object _lock = new();
    private readonly DownloadStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly EngineOptions _engineTemplate;
    private readonly RateLimiter _limiter = new();
    private readonly List<DownloadRecord> _items;
    private readonly List<Category> _categories;
    private readonly Dictionary<long, ActiveJob> _active = [];
    private AppSettings _settings;
    private bool _pumpSuspended;

    public DownloadService(DownloadStore store, HttpClient? http = null, EngineOptions? engineTemplate = null)
    {
        _store = store;
        _ownsHttp = http is null;
        _http = http ?? CreateHttpClient();
        _engineTemplate = engineTemplate ?? new EngineOptions();
        _settings = store.LoadSettings();
        _limiter.BytesPerSecond = _settings.SpeedLimitKBps * 1024;
        _items = store.LoadAll();
        _categories = store.LoadCategories();

        // Downloads interrupted by a crash or forced exit wait for the user.
        foreach (var item in _items.Where(i => i.Status == DownloadStatus.Downloading))
        {
            item.Status = DownloadStatus.Paused;
            store.Update(item);
        }
        lock (_lock) Pump();
    }

    public static HttpClient CreateHttpClient(CookieContainer? cookies = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None, // byte ranges refer to the raw entity
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (cookies is not null)
            handler.CookieContainer = cookies;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadManager/1.0");
        return client;
    }

    /// <summary>Raised on a worker thread when a download finishes successfully.</summary>
    public event Action<DownloadSnapshot>? DownloadCompleted;

    /// <summary>Raised when categories are added, changed or removed.</summary>
    public event Action? CategoriesChanged;

    public AppSettings Settings
    {
        get { lock (_lock) return _settings.Clone(); }
    }

    public IReadOnlyList<Category> Categories
    {
        get { lock (_lock) return _categories.OrderBy(c => c.SortOrder).Select(c => c.Clone()).ToList(); }
    }

    public bool QueueRunning
    {
        get { lock (_lock) return _settings.QueueRunning; }
    }

    public IReadOnlyList<DownloadSnapshot> GetSnapshots()
    {
        lock (_lock)
            return _items.OrderBy(i => i.SortOrder).Select(SnapshotLocked).ToList();
    }

    public DownloadSnapshot? GetSnapshot(long id)
    {
        lock (_lock) return Find(id) is { } record ? SnapshotLocked(record) : null;
    }

    /// <summary>The download's byte ranges, live while it runs.</summary>
    public IReadOnlyList<SegmentView> GetSegments(long id)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(id, out var active) && active.Job.SegmentsView() is { } live)
                return live;
            return Find(id) is { } record
                ? record.Segments.Select(s => new SegmentView(s.Start, s.End, s.Written, false)).ToList()
                : [];
        }
    }

    /// <summary>
    /// Adds a download. Without a folder it goes to its category's folder. Without a category or file name, the
    /// category is guessed from the URL now and confirmed once the server reports the real file name and type.
    /// </summary>
    public long Add(string url, string? directory = null, string? fileName = null, bool startNow = true, RequestContext? context = null,
        long? categoryId = null)
    {
        if (!Urls.TryParseHttp(url, out var uri))
            throw new ArgumentException("Enter a valid http:// or https:// address.", nameof(url));

        lock (_lock)
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? null : FileNames.Sanitize(fileName);
            var category = _categories.FirstOrDefault(c => c.Id == categoryId) ?? Core.Categories.MatchUrl(_categories, uri.AbsoluteUri, name);
            var record = new DownloadRecord
            {
                Url = uri.AbsoluteUri,
                Directory = string.IsNullOrWhiteSpace(directory) ? category.ResolveFolder(_settings.DownloadFolder) : directory,
                FileName = name,
                Status = startNow ? DownloadStatus.Paused : DownloadStatus.Queued,
                SortOrder = _items.Count == 0 ? 0 : _items.Max(i => i.SortOrder) + 1,
                AddedUtc = DateTime.UtcNow,
                Context = context is { IsEmpty: false } ? context : null,
                CategoryId = category.Id,
                AutoCategory = categoryId is null && name is null,
            };
            _store.Insert(record);
            _items.Add(record);
            if (startNow)
                StartLocked(record, fromQueue: false);
            else
                Pump();
            return record.Id;
        }
    }

    /// <summary>Starts (or resumes) a download right away, outside the queue limits.</summary>
    public void Start(long id)
    {
        lock (_lock)
        {
            if (Find(id) is { Status: not DownloadStatus.Completed } record)
                StartLocked(record, fromQueue: false);
        }
    }

    /// <summary>Puts a download back in the queue to be started in list order.</summary>
    public void Enqueue(long id)
    {
        lock (_lock)
        {
            if (Find(id) is { Status: DownloadStatus.Paused or DownloadStatus.Failed } record)
            {
                record.Status = DownloadStatus.Queued;
                record.Error = null;
                _store.Update(record);
                Pump();
            }
        }
    }

    public Task PauseAsync(long id) => StopAsync(a => a.Job.Record.Id == id, _ => DownloadStatus.Paused, alsoUnqueue: id);

    public Task PauseAllAsync() => StopAsync(_ => true, _ => DownloadStatus.Paused);

    public void StartQueue()
    {
        lock (_lock)
        {
            _settings.QueueRunning = true;
            _store.SaveSettings(_settings);
            Pump();
        }
    }

    /// <summary>Stops the queue; downloads it started go back to waiting in the queue.</summary>
    public Task StopQueueAsync()
    {
        lock (_lock)
        {
            _settings.QueueRunning = false;
            _store.SaveSettings(_settings);
        }
        return StopAsync(a => a.FromQueue, _ => DownloadStatus.Queued);
    }

    public async Task RemoveAsync(long id, bool deleteFile)
    {
        await PauseAsync(id);
        lock (_lock)
        {
            if (Find(id) is not { } record)
                return;
            _items.Remove(record);
            _store.Delete(id);
            TryDelete(record.PartPath);
            if (deleteFile && record.Status == DownloadStatus.Completed)
                TryDelete(record.FinalPath);
        }
    }

    /// <summary>Moves a download up (negative) or down (positive) in the list and queue order.</summary>
    public void Move(long id, int delta)
    {
        lock (_lock)
        {
            var ordered = _items.OrderBy(i => i.SortOrder).ToList();
            var index = ordered.FindIndex(i => i.Id == id);
            var target = index + Math.Sign(delta);
            if (index < 0 || target < 0 || target >= ordered.Count)
                return;
            (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].SortOrder != i)
                {
                    ordered[i].SortOrder = i;
                    _store.Update(ordered[i]);
                }
            }
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        lock (_lock)
        {
            var queueRunning = _settings.QueueRunning;
            _settings = settings.Clone();
            _settings.MaxConnections = Math.Clamp(settings.MaxConnections, 1, 32);
            _settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 20);
            _settings.SpeedLimitKBps = Math.Max(0, settings.SpeedLimitKBps);
            _settings.QueueRunning = queueRunning;
            _limiter.BytesPerSecond = _settings.SpeedLimitKBps * 1024;
            _store.SaveSettings(_settings);
            Pump();
        }
    }

    /// <summary>Adds a category at the end of the list (Id 0) or saves changes to an existing one.</summary>
    public Category SaveCategory(Category category)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
            throw new ArgumentException("A category needs a name.", nameof(category));
        Category result;
        lock (_lock)
        {
            var saved = category.Clone();
            saved.Name = saved.Name.Trim();
            saved.Folder = saved.Folder.Trim();
            var existing = _categories.FirstOrDefault(c => c.Id == saved.Id);
            if (existing is null)
            {
                saved.Id = 0;
                saved.Key = null;
                saved.SortOrder = _categories.Count == 0 ? 0 : _categories.Max(c => c.SortOrder) + 1;
                _store.SaveCategory(saved);
                _categories.Add(saved);
            }
            else
            {
                saved.Key = existing.Key;
                saved.SortOrder = existing.SortOrder;
                _store.SaveCategory(saved);
                _categories[_categories.IndexOf(existing)] = saved;
            }
            result = saved.Clone();
        }
        CategoriesChanged?.Invoke();
        return result;
    }

    /// <summary>Removes a category; its downloads move to the general one, which cannot be removed.</summary>
    public void DeleteCategory(long id)
    {
        lock (_lock)
        {
            if (_categories.FirstOrDefault(c => c.Id == id) is not { IsGeneral: false } category)
                return;
            var general = _categories.First(c => c.IsGeneral);
            _store.DeleteCategory(id, general.Id);
            _categories.Remove(category);
            foreach (var record in _items.Where(r => r.CategoryId == id))
                record.CategoryId = general.Id;
            foreach (var active in _active.Values.Where(a => a.Job.Record.CategoryId == id))
                active.Job.Record.CategoryId = general.Id;
        }
        CategoriesChanged?.Invoke();
    }

    /// <summary>Files a download under another category; the file itself stays where it is.</summary>
    public void SetCategory(long id, long categoryId)
    {
        lock (_lock)
        {
            if (Find(id) is not { } record || _categories.All(c => c.Id != categoryId))
                return;
            record.CategoryId = categoryId;
            record.AutoCategory = false;
            if (_active.TryGetValue(id, out var active))
            {
                active.Job.Record.CategoryId = categoryId;
                active.Job.Record.AutoCategory = false;
            }
            _store.Update(record);
        }
    }

    /// <summary>
    /// Asks the server at <paramref name="newUrl"/> about the file and compares it with what was already
    /// downloaded, typically because the old link expired.
    /// </summary>
    public async Task<UrlCheck> CheckNewUrlAsync(long id, string newUrl, CancellationToken ct = default)
    {
        if (!Urls.TryParseHttp(newUrl, out var uri))
            throw new ArgumentException("Enter a valid http:// or https:// address.", nameof(newUrl));
        DownloadRecord record;
        lock (_lock)
        {
            var stored = Find(id) ?? throw new InvalidOperationException("The download no longer exists.");
            record = _active.TryGetValue(id, out var active) ? active.Job.Record.Clone() : stored.Clone();
            record.Status = stored.Status;
        }

        using var ownHttp = record.Context is { Cookies.Count: > 0 } context
            ? CreateHttpClient(RequestContexts.ToCookieContainer(context.Cookies))
            : null;
        var probe = await HttpProbe.ProbeAsync(ownHttp ?? _http, uri.AbsoluteUri, ct, record.Context);
        return UrlCheck.Compare(record, uri.AbsoluteUri, probe);
    }

    /// <summary>
    /// Switches a download to a new address. With <paramref name="keepData"/> the bytes already on disk are kept
    /// (use it when <see cref="CheckNewUrlAsync"/> found the same file); otherwise the download starts again from zero.
    /// A download that was running continues on the new address.
    /// </summary>
    public async Task ChangeUrlAsync(long id, UrlCheck check, bool keepData)
    {
        bool wasRunning;
        lock (_lock)
            wasRunning = _active.ContainsKey(id);
        if (wasRunning)
            await PauseAsync(id);

        lock (_lock)
        {
            if (Find(id) is not { Status: not DownloadStatus.Completed } record || _active.ContainsKey(id))
                return;
            record.Url = check.NewUrl;
            record.Error = null;
            if (record.Status == DownloadStatus.Failed)
                record.Status = DownloadStatus.Paused;
            if (keepData)
            {
                // From now on the new server's validators guard every resume.
                record.SupportsRanges = check.Probe.SupportsRanges;
                record.ETag = check.Probe.ETag;
                record.LastModified = check.Probe.LastModified;
            }
            else
            {
                TryDelete(record.PartPath);
                record.Segments = [];
                record.TotalSize = null;
                record.ETag = null;
                record.LastModified = null;
            }
            _store.Update(record);
            if (wasRunning)
                StartLocked(record, fromQueue: false);
        }
    }

    /// <summary>Checkpoints and stops every running download so it can resume on the next launch.</summary>
    public Task ShutdownAsync() =>
        StopAsync(_ => true, a => a.FromQueue ? DownloadStatus.Queued : DownloadStatus.Paused, pumpAfter: false);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        if (_ownsHttp)
            _http.Dispose();
    }

    private DownloadSnapshot SnapshotLocked(DownloadRecord r)
    {
        _active.TryGetValue(r.Id, out var active);
        var live = active?.Job.Record;
        var name = live?.FileName ?? r.FileName ?? DisplayNameFromUrl(r.Url);
        return new DownloadSnapshot(
            r.Id, r.Url, name, live?.Directory ?? r.Directory,
            r.Status == DownloadStatus.Completed ? r.FinalPath : null,
            live?.TotalSize ?? r.TotalSize,
            active?.Job.BytesDownloaded ?? r.DownloadedBytes,
            r.Status, r.Error,
            active?.Job.ActiveConnections ?? 0,
            r.SortOrder,
            live?.SupportsRanges ?? r.SupportsRanges,
            r.AddedUtc,
            live?.CategoryId ?? r.CategoryId,
            r.CompletedUtc,
            live?.ETag ?? r.ETag,
            live?.LastModified ?? r.LastModified,
            live?.ContentType ?? r.ContentType);
    }

    private async Task StopAsync(Func<ActiveJob, bool> which, Func<ActiveJob, DownloadStatus> statusAfter, long? alsoUnqueue = null, bool pumpAfter = true)
    {
        Task[] tasks;
        lock (_lock)
        {
            if (!pumpAfter)
                _pumpSuspended = true;
            var targets = _active.Values.Where(which).ToList();
            foreach (var a in targets)
            {
                a.StopStatus = statusAfter(a);
                a.Cts.Cancel();
            }
            tasks = targets.Select(a => a.Task).ToArray();

            if (alsoUnqueue is { } id && !_active.ContainsKey(id) && Find(id) is { Status: DownloadStatus.Queued } waiting)
            {
                waiting.Status = DownloadStatus.Paused;
                _store.Update(waiting);
            }
        }
        await Task.WhenAll(tasks);
    }

    private void StartLocked(DownloadRecord record, bool fromQueue)
    {
        if (_active.ContainsKey(record.Id) || record.Status == DownloadStatus.Completed)
            return;

        record.Status = DownloadStatus.Downloading;
        record.Error = null;
        _store.Update(record);

        var options = new EngineOptions
        {
            MaxConnections = _settings.MaxConnections,
            MinSegmentSize = _engineTemplate.MinSegmentSize,
            BufferSize = _engineTemplate.BufferSize,
            ReadTimeout = _engineTemplate.ReadTimeout,
            MaxRetries = _engineTemplate.MaxRetries,
            RetryBaseDelay = _engineTemplate.RetryBaseDelay,
            CheckpointInterval = _engineTemplate.CheckpointInterval,
        };
        var job = new DownloadJob(record.Clone(), _http, _limiter, options, OnCheckpoint, OnProbed);
        var active = new ActiveJob(job, fromQueue);
        _active[record.Id] = active;
        active.Task = Task.Run(() => RunJobAsync(active));
    }

    private async Task RunJobAsync(ActiveJob active)
    {
        DownloadStatus final;
        string? error = null;
        try
        {
            await active.Job.RunAsync(active.Cts.Token);
            final = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (active.Cts.IsCancellationRequested)
        {
            final = active.StopStatus;
        }
        catch (Exception ex)
        {
            final = DownloadStatus.Failed;
            error = ex.Message;
        }

        DownloadSnapshot? completed = null;
        lock (_lock)
        {
            _active.Remove(active.Job.Record.Id);
            if (Find(active.Job.Record.Id) is { } record)
            {
                CopyProgress(active.Job.Record, record);
                record.Status = final;
                record.Error = error;
                if (final == DownloadStatus.Completed)
                {
                    record.Context = null; // session cookies are not kept longer than needed
                    completed = SnapshotLocked(record);
                }
                _store.Update(record);
            }
            Pump();
        }
        active.Cts.Dispose();
        if (completed is not null)
            DownloadCompleted?.Invoke(completed);
    }

    /// <summary>The server revealed the file name and type: confirm a guessed category before any data is written.</summary>
    private void OnProbed(DownloadRecord record, ProbeResult probe)
    {
        lock (_lock)
        {
            if (!record.AutoCategory)
                return;
            var guessed = _categories.FirstOrDefault(c => c.Id == record.CategoryId);
            var category = Core.Categories.Match(_categories, record.FileName, probe.ContentType);
            // The folder follows the category only if it was the guessed category's folder, not one the user chose.
            if (guessed is null || SamePath(record.Directory, guessed.ResolveFolder(_settings.DownloadFolder)))
                record.Directory = category.ResolveFolder(_settings.DownloadFolder);
            record.CategoryId = category.Id;
            record.AutoCategory = false;
        }
    }

    private Task OnCheckpoint(DownloadRecord snapshot)
    {
        lock (_lock)
        {
            if (Find(snapshot.Id) is { } record)
            {
                CopyProgress(snapshot, record);
                if (snapshot.Status == DownloadStatus.Completed)
                {
                    record.Status = DownloadStatus.Completed;
                    record.CompletedUtc = snapshot.CompletedUtc;
                }
                _store.Update(record);
            }
        }
        return Task.CompletedTask;
    }

    private static void CopyProgress(DownloadRecord from, DownloadRecord to)
    {
        to.FileName = from.FileName;
        to.Directory = from.Directory;
        to.CategoryId = from.CategoryId;
        to.AutoCategory = from.AutoCategory;
        to.ContentType = from.ContentType;
        to.TotalSize = from.TotalSize;
        to.ETag = from.ETag;
        to.LastModified = from.LastModified;
        to.SupportsRanges = from.SupportsRanges;
        to.Segments = from.Segments.Select(s => s.Clone()).ToList();
        to.CompletedUtc = from.CompletedUtc;
    }

    private void Pump()
    {
        if (!_settings.QueueRunning || _pumpSuspended)
            return;
        while (_active.Count < _settings.MaxConcurrentDownloads)
        {
            var next = _items
                .Where(i => i.Status == DownloadStatus.Queued && !_active.ContainsKey(i.Id))
                .OrderBy(i => i.SortOrder)
                .FirstOrDefault();
            if (next is null)
                return;
            StartLocked(next, fromQueue: true);
        }
    }

    private DownloadRecord? Find(long id) => _items.FirstOrDefault(i => i.Id == id);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string DisplayNameFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? FileNames.Sanitize(FileNames.FromUrl(uri)) : url;

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
