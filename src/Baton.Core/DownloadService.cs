using System.Globalization;
using System.Net;
using Baton.Core.Integration;
using Baton.Core.Media;
using Baton.Core.Net;
using Baton.Core.Platform;
using Baton.Core.PostDownload;
using Baton.Core.Resources;
using Baton.Core.Security;

namespace Baton.Core;

/// <summary>
/// Owns the download list, the categories and the queues. "Start" begins a download immediately, outside any
/// limit; "Queued" downloads are started in order by their queue while it runs, within the queue's limit, the
/// global limit and the hourly quotas. Queues are started and stopped by the user or by their schedule.
/// </summary>
public sealed class DownloadService : IAsyncDisposable
{
    private sealed class ActiveJob(IDownloadJob job, bool fromQueue)
    {
        public IDownloadJob Job { get; } = job;
        public bool FromQueue { get; } = fromQueue;
        public CancellationTokenSource Cts { get; } = new();
        public DownloadStatus StopStatus { get; set; } = DownloadStatus.Paused;
        public Task Task { get; set; } = Task.CompletedTask;
        public long CountedBytes { get; set; }
    }

    private readonly object _lock = new();
    private readonly DownloadStore _store;
    private readonly HttpClient? _fixedHttp; // given by a test: used as is, whatever the network settings
    private HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly EngineOptions _engineTemplate;
    private readonly bool _customEngine;
    private NetworkContext _network;
    private NetworkCredential? _sessionProxyLogin;
    private readonly RateLimiter _limiter = new();
    private readonly List<DownloadRecord> _items;
    private readonly List<Category> _categories;
    private readonly List<DownloadQueue> _queues;
    private readonly Dictionary<long, ActiveJob> _active = [];
    private readonly IClock _clock;
    private readonly ScheduleTracker _tracker = new();
    private readonly Dictionary<long, HourlyQuota> _quotas = [];
    private readonly HourlyQuota _globalQuota = new();
    private readonly HashSet<long> _runStarted = []; // queues that started at least one download in the current run
    private readonly CancellationTokenSource _tickerCts = new();
    private readonly CancellationTokenSource _lifetime = new(); // stops the checks after a download when the app closes
    private readonly SemaphoreSlim _checkGate = new(1, 1); // one hash or scan at a time: they read whole files
    private readonly Task _ticker;
    private AppSettings _settings;
    private bool _pumpSuspended;

    /// <param name="clock">The time source; tests pass a fake one.</param>
    /// <param name="autoTick">False in tests that drive <see cref="Tick"/> themselves.</param>
    public DownloadService(DownloadStore store, HttpClient? http = null, EngineOptions? engineTemplate = null,
        IClock? clock = null, bool autoTick = true)
    {
        _store = store;
        _ownsHttp = false;
        _fixedHttp = http;
        _engineTemplate = engineTemplate ?? new EngineOptions();
        _customEngine = engineTemplate is not null;
        _clock = clock ?? SystemClock.Instance;
        _settings = store.LoadSettings();
        Credentials = new CredentialStore(store);
        Certificates = new CertificateTrust(store);
        _network = new NetworkContext(_settings, Credentials, Certificates);
        _http = http ?? _network.SharedHttp;
        _ = _network.Proxy.LoadPacAsync();
        _limiter.BytesPerSecond = _settings.SpeedLimitKBps * 1024;
        _items = store.LoadAll();
        _categories = store.LoadCategories();
        _queues = store.LoadQueues();

        var main = MainQueue;
        foreach (var item in _items)
        {
            var changed = false;
            // Downloads interrupted by a crash or forced exit wait for the user.
            if (item.Status == DownloadStatus.Downloading)
            {
                item.Status = DownloadStatus.Paused;
                changed = true;
            }
            if (item.QueueId is not { } queueId || _queues.All(q => q.Id != queueId))
            {
                item.QueueId = main.Id;
                changed = true;
            }
            if (changed)
                store.Update(item);
        }

        // The first evaluation happens now: an app started inside a scheduled window starts the queue.
        Tick();
        _ticker = autoTick ? TickLoopAsync(_tickerCts.Token) : Task.CompletedTask;
    }

    /// <summary>
    /// Runs again the checks the app closed before they finished. Called once the app has subscribed to
    /// <see cref="DownloadCompleted"/>, so their outcome is announced (notification, grabber) like any other.
    /// </summary>
    public void ResumePendingChecks()
    {
        List<long> unfinished;
        lock (_lock)
            unfinished = _items.Where(i => i.Status == DownloadStatus.Completed && i.Check is { Pending: true }).Select(i => i.Id).ToList();
        foreach (var id in unfinished)
            _ = RunChecksAsync(id, announce: true);
    }

    public static HttpClient CreateHttpClient(CookieContainer? cookies = null)
    {
        SocketsHttpHandler Handler(bool redirects)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = redirects,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.None, // byte ranges refer to the raw entity
                ConnectTimeout = TimeSpan.FromSeconds(20),
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectCallback = (connection, ct) => Net.UrlGuard.ConnectAsync(connection, viaProxy: false, ct),
            };
            if (cookies is not null)
                handler.CookieContainer = cookies;
            return handler;
        }
        var client = new HttpClient(new Net.GuardedRedirectHandler(Handler(true), Handler(false))) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppSettings.DefaultUserAgent);
        return client;
    }

    /// <summary>Saved and temporary logins for servers (HTTP authentication, FTP).</summary>
    public CredentialStore Credentials { get; }

    /// <summary>FTPS certificates the user accepted; the app sets <see cref="CertificateTrust.Ask"/> to ask about new ones.</summary>
    public CertificateTrust Certificates { get; }

    /// <summary>The network as configured now (proxy, timeouts...), for example to test the proxy.</summary>
    public NetworkContext Network
    {
        get { lock (_lock) return _network; }
    }

    /// <summary>
    /// Raised when a server (401, FTP login refused) or the proxy (407) wants a login the app does not have. The
    /// download is paused; <see cref="ProvideLogin"/> resumes it.
    /// </summary>
    public event Action<LoginRequest>? LoginRequired;

    /// <summary>
    /// Raised on a worker thread when a download finishes successfully, after its checks (hash, antivirus) if any:
    /// the snapshot carries their outcome.
    /// </summary>
    public event Action<DownloadSnapshot>? DownloadCompleted;

    /// <summary>The checks of a finished download changed (also when run again from Properties).</summary>
    public event Action<DownloadSnapshot>? CheckCompleted;

    /// <summary>The global speed limit and hourly quota, for transfers outside the queues (the grabber's offline copies).</summary>
    public async Task ConsumeBandwidthAsync(int bytes, CancellationToken ct)
    {
        await _limiter.WaitAsync(bytes, ct);
        while (true)
        {
            DateTime? freeAt;
            lock (_lock)
            {
                var now = _clock.UtcNow;
                _globalQuota.Add(bytes, now);
                bytes = 0;
                freeAt = _globalQuota.FreeAt(_settings.GlobalQuotaMBPerHour, now);
                if (freeAt is null)
                    return;
            }
            // Over the hourly quota: wait (checking every few seconds, the quota may be raised meanwhile).
            var wait = freeAt.Value - _clock.UtcNow;
            await Task.Delay(wait < TimeSpan.FromSeconds(5) && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Raised when categories are added, changed or removed.</summary>
    public event Action? CategoriesChanged;

    /// <summary>Raised when queues are added, changed, removed, started or stopped.</summary>
    public event Action? QueuesChanged;

    /// <summary>
    /// Raised when a queue that works in runs (with an end action or a schedule) has processed all its downloads;
    /// the app carries out the queue's end action.
    /// </summary>
    public event Action<DownloadQueue>? QueueFinished;

    public AppSettings Settings
    {
        get { lock (_lock) return _settings.Clone(); }
    }

    public IReadOnlyList<Category> Categories
    {
        get { lock (_lock) return _categories.OrderBy(c => c.SortOrder).Select(c => c.Clone()).ToList(); }
    }

    public IReadOnlyList<DownloadQueue> Queues
    {
        get { lock (_lock) return _queues.OrderBy(q => q.SortOrder).ThenBy(q => q.Id).Select(q => q.Clone()).ToList(); }
    }

    public long MainQueueId
    {
        get { lock (_lock) return MainQueue.Id; }
    }

    private DownloadQueue MainQueue => _queues.First(q => q.IsMain);

    /// <summary>Whether the main queue runs (the single queue of earlier versions).</summary>
    public bool QueueRunning
    {
        get { lock (_lock) return MainQueue.Running; }
    }

    public DateTime UtcNow => _clock.UtcNow;

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

    public IReadOnlyList<QueueStatus> GetQueueStatuses()
    {
        lock (_lock)
        {
            var now = _clock.UtcNow;
            return _queues.OrderBy(q => q.SortOrder).ThenBy(q => q.Id).Select(q =>
            {
                var next = ScheduleCalculator.NextStart(q.Schedule, now, _clock.Zone);
                var state = q.Running ? QueueState.Running : next is not null ? QueueState.Scheduled : QueueState.Stopped;
                return new QueueStatus(q.Id, state, next, _items.Count(i => i.QueueId == q.Id),
                    _active.Values.Count(a => a.Job.Record.QueueId == q.Id));
            }).ToList();
        }
    }

    /// <summary>The queue the extension asked for, by name or key; the main queue when absent or unknown.</summary>
    public long ResolveQueue(string? nameOrKey)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(nameOrKey))
                return MainQueue.Id;
            var wanted = nameOrKey.Trim();
            // The extension lists the names as shown (a built-in name in the interface language) and may keep an older one.
            return (_queues.FirstOrDefault(q => string.Equals(q.Name, wanted, StringComparison.CurrentCultureIgnoreCase)
                                                || string.Equals(q.DisplayName, wanted, StringComparison.CurrentCultureIgnoreCase))
                    ?? _queues.FirstOrDefault(q => q.Key == wanted)
                    ?? MainQueue).Id;
        }
    }

    /// <summary>
    /// Adds a download. Without a folder it goes to its category's folder. Without a category or file name, the
    /// category is guessed from the URL now and confirmed once the server reports the real file name and type.
    /// Without a queue it joins the main queue.
    /// </summary>
    /// <param name="expectedHash">A hash to verify once the file is downloaded (see <see cref="FileHash.TryParse"/>).</param>
    public long Add(string url, string? directory = null, string? fileName = null, bool startNow = true, RequestContext? context = null,
        long? categoryId = null, long? queueId = null, string? expectedHash = null)
    {
        PostCheck? check = null;
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            if (!FileHash.TryParse(expectedHash, out var algorithm, out var hex))
                throw new ArgumentException(Strings.ErrorHashFormat, nameof(expectedHash));
            check = new PostCheck { HashAlgorithm = algorithm, ExpectedHash = hex };
        }
        if (!Urls.TryParseDownload(url, out var parsed))
            throw new ArgumentException(Strings.ErrorInvalidUrl);
        // A login written in the address is kept encrypted with the download, never in the address shown.
        var (uri, user, password) = Urls.SplitLogin(parsed);
        if (user is not null)
            context = (context ?? new RequestContext(null, null, [])) with { Username = user, Password = password };

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
                SortOrder = NextSortOrder(),
                AddedUtc = _clock.UtcNow,
                Context = context is { IsEmpty: false } ? context : null,
                CategoryId = category.Id,
                AutoCategory = categoryId is null && name is null,
                QueueId = _queues.FirstOrDefault(q => q.Id == queueId)?.Id ?? MainQueue.Id,
                Check = check,
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

    /// <summary>
    /// What a video or audio address offers: its qualities with an estimated size, or why it cannot be downloaded
    /// (protected, live, or from a site whose terms forbid it). Asked with the same network, cookies and referrer
    /// the download would use.
    /// </summary>
    public async Task<MediaInfoReply> ProbeMediaAsync(MediaRequest request, CancellationToken ct = default)
    {
        HttpClient http;
        bool owned;
        lock (_lock)
            (http, owned) = ClientFor(request.ToContext());
        try
        {
            return await Task.Run(() => MediaCatalog.ProbeAsync(http, request.Url, request.ToContext(), request.PageUrl, ct), ct);
        }
        finally
        {
            if (owned)
                http.Dispose();
        }
    }

    /// <summary>
    /// Adds a stream in the chosen quality. It is downloaded like any other download (queues, speed limit, proxy)
    /// and put together at the end. Without a file name it is named after the page title.
    /// </summary>
    public long AddMedia(MediaRequest request, string kind, string? label, string? directory = null, string? fileName = null,
        bool startNow = true, long? categoryId = null, long? queueId = null)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException(Strings.ErrorStreamScheme);
        if (string.IsNullOrEmpty(request.VariantId))
            throw new ArgumentException(Strings.ErrorNoQuality);
        if (MediaPolicy.IsRestricted(request.Url) || MediaPolicy.IsRestricted(request.PageUrl))
            throw new ArgumentException(Strings.MediaBlockedTerms, nameof(request));

        var name = string.IsNullOrWhiteSpace(fileName) ? MediaCatalog.FileNameFor(request.Title, request.Url, ".mp4") : FileNames.Sanitize(fileName);
        var context = request.ToContext();
        lock (_lock)
        {
            var category = _categories.FirstOrDefault(c => c.Id == categoryId) ?? Core.Categories.Match(_categories, name, "video/mp4");
            var record = new DownloadRecord
            {
                Url = uri.AbsoluteUri,
                Directory = string.IsNullOrWhiteSpace(directory) ? category.ResolveFolder(_settings.DownloadFolder) : directory,
                FileName = name,
                Status = startNow ? DownloadStatus.Paused : DownloadStatus.Queued,
                SortOrder = NextSortOrder(),
                AddedUtc = _clock.UtcNow,
                Context = context is { IsEmpty: false } ? context : null,
                CategoryId = category.Id,
                QueueId = _queues.FirstOrDefault(q => q.Id == queueId)?.Id ?? MainQueue.Id,
                ContentType = kind == "dash" ? "application/dash+xml" : "application/vnd.apple.mpegurl",
                Media = new MediaSource
                {
                    Kind = kind,
                    VariantId = request.VariantId,
                    Label = label,
                    AudioId = string.IsNullOrEmpty(request.AudioId) ? null : request.AudioId,
                    SubtitleIds = request.SubtitleIds is { Count: > 0 } subtitles ? [.. subtitles.Distinct()] : null,
                    SubtitleMode = request.SubtitleIds is { Count: > 0 } ? request.SubtitleMode ?? "vtt" : null,
                },
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

    /// <summary>Copies of the downloads for an export: without their request context (cookies, form data, login).</summary>
    public IReadOnlyList<DownloadRecord> ExportRecords()
    {
        lock (_lock)
        {
            return _items.OrderBy(i => i.SortOrder).Select(i =>
            {
                var copy = i.Clone();
                copy.Context = null;
                return copy;
            }).ToList();
        }
    }

    /// <summary>
    /// A download from an imported list: an unfinished one waits paused (nothing starts by itself), a completed one
    /// is kept as history. Its category and queue are found by name; the file name is sanitized.
    /// </summary>
    public long AddImported(Portable.ExportedDownload d)
    {
        if (!Urls.TryParseDownload(d.Url, out var parsed))
            throw new ArgumentException("Invalid address.", nameof(d));
        var uri = Urls.SplitLogin(parsed).Address;
        var name = string.IsNullOrWhiteSpace(d.FileName) ? null : FileNames.Sanitize(d.FileName);
        PostCheck? check = null;
        if (d.ExpectedHash is { } expected && FileHash.TryParse(expected, out var algorithm, out var hex))
            check = new PostCheck { HashAlgorithm = algorithm, ExpectedHash = hex };
        lock (_lock)
        {
            var category = _categories.FirstOrDefault(c => string.Equals(c.Name, d.Category, StringComparison.CurrentCultureIgnoreCase))
                           ?? Core.Categories.MatchUrl(_categories, uri.AbsoluteUri, name);
            // An imported file is untrusted: its entry never points at a file on this PC ("remove with the file" or the move
            // off the Desktop would then act on a file the app never downloaded). It is paused, in the category's folder.
            var record = new DownloadRecord
            {
                Url = uri.AbsoluteUri,
                Directory = category.ResolveFolder(_settings.DownloadFolder),
                FileName = name,
                Status = DownloadStatus.Paused,
                SortOrder = NextSortOrder(),
                AddedUtc = d.AddedUtc == default ? _clock.UtcNow : DateTime.SpecifyKind(d.AddedUtc, DateTimeKind.Utc),
                CategoryId = category.Id,
                QueueId = _queues.FirstOrDefault(q => string.Equals(q.Name, d.Queue, StringComparison.CurrentCultureIgnoreCase))?.Id ?? MainQueue.Id,
                Check = check,
                Media = d.Media is { } m ? new MediaSource
                {
                    Kind = m.Kind, VariantId = m.VariantId, Label = m.Label, AudioId = m.AudioId,
                    SubtitleIds = m.SubtitleIds?.Take(Integration.MessageValidator.MaxSubtitles).ToList(), SubtitleMode = m.SubtitleMode,
                } : null,
            };
            _store.Insert(record);
            _items.Add(record);
            return record.Id;
        }
    }

    /// <summary>
    /// Moves the folders of a <see cref="Setup.RelocationPlan"/> (settings and categories), and with
    /// <paramref name="moveFiles"/> the finished files listed in it. Files that cannot be moved stay where they are and
    /// are listed; a name already taken gets " (n)". Unfinished downloads keep their folder until they finish.
    /// </summary>
    public Setup.RelocationResult ApplyRelocation(Setup.RelocationPlan plan, bool moveFiles)
    {
        if (plan.DownloadFolder is { } folder)
        {
            var settings = Settings;
            settings.DownloadFolder = folder;
            UpdateSettings(settings);
        }
        foreach (var move in plan.Folders.Where(f => f.CategoryId is not null))
        {
            if (Categories.FirstOrDefault(c => c.Id == move.CategoryId) is { } category)
            {
                category.Folder = move.To;
                SaveCategory(category);
            }
        }
        var moved = 0;
        var problems = new List<string>();
        if (!moveFiles)
            return new Setup.RelocationResult(0, problems);
        foreach (var file in plan.Files)
        {
            lock (_lock)
            {
                if (Find(file.DownloadId) is not { Status: DownloadStatus.Completed } || _active.ContainsKey(file.DownloadId))
                    continue;
            }
            // Outside the lock: across drives a move is a copy, which must not hold up the running downloads.
            string target;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.To)!);
                target = FileNames.MakeUnique(file.To);
                File.Move(file.From, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file.From)}: {StorageErrors.Describe(ex)}");
                continue;
            }
            lock (_lock)
            {
                if (Find(file.DownloadId) is { } record)
                {
                    record.Directory = Path.GetDirectoryName(target)!;
                    record.FileName = Path.GetFileName(target);
                    _store.Update(record);
                }
            }
            moved++;
        }
        Log.Info("relocation", $"moved {moved} files away from the Desktop, {problems.Count} left");
        return new Setup.RelocationResult(moved, problems);
    }

    /// <summary>A client with the request's cookies (owned by the caller), or the shared one.</summary>
    private (HttpClient Client, bool Owned) ClientFor(RequestContext? context)
    {
        if (context is not { Cookies.Count: > 0 })
            return (_http, false);
        var cookies = RequestContexts.ToCookieContainer(context.Cookies);
        return (_fixedHttp is not null ? CreateHttpClient(cookies) : _network.CreateHttpClient(cookies), true);
    }

    /// <summary>Starts (or resumes) a download right away, outside the queue limits and quotas.</summary>
    public void Start(long id)
    {
        lock (_lock)
        {
            if (Find(id) is { Status: not DownloadStatus.Completed } record)
            {
                record.Failures = 0;
                record.RetryAtUtc = null;
                StartLocked(record, fromQueue: false);
            }
        }
    }

    /// <summary>Puts a download back in its queue to be started in order.</summary>
    public void Enqueue(long id)
    {
        lock (_lock)
        {
            if (Find(id) is { Status: DownloadStatus.Paused or DownloadStatus.Failed } record)
            {
                record.Status = DownloadStatus.Queued;
                record.Error = null;
                record.Failures = 0;
                record.RetryAtUtc = null;
                _store.Update(record);
                Pump();
            }
        }
    }

    public Task PauseAsync(long id) => StopAsync(a => a.Job.Record.Id == id, _ => DownloadStatus.Paused, alsoUnqueue: id);

    public Task PauseAllAsync() => StopAsync(_ => true, _ => DownloadStatus.Paused);

    /// <summary>Starts the main queue.</summary>
    public void StartQueue() => StartQueue(MainQueueId);

    /// <summary>Stops the main queue; downloads it started go back to waiting.</summary>
    public Task StopQueueAsync() => StopQueueAsync(MainQueueId);

    public void StartQueue(long queueId)
    {
        lock (_lock)
        {
            if (_queues.FirstOrDefault(q => q.Id == queueId) is not { } queue)
                return;
            SetRunning(queue, true);
            Pump();
        }
        QueuesChanged?.Invoke();
    }

    /// <summary>Stops a queue; the downloads it started go back to waiting in it.</summary>
    public async Task StopQueueAsync(long queueId)
    {
        Task task;
        lock (_lock)
        {
            if (_queues.FirstOrDefault(q => q.Id == queueId) is not { } queue)
                return;
            task = StopQueueLocked(queue);
        }
        QueuesChanged?.Invoke();
        await task;
    }

    /// <summary>Where the files of downloads deleted "from the disk" go; the app sets the Windows one. Never deleted for good.</summary>
    public IRecycleBin RecycleBin { get; set; } = new NoRecycleBin();

    /// <summary>
    /// Stops the download if it runs and removes it from the list; its temporary data (.part, segments) is cleaned.
    /// With <paramref name="deleteFile"/> the finished file goes to the Recycle Bin. False when that file stayed on the
    /// disk (no Recycle Bin there, or in use): the download is removed from the list all the same.
    /// </summary>
    public async Task<bool> RemoveAsync(long id, bool deleteFile)
    {
        await PauseAsync(id);
        string? recycle = null;
        lock (_lock)
        {
            if (Find(id) is not { } record)
                return true;
            _items.Remove(record);
            _store.Delete(id);
            TryDelete(record.PartPath);
            if (record.Media is not null && record.Status != DownloadStatus.Completed)
                TryDeleteFolder(MediaJob.PartsFolderOf(record));
            if (deleteFile && record.Status == DownloadStatus.Completed && record.FinalPath is { } path && File.Exists(path))
                recycle = path;
        }
        // Outside the lock and off the caller's thread: the shell can take a moment, or ask before deleting for good.
        return recycle is null || await Task.Run(() => RecycleBin.Recycle(recycle));
    }

    /// <summary>Moves a download up (negative) or down (positive) among the downloads of its own queue.</summary>
    public void Move(long id, int delta)
    {
        lock (_lock)
        {
            if (Find(id) is not { } item)
                return;
            if (_items.Select(i => i.SortOrder).Distinct().Count() != _items.Count)
                Renumber();
            var ordered = _items.Where(i => i.QueueId == item.QueueId).OrderBy(i => i.SortOrder).ToList();
            var index = ordered.IndexOf(item);
            var target = index + Math.Sign(delta);
            if (target < 0 || target >= ordered.Count)
                return;
            var other = ordered[target];
            (item.SortOrder, other.SortOrder) = (other.SortOrder, item.SortOrder);
            _store.Update(item);
            _store.Update(other);
            Pump();
        }
    }

    /// <summary>Moves downloads to another queue, at its end, in their current order.</summary>
    public void MoveToQueue(IEnumerable<long> ids, long queueId)
    {
        lock (_lock)
        {
            if (_queues.All(q => q.Id != queueId))
                return;
            foreach (var record in ids.Select(Find).OfType<DownloadRecord>().Where(r => r.QueueId != queueId).OrderBy(r => r.SortOrder).ToList())
            {
                record.QueueId = queueId;
                record.SortOrder = NextSortOrder();
                if (_active.TryGetValue(record.Id, out var active))
                    active.Job.Record.QueueId = queueId;
                _store.Update(record);
            }
            Pump();
        }
        QueuesChanged?.Invoke();
    }

    public void UpdateSettings(AppSettings settings)
    {
        lock (_lock)
        {
            var queueRunning = _settings.QueueRunning;
            _settings = settings.Clone();
            _settings.MaxConnections = Math.Clamp(settings.MaxConnections, 1, 32);
            _settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 50);
            _settings.SpeedLimitKBps = Math.Max(0, settings.SpeedLimitKBps);
            _settings.GlobalQuotaMBPerHour = Math.Max(0, settings.GlobalQuotaMBPerHour);
            _settings.TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 5, 600);
            _settings.MaxConnectionsPerServer = Math.Clamp(settings.MaxConnectionsPerServer, 1, 256);
            _settings.ProxyPort = Math.Clamp(settings.ProxyPort, 1, 65535);
            _settings.QueueRunning = queueRunning;
            _limiter.BytesPerSecond = _settings.SpeedLimitKBps * 1024;
            _store.SaveSettings(_settings);
            RebuildNetworkLocked();
            Pump();
        }
    }

    /// <summary>
    /// New downloads use the new network settings; running ones finish on the connections they have. The old client
    /// is not disposed here because running transfers may still be using it.
    /// </summary>
    private void RebuildNetworkLocked()
    {
        _network = new NetworkContext(_settings, Credentials, Certificates, _sessionProxyLogin);
        if (_fixedHttp is null)
            _http = _network.SharedHttp;
        _ = _network.Proxy.LoadPacAsync();
    }

    /// <summary>
    /// The user's answer to <see cref="LoginRequired"/>: saved (encrypted) when <paramref name="remember"/>, otherwise
    /// kept until the app closes. The paused download then resumes.
    /// </summary>
    public void ProvideLogin(LoginRequest request, string username, string password, bool remember)
    {
        if (request.IsProxy)
        {
            lock (_lock)
            {
                if (remember)
                {
                    _settings.ProxyUser = username;
                    _settings.ProxyPassword = password;
                    _store.SaveSettings(_settings);
                    _sessionProxyLogin = null;
                }
                else
                {
                    _sessionProxyLogin = new NetworkCredential(username, password);
                }
                RebuildNetworkLocked();
            }
        }
        else
        {
            var port = FtpTransport.IsFtp(request.Target) ? FtpTransport.PortOf(request.Target) : request.Target.Port;
            // Typed in the login window, which made the user confirm a plain-text password before accepting it.
            Credentials.Save(new SiteCredential
            {
                Scheme = request.Target.Scheme, Host = request.Target.Host, Port = port, Username = username, Password = password,
                PlainConfirmed = request.SendsPasswordInClear,
            }, remember);
        }
        Start(request.DownloadId);
    }

    /// <summary>Changes only the global speed limit, for example from the notification area menu.</summary>
    public void SetSpeedLimit(long kilobytesPerSecond)
    {
        var settings = Settings;
        settings.SpeedLimitKBps = kilobytesPerSecond;
        UpdateSettings(settings);
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

    /// <summary>Adds a queue at the end (Id 0) or saves changes to an existing one, keeping its started/stopped state.</summary>
    public DownloadQueue SaveQueue(DownloadQueue queue)
    {
        if (string.IsNullOrWhiteSpace(queue.Name))
            throw new ArgumentException("A queue needs a name.", nameof(queue));
        DownloadQueue result;
        lock (_lock)
        {
            var saved = queue.Clone();
            saved.Name = saved.Name.Trim();
            saved.MaxConcurrent = Math.Clamp(saved.MaxConcurrent, 1, 20);
            saved.RetryCount = Math.Clamp(saved.RetryCount, 0, 100);
            saved.RetryDelaySeconds = Math.Clamp(saved.RetryDelaySeconds, 0, 24 * 3600);
            saved.QuotaMBPerHour = Math.Max(0, saved.QuotaMBPerHour);
            saved.Schedule.RepeatMinutes = Math.Max(0, saved.Schedule.RepeatMinutes);
            var existing = _queues.FirstOrDefault(q => q.Id == saved.Id);
            if (existing is null)
            {
                saved.Id = 0;
                saved.Key = null;
                saved.Running = false;
                saved.SortOrder = _queues.Max(q => q.SortOrder) + 1;
                _store.SaveQueue(saved);
                _queues.Add(saved);
            }
            else
            {
                saved.Key = existing.Key;
                saved.SortOrder = existing.SortOrder;
                saved.Running = existing.Running;
                _store.SaveQueue(saved);
                _queues[_queues.IndexOf(existing)] = saved;
                _tracker.Forget(saved.Id); // a changed schedule is evaluated afresh
            }
            result = saved.Clone();
            Pump();
        }
        QueuesChanged?.Invoke();
        return result;
    }

    /// <summary>Removes a queue; its downloads move to the main queue, which cannot be removed.</summary>
    public void DeleteQueue(long id)
    {
        lock (_lock)
        {
            if (_queues.FirstOrDefault(q => q.Id == id) is not { IsMain: false } queue)
                return;
            var main = MainQueue;
            _store.DeleteQueue(id, main.Id);
            _queues.Remove(queue);
            _tracker.Forget(id);
            _runStarted.Remove(id);
            _quotas.Remove(id);
            foreach (var record in _items.Where(r => r.QueueId == id))
                record.QueueId = main.Id;
            foreach (var active in _active.Values.Where(a => a.Job.Record.QueueId == id))
                active.Job.Record.QueueId = main.Id;
            Pump();
        }
        QueuesChanged?.Invoke();
    }

    /// <summary>Bytes transferred in the last hour by a queue (or by all downloads when null).</summary>
    public long QuotaUsed(long? queueId)
    {
        lock (_lock)
        {
            var now = _clock.UtcNow;
            return queueId is { } id ? (_quotas.TryGetValue(id, out var quota) ? quota.Used(now) : 0) : _globalQuota.Used(now);
        }
    }

    /// <summary>
    /// Asks the server at <paramref name="newUrl"/> about the file and compares it with what was already
    /// downloaded, typically because the old link expired.
    /// </summary>
    public async Task<UrlCheck> CheckNewUrlAsync(long id, string newUrl, CancellationToken ct = default)
    {
        if (!Urls.TryParseDownload(newUrl, out var parsed))
            throw new ArgumentException(Strings.ErrorInvalidUrl);
        lock (_lock)
        {
            if (Find(id) is { Media: not null })
                throw new ArgumentException(Strings.MediaCannotChangeUrl, nameof(newUrl));
        }
        DownloadRecord record;
        NetworkContext network;
        lock (_lock)
        {
            var stored = Find(id) ?? throw new InvalidOperationException(Strings.ErrorDownloadGone);
            record = _active.TryGetValue(id, out var active) ? active.Job.Record.Clone() : stored.Clone();
            record.Status = stored.Status;
            network = _network;
        }

        // The new address is asked with the same network, cookies and login a download would use.
        var (uri, user, password) = Urls.SplitLogin(parsed);
        var probeRecord = record.Clone();
        probeRecord.Url = uri.AbsoluteUri;
        if (user is not null)
            probeRecord.Context = (record.Context ?? new RequestContext(null, null, [])) with { Username = user, Password = password };
        // On a worker thread: an FTPS certificate question waits for the UI, which must not be the waiting thread.
        var probe = await Task.Run(() => network.ProbeAsync(probeRecord, ct), ct);
        return UrlCheck.Compare(record, parsed.AbsoluteUri, probe);
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
            var (address, user, password) = Urls.SplitLogin(new Uri(check.NewUrl));
            record.Url = address.AbsoluteUri;
            if (user is not null)
                record.Context = (record.Context ?? new RequestContext(null, null, [])) with { Username = user, Password = password };
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

    /// <summary>
    /// One step of the scheduler, run every second (or by tests with a fake clock): counts transferred bytes for
    /// the quotas, starts and stops queues by their schedule, enforces quotas, restarts downloads whose retry time
    /// came, and reports queues that finished.
    /// </summary>
    public void Tick()
    {
        var finished = new List<DownloadQueue>();
        var queuesChanged = false;
        lock (_lock)
        {
            if (_pumpSuspended)
                return;
            var now = _clock.UtcNow;
            foreach (var active in _active.Values)
                CountBytes(active, now);

            foreach (var queue in _queues.ToList())
            {
                switch (_tracker.Evaluate(queue, now, _clock.Zone))
                {
                    case ScheduleTracker.Change.Start:
                        // Each scheduled start also gives the downloads that failed another chance.
                        foreach (var failed in _items.Where(i => i.QueueId == queue.Id && i.Status == DownloadStatus.Failed))
                        {
                            failed.Status = DownloadStatus.Queued;
                            failed.Failures = 0;
                            failed.RetryAtUtc = null;
                            _store.Update(failed);
                        }
                        if (!queue.Running)
                            SetRunning(queue, true);
                        queuesChanged = true;
                        break;
                    case ScheduleTracker.Change.Resume:
                        if (!queue.Running)
                        {
                            SetRunning(queue, true);
                            queuesChanged = true;
                        }
                        break;
                    case ScheduleTracker.Change.Stop:
                        if (queue.Running)
                        {
                            _ = StopQueueLocked(queue);
                            queuesChanged = true;
                        }
                        break;
                }
            }

            // Queues over their hourly quota hand their downloads back until the past hour frees up.
            foreach (var active in _active.Values.Where(a => a.FromQueue && QuotaBlocked(a.Job.Record.QueueId, now)).ToList())
            {
                active.StopStatus = DownloadStatus.Queued;
                active.Cts.Cancel();
            }

            Pump();
            finished.AddRange(CollectFinished());
            queuesChanged |= finished.Count > 0;
        }
        if (queuesChanged)
            QueuesChanged?.Invoke();
        foreach (var queue in finished)
            QueueFinished?.Invoke(queue);
    }

    /// <summary>
    /// Undoes <see cref="ShutdownAsync"/> when the app keeps running after all, for example when Windows or another
    /// program cancelled the shutdown: queues start downloads again.
    /// </summary>
    public void ResumeAfterShutdown()
    {
        lock (_lock)
        {
            _pumpSuspended = false;
            Pump();
        }
    }

    /// <summary>Checkpoints and stops every running download so it can resume on the next launch.</summary>
    public Task ShutdownAsync() =>
        StopAsync(_ => true, a => a.FromQueue ? DownloadStatus.Queued : DownloadStatus.Paused, pumpAfter: false);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _tickerCts.Cancel();
        await _ticker.ContinueWith(_ => { }, TaskScheduler.Default);
        await ShutdownAsync();
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                Tick();
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // A failed step is retried on the next tick.
            }
        }
    }

    private void SetRunning(DownloadQueue queue, bool running)
    {
        queue.Running = running;
        if (!running)
            _runStarted.Remove(queue.Id);
        _store.SaveQueue(queue);
    }

    private Task StopQueueLocked(DownloadQueue queue)
    {
        SetRunning(queue, false);
        var targets = _active.Values.Where(a => a.FromQueue && a.Job.Record.QueueId == queue.Id).ToList();
        foreach (var a in targets)
        {
            a.StopStatus = DownloadStatus.Queued;
            a.Cts.Cancel();
        }
        return Task.WhenAll(targets.Select(a => a.Task));
    }

    private bool QuotaBlocked(long? queueId, DateTime now)
    {
        if (HourlyQuota.IsExceeded(_globalQuota.Used(now), _settings.GlobalQuotaMBPerHour))
            return true;
        var queue = _queues.FirstOrDefault(q => q.Id == queueId);
        return queue is not null && _quotas.TryGetValue(queue.Id, out var quota) && HourlyQuota.IsExceeded(quota.Used(now), queue.QuotaMBPerHour);
    }

    private DateTime? QuotaFreeAt(long? queueId, DateTime now)
    {
        var global = _globalQuota.FreeAt(_settings.GlobalQuotaMBPerHour, now);
        var queue = _queues.FirstOrDefault(q => q.Id == queueId);
        var own = queue is not null && _quotas.TryGetValue(queue.Id, out var quota) ? quota.FreeAt(queue.QuotaMBPerHour, now) : null;
        return global is null ? own : own is null ? global : global > own ? global : own;
    }

    private void CountBytes(ActiveJob active, DateTime now)
    {
        var total = active.Job.BytesDownloaded;
        var delta = total - active.CountedBytes;
        active.CountedBytes = total;
        if (delta <= 0)
            return; // nothing new, or the transfer restarted from zero
        _globalQuota.Add(delta, now);
        if (active.Job.Record.QueueId is { } queueId)
        {
            if (!_quotas.TryGetValue(queueId, out var quota))
                _quotas[queueId] = quota = new HourlyQuota();
            quota.Add(delta, now);
        }
    }

    /// <summary>Queues working in runs that have nothing left: they stop, and the caller raises <see cref="QueueFinished"/>.</summary>
    private List<DownloadQueue> CollectFinished()
    {
        var finished = new List<DownloadQueue>();
        foreach (var queue in _queues.Where(q => q.Running && q.StopsWhenDone && _runStarted.Contains(q.Id)).ToList())
        {
            // Checks still running count too: the end action (e.g. shut down) must not cut a scan short.
            var busy = _active.Values.Any(a => a.FromQueue && a.Job.Record.QueueId == queue.Id)
                       || _items.Any(i => i.QueueId == queue.Id && (i.Status == DownloadStatus.Queued || i.Check is { Pending: true }));
            if (busy)
                continue;
            SetRunning(queue, false);
            finished.Add(queue.Clone());
        }
        return finished;
    }

    private DownloadSnapshot SnapshotLocked(DownloadRecord r)
    {
        _active.TryGetValue(r.Id, out var active);
        var live = active?.Job.Record;
        var name = live?.FileName ?? r.FileName ?? DisplayNameFromUrl(r.Url);
        // A finished stream saved without ffmpeg says what was saved instead of a single MP4.
        var error = r.Status == DownloadStatus.Completed ? r.Media?.Note ?? r.Error : r.Error;
        // A waiting download says why it does not start.
        if (r.Status == DownloadStatus.Queued && active is null)
        {
            var now = _clock.UtcNow;
            if (r.RetryAtUtc is { } retry && retry > now)
                error = string.Format(CultureInfo.CurrentCulture, Strings.WaitingRetry, r.Failures, LocalTime(retry), r.Error);
            else if (QuotaBlocked(r.QueueId, now) && QuotaFreeAt(r.QueueId, now) is { } free)
                error = string.Format(CultureInfo.CurrentCulture, Strings.WaitingQuota, LocalTime(free));
        }
        return new DownloadSnapshot(
            r.Id, r.Url, name, live?.Directory ?? r.Directory,
            r.Status == DownloadStatus.Completed ? r.FinalPath : null,
            live?.TotalSize ?? r.TotalSize,
            active?.Job.BytesDownloaded ?? r.DownloadedBytes,
            r.Status, error,
            active?.Job.ActiveConnections ?? 0,
            r.SortOrder,
            live?.SupportsRanges ?? r.SupportsRanges,
            r.AddedUtc,
            live?.CategoryId ?? r.CategoryId,
            r.CompletedUtc,
            live?.ETag ?? r.ETag,
            live?.LastModified ?? r.LastModified,
            live?.ContentType ?? r.ContentType,
            r.QueueId,
            r.RetryAtUtc,
            r.Check?.Clone());
    }

    private string LocalTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, _clock.Zone).ToString("t", CultureInfo.CurrentCulture);

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
                waiting.RetryAtUtc = null;
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
        record.RetryAtUtc = null;
        _store.Update(record);
        if (fromQueue && record.QueueId is { } queueId)
            _runStarted.Add(queueId);

        var options = new EngineOptions
        {
            MaxConnections = _settings.MaxConnections,
            MinSegmentSize = _engineTemplate.MinSegmentSize,
            BufferSize = _engineTemplate.BufferSize,
            ReadTimeout = _customEngine ? _engineTemplate.ReadTimeout : _network.Timeout,
            MaxRetries = _engineTemplate.MaxRetries,
            RetryBaseDelay = _engineTemplate.RetryBaseDelay,
            CheckpointInterval = _engineTemplate.CheckpointInterval,
        };
        IDownloadJob job = record.Media is not null
            ? new MediaJob(record.Clone(), _http, _limiter, options, OnCheckpoint, _network, () => Ffmpeg.Find(Settings.FfmpegPath))
            : new DownloadJob(record.Clone(), _http, _limiter, options, OnCheckpoint, OnProbed, _network);
        var active = new ActiveJob(job, fromQueue) { CountedBytes = job.BytesDownloaded };
        _active[record.Id] = active;
        active.Task = Task.Run(() => RunJobAsync(active));
    }

    private async Task RunJobAsync(ActiveJob active)
    {
        DownloadStatus final;
        string? error = null;
        LoginRequest? login = null;
        try
        {
            await active.Job.RunAsync(active.Cts.Token);
            final = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (active.Cts.IsCancellationRequested)
        {
            final = active.StopStatus;
        }
        catch (AuthenticationRequiredException ex)
        {
            // Paused, not failed: it continues as soon as the user gives the login.
            final = DownloadStatus.Paused;
            error = string.Format(CultureInfo.CurrentCulture, ex.IsProxy ? Strings.WaitingProxyLogin : Strings.WaitingLogin, ex.Target.Host);
            login = new LoginRequest(active.Job.Record.Id, ex.IsProxy ? ProxyAddress(ex.Target) : ex.Target, ex.IsProxy, ex.Scheme, ex.Realm,
                active.Job.Record.FileName ?? FileNames.FromUrl(new Uri(active.Job.Record.Url)));
        }
        catch (UntrustedCertificateException ex)
        {
            final = DownloadStatus.Failed;
            error = string.Format(CultureInfo.CurrentCulture, Strings.CertificateRejected, ex.Host);
        }
        catch (Exception ex)
        {
            final = DownloadStatus.Failed;
            // A full disk, a folder that cannot be written or a path too long is said in words; the data on disk is kept.
            error = StorageErrors.Describe(ex, StorageErrors.IsPermanent(ex) ? active.Job.Record.Directory : null);
        }
        Log.Info("download", $"{active.Job.Record.Id} {final} {Log.Url(active.Job.Record.Url)}{(error is null ? "" : " - " + error)}");

        DownloadSnapshot? completed = null;
        long? checks = null;
        List<DownloadQueue> finished;
        lock (_lock)
        {
            var now = _clock.UtcNow;
            CountBytes(active, now);
            _active.Remove(active.Job.Record.Id);
            if (Find(active.Job.Record.Id) is { } record)
            {
                CopyProgress(active.Job.Record, record);
                record.Status = final;
                record.Error = error;
                var queue = _queues.FirstOrDefault(q => q.Id == record.QueueId);
                if (final == DownloadStatus.Completed)
                {
                    record.Context = null; // session cookies are not kept longer than needed
                    record.Failures = 0;
                    if (NeedsChecks(record))
                    {
                        // Announced as completed once the checks have an outcome.
                        record.Check ??= new PostCheck();
                        record.Check.Pending = true;
                        checks = record.Id;
                    }
                    else
                    {
                        completed = SnapshotLocked(record);
                    }
                }
                else if (final == DownloadStatus.Failed && active.FromQueue && queue is not null && record.Failures < queue.RetryCount)
                {
                    // The queue tries again later instead of giving up.
                    record.Failures++;
                    record.Status = DownloadStatus.Queued;
                    record.RetryAtUtc = now.AddSeconds(queue.RetryDelaySeconds);
                }
                _store.Update(record);
            }
            Pump();
            finished = CollectFinished();
        }
        active.Cts.Dispose();
        if (checks is { } id)
            _ = RunChecksAsync(id, announce: true);
        if (login is not null)
            LoginRequired?.Invoke(login);
        if (completed is not null)
            DownloadCompleted?.Invoke(completed);
        if (finished.Count > 0)
            QueuesChanged?.Invoke();
        foreach (var queue in finished)
            QueueFinished?.Invoke(queue);
    }

    private bool NeedsChecks(DownloadRecord record) =>
        record.Check?.ExpectedHash is not null || _settings.ComputeHashOnCompletion || _settings.ScanOnCompletion;

    /// <summary>
    /// Hash and antivirus scan of a finished file, on a worker thread, one file at a time. Nothing here runs the file:
    /// it is read for the hash and handed as an argument to the scanner.
    /// </summary>
    /// <param name="announce">Raise <see cref="DownloadCompleted"/> (the completion of a download), not only <see cref="CheckCompleted"/>.</param>
    private async Task RunChecksAsync(long id, bool announce)
    {
        try
        {
            await _checkGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            string? path;
            PostCheck check;
            PostCheck? started;
            AppSettings settings;
            lock (_lock)
            {
                if (Find(id) is not { Status: DownloadStatus.Completed } record)
                    return;
                path = record.FinalPath;
                started = record.Check;
                check = record.Check?.Clone() ?? new PostCheck();
                settings = _settings.Clone();
            }

            try
            {
                await RunChecksOnFileAsync(id, path, check, settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never left "checking" forever: the outcome says the check could not be completed.
                Log.Error("checks", $"download {id}", ex);
                if (settings.ScanOnCompletion)
                {
                    check.Scan ??= ScanOutcome.Failed;
                    check.ScanDetail ??= ex.Message;
                }
            }

            DownloadSnapshot snapshot;
            List<DownloadQueue> finished;
            lock (_lock)
            {
                if (Find(id) is not { } record)
                    return;
                // What changed while the checks ran is kept: an expected hash set in Properties (its own run is queued
                // and the download stays pending until then), or a hash computed for "Copy hash".
                var current = record.Check;
                var merged = current?.Clone() ?? new PostCheck();
                if (merged.ComputedHash is null && (merged.HashAlgorithm is null || merged.HashAlgorithm == check.HashAlgorithm))
                {
                    merged.HashAlgorithm ??= check.HashAlgorithm;
                    merged.ComputedHash = check.ComputedHash;
                }
                merged.Scan ??= check.Scan;
                merged.ScanDetail ??= check.ScanDetail;
                merged.Pending = !ReferenceEquals(current, started) && merged.Pending;
                record.Check = merged;
                _store.Update(record);
                snapshot = SnapshotLocked(record);
                finished = CollectFinished();
            }
            CheckCompleted?.Invoke(snapshot);
            if (announce)
                DownloadCompleted?.Invoke(snapshot);
            if (finished.Count > 0)
                QueuesChanged?.Invoke();
            foreach (var queue in finished)
                QueueFinished?.Invoke(queue);
        }
        catch (OperationCanceledException)
        {
            // The app is closing: the checks run again on the next start.
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // The outcome is in memory (and shown) but could not be saved: the checks run again on the next start.
            Log.Error("checks", $"download {id}: not saved", ex);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>Fills <paramref name="check"/> with the hash and the scan of the file, as configured.</summary>
    private async Task RunChecksOnFileAsync(long id, string? path, PostCheck check, AppSettings settings)
    {
        if (path is null || !File.Exists(path))
            return;
        var algorithm = check.HashAlgorithm ?? (settings.ComputeHashOnCompletion ? settings.HashAlgorithm : null);
        if (algorithm is not null && check.ComputedHash is null)
        {
            check.HashAlgorithm = algorithm;
            // A file just written can be locked for a moment (an antivirus scanning it): a few tries before giving up.
            for (var attempt = 1; check.ComputedHash is null; attempt++)
            {
                try
                {
                    check.ComputedHash = await FileHash.ComputeAsync(path, algorithm, _lifetime.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning("hash", $"download {id}, attempt {attempt}: {StorageErrors.Describe(ex)}");
                    if (attempt == 5)
                        break;
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), _lifetime.Token);
                }
            }
        }
        if (settings.ScanOnCompletion && check.Scan is null)
        {
            var (outcome, detail) = await VirusScanner.ScanAsync(VirusScanner.ResolveScanner(settings.ScannerPath), settings.ScannerArguments,
                settings.ScanThreatCodes, path, TimeSpan.FromSeconds(Math.Clamp(settings.ScanTimeoutSeconds, 10, 3600)), _lifetime.Token);
            check.Scan = outcome;
            check.ScanDetail = detail;
            Log.Info("scan", $"download {id}: {outcome}");
        }
    }

    /// <summary>
    /// Sets (or clears) the hash a finished or future download must have; a finished file is checked right away.
    /// </summary>
    public void SetExpectedHash(long id, string? text)
    {
        PostCheck? check = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!FileHash.TryParse(text, out var algorithm, out var hex))
                throw new ArgumentException(Strings.ErrorHashFormat, nameof(text));
            check = new PostCheck { HashAlgorithm = algorithm, ExpectedHash = hex };
        }
        bool run;
        lock (_lock)
        {
            if (Find(id) is not { } record)
                return;
            var previous = record.Check;
            check ??= new PostCheck();
            check.Scan = previous?.Scan;
            check.ScanDetail = previous?.ScanDetail;
            // A hash computed with the same algorithm stays valid.
            if (previous?.ComputedHash is { } computed && previous.HashAlgorithm == check.HashAlgorithm)
                check.ComputedHash = computed;
            run = record.Status == DownloadStatus.Completed && check.ExpectedHash is not null && check.ComputedHash is null;
            check.Pending = run;
            record.Check = check.ExpectedHash is null && check.ComputedHash is null && check.Scan is null ? null : check;
            if (_active.TryGetValue(id, out var active))
                active.Job.Record.Check = record.Check?.Clone();
            _store.Update(record);
        }
        if (run)
            _ = RunChecksAsync(id, announce: false); // an old download: no new "completed" notification
    }

    /// <summary>Computes the hash of a finished file now (for "Copy hash" in Properties) and keeps it with the download.</summary>
    public async Task<string?> ComputeHashAsync(long id, string algorithm, CancellationToken ct = default)
    {
        string? path;
        lock (_lock)
            path = Find(id) is { Status: DownloadStatus.Completed } record ? record.FinalPath : null;
        if (path is null || !File.Exists(path))
            return null;
        var hex = await Task.Run(() => FileHash.ComputeAsync(path, algorithm, ct), ct);
        DownloadSnapshot? snapshot = null;
        lock (_lock)
        {
            if (Find(id) is { } record)
            {
                record.Check ??= new PostCheck();
                if (record.Check.ExpectedHash is null || record.Check.HashAlgorithm == algorithm)
                {
                    record.Check.HashAlgorithm = algorithm;
                    record.Check.ComputedHash = hex;
                }
                _store.Update(record);
                snapshot = SnapshotLocked(record);
            }
        }
        if (snapshot is not null)
            CheckCompleted?.Invoke(snapshot);
        return hex;
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
                    // Never seen as completed without its checks: they are pending from this same instant.
                    if (NeedsChecks(record))
                    {
                        record.Check ??= new PostCheck();
                        record.Check.Pending = true;
                    }
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
        to.Media = from.Media?.Clone();
    }

    /// <summary>Starts waiting downloads, queue by queue in list order, within every limit and quota.</summary>
    private void Pump()
    {
        if (_pumpSuspended)
            return;
        var now = _clock.UtcNow;
        foreach (var queue in _queues.OrderBy(q => q.SortOrder).ThenBy(q => q.Id))
        {
            if (!queue.Running || QuotaBlocked(queue.Id, now))
                continue;
            while (_active.Values.Count(a => a.FromQueue) < _settings.MaxConcurrentDownloads
                   && _active.Values.Count(a => a.FromQueue && a.Job.Record.QueueId == queue.Id) < queue.MaxConcurrent)
            {
                var next = _items
                    .Where(i => i.QueueId == queue.Id && i.Status == DownloadStatus.Queued && !_active.ContainsKey(i.Id)
                                && (i.RetryAtUtc is not { } retry || retry <= now))
                    .OrderBy(i => i.SortOrder)
                    .FirstOrDefault();
                if (next is null)
                    break;
                StartLocked(next, fromQueue: true);
            }
        }
    }

    private int NextSortOrder() => _items.Count == 0 ? 0 : _items.Max(i => i.SortOrder) + 1;

    /// <summary>Gives every download a distinct position, keeping the current order.</summary>
    private void Renumber()
    {
        var ordered = _items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SortOrder != i)
            {
                ordered[i].SortOrder = i;
                _store.Update(ordered[i]);
            }
        }
    }

    private DownloadRecord? Find(long id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>The proxy that asked for a login (407), as the proxy settings would pick it for that address.</summary>
    private Uri ProxyAddress(Uri target)
    {
        lock (_lock)
            return _network.Proxy.Resolve(target) ?? target;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string DisplayNameFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? FileNames.Sanitize(FileNames.FromUrl(uri)) : url;

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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
