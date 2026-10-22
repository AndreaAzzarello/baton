using System.Globalization;
using System.Net;
using Baton.Core.Net;
using Baton.Core.Resources;
using Baton.Core.Security;

namespace Baton.Core.Grabber;

/// <summary>A grabber run in progress or finished: its live list of files, its progress, and a way to stop it.</summary>
public sealed class GrabberRun
{
    private readonly GrabberEngine _engine;
    private readonly CancellationTokenSource _cts = new();

    internal GrabberRun(GrabberProject project, GrabberEngine engine, HttpClient client, IReadOnlyDictionary<string, GrabberFileState> previous,
        bool automatic)
    {
        Project = project;
        _engine = engine;
        Client = client;
        Previous = previous;
        Automatic = automatic;
    }

    public GrabberProject Project { get; }

    /// <summary>Started by the schedule: files are sent to the queue without asking.</summary>
    public bool Automatic { get; }

    public IReadOnlyDictionary<string, GrabberFileState> Previous { get; }

    public IReadOnlyCollection<GrabberItem> Items => _engine.Items;

    /// <summary>Raised on a worker thread for each file found.</summary>
    public event Action<GrabberItem>? ItemFound
    {
        add => _engine.ItemFound += value;
        remove => _engine.ItemFound -= value;
    }

    public GrabberProgress Progress => Result is null ? _engine.Progress : _engine.Progress with { Finished = true, CurrentUrl = null };

    public GrabberRunResult? Result { get; private set; }

    /// <summary>Completes when the run and its bookkeeping are done (never faults).</summary>
    public Task<GrabberRunResult> Completion { get; internal set; } = null!;

    internal GrabberEngine Engine => _engine;

    internal HttpClient Client { get; }

    internal CancellationToken Token => _cts.Token;

    internal void Finish(GrabberRunResult result) => Result = result;

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Grabber projects: saved, run on request or by their schedule (the same schedule model and daylight-saving rules
/// as the queues), and the files they find sent to a queue. Remembers what each run saw, so the next one fetches
/// only what is new or changed and reports what the site removed (without deleting anything).
/// </summary>
public sealed class GrabberService : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly DownloadService _downloads;
    private readonly DownloadStore _store;
    private readonly IClock _clock;
    private readonly Func<GrabberProject, CookieContainer, HttpClient>? _clientFactory;
    private readonly GrabberEngineOptions? _engineOptions;
    private readonly List<GrabberProject> _projects;
    private readonly Dictionary<long, GrabberRun> _runs = [];
    private readonly Dictionary<long, (DateTime WindowStart, long Slot)?> _lastSlot = [];
    private readonly CancellationTokenSource _tickerCts = new();
    private readonly Task _ticker;

    /// <param name="clientFactory">Tests: the HTTP client of a run (it must not follow redirects by itself).</param>
    public GrabberService(DownloadService downloads, DownloadStore store, IClock? clock = null,
        Func<GrabberProject, CookieContainer, HttpClient>? clientFactory = null, GrabberEngineOptions? engineOptions = null, bool autoTick = true)
    {
        _downloads = downloads;
        _store = store;
        _clock = clock ?? SystemClock.Instance;
        _clientFactory = clientFactory;
        _engineOptions = engineOptions;
        _projects = store.LoadGrabberProjects();
        // A file sent to a queue counts as obtained once its download completes.
        _downloads.DownloadCompleted += snapshot => _store.MarkGrabberDownloadObtained(snapshot.Id);
        _ticker = autoTick ? TickLoopAsync(_tickerCts.Token) : Task.CompletedTask;
    }

    /// <summary>Raised when a run starts (by the user or by its schedule).</summary>
    public event Action<GrabberRun>? RunStarted;

    /// <summary>Raised on a worker thread when a run has finished and its results are saved.</summary>
    public event Action<GrabberRun>? RunFinished;

    /// <summary>Raised when projects are added, changed or removed, or a run starts or ends.</summary>
    public event Action? ProjectsChanged;

    public IReadOnlyList<GrabberProject> Projects
    {
        get { lock (_lock) return _projects.Select(p => p.Clone()).ToList(); }
    }

    public GrabberProject? Find(long id)
    {
        lock (_lock) return _projects.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    public GrabberRun? ActiveRun(long projectId)
    {
        lock (_lock) return _runs.TryGetValue(projectId, out var run) && run.Result is null ? run : null;
    }

    public GrabberRun? LastRun(long projectId)
    {
        lock (_lock) return _runs.GetValueOrDefault(projectId);
    }

    /// <summary>The next scheduled start of a project, if it has one.</summary>
    public DateTime? NextRun(GrabberProject project) => ScheduleCalculator.NextStart(project.Schedule, _clock.UtcNow, _clock.Zone);

    /// <summary>Adds a project (Id 0) or saves changes; the cookies it already has are kept unless new ones are given.</summary>
    public GrabberProject Save(GrabberProject project)
    {
        var saved = project.Clone();
        saved.Clamp();
        if (saved.Validate() is { } error)
            throw new ArgumentException($"Invalid grabber project ({error}).", nameof(project));
        lock (_lock)
        {
            var existing = _projects.FirstOrDefault(p => p.Id == saved.Id);
            if (existing is null)
            {
                saved.Id = 0;
                saved.AddedUtc = _clock.UtcNow;
                _store.SaveGrabberProject(saved);
                _projects.Add(saved);
            }
            else
            {
                saved.AddedUtc = existing.AddedUtc;
                saved.LastRunUtc = existing.LastRunUtc;
                saved.LastResult = existing.LastResult;
                _store.SaveGrabberProject(saved);
                _projects[_projects.IndexOf(existing)] = saved;
                _lastSlot.Remove(saved.Id); // a changed schedule is evaluated afresh
            }
        }
        ProjectsChanged?.Invoke();
        return saved.Clone();
    }

    /// <summary>Removes a project and its memory of the files; the files already saved stay where they are.</summary>
    public void Delete(long id)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(id, out var run))
                run.Cancel();
            _projects.RemoveAll(p => p.Id == id);
            _lastSlot.Remove(id);
            _store.DeleteGrabberProject(id);
        }
        ProjectsChanged?.Invoke();
    }

    /// <summary>What a project remembers of the files it has seen.</summary>
    public IReadOnlyDictionary<string, GrabberFileState> Files(long projectId) => _store.LoadGrabberFiles(projectId);

    /// <summary>Starts a run of a project, or returns the one already running.</summary>
    public GrabberRun Start(long projectId, bool automatic = false)
    {
        GrabberRun run;
        lock (_lock)
        {
            var project = _projects.FirstOrDefault(p => p.Id == projectId)?.Clone()
                          ?? throw new ArgumentException(Strings.ErrorProjectGone);
            if (_runs.TryGetValue(projectId, out var active) && active.Result is null)
                return active;
            var previous = Obtainable(projectId, _store.LoadGrabberFiles(projectId));
            var settings = _downloads.Settings;
            var client = CreateClient(project, settings);
            var engine = new GrabberEngine(project, client, previous,
                string.IsNullOrWhiteSpace(settings.UserAgent) ? AppSettings.DefaultUserAgent : settings.UserAgent, _engineOptions,
                _downloads.ConsumeBandwidthAsync);
            run = new GrabberRun(project, engine, client, previous, automatic);
            _runs[projectId] = run;
            run.Completion = Task.Run(() => RunAsync(run));
        }
        RunStarted?.Invoke(run);
        ProjectsChanged?.Invoke();
        return run;
    }

    /// <summary>
    /// What a run compares with: files sent to a queue count only once their download completed. One whose download
    /// failed or was removed is left out, so the run lists it as new again; one still waiting in its queue is kept,
    /// so it is not sent twice.
    /// </summary>
    private Dictionary<string, GrabberFileState> Obtainable(long projectId, Dictionary<string, GrabberFileState> files)
    {
        var result = new Dictionary<string, GrabberFileState>(StringComparer.Ordinal);
        var fixedUp = new List<GrabberFileState>();
        foreach (var (url, state) in files)
        {
            if (!state.Obtained)
            {
                var download = state.DownloadId is { } id ? _downloads.GetSnapshot(id) : null;
                if (download is null || download.Status == DownloadStatus.Failed)
                    continue;
                if (download.Status == DownloadStatus.Completed)
                {
                    state.Obtained = true; // completed while its state was being written
                    fixedUp.Add(state);
                }
            }
            result[url] = state;
        }
        if (fixedUp.Count > 0)
            _store.SaveGrabberFiles(projectId, fixedUp);
        return result;
    }

    private HttpClient CreateClient(GrabberProject project, AppSettings settings)
    {
        var cookies = project.UseCookies && project.Cookies.Count > 0 ? RequestContexts.ToCookieContainer(project.Cookies) : new CookieContainer();
        if (_clientFactory is not null)
            return _clientFactory(project, cookies);
        if (!project.UseProxy)
            settings.ProxyMode = ProxyMode.None;
        var network = new NetworkContext(settings, project.UseSavedCredentials ? _downloads.Credentials : new CredentialStore(null), _downloads.Certificates);
        _ = network.Proxy.LoadPacAsync();
        return network.CreateHttpClient(cookies, followRedirects: false);
    }

    private async Task<GrabberRunResult> RunAsync(GrabberRun run)
    {
        GrabberRunResult result;
        try
        {
            result = await run.Engine.RunAsync(run.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unexpected failure ends the run; what was found so far is kept.
            result = new GrabberRunResult(0, 0, 0, 0, 0, 0, 1, 0, ex.Message, false, null);
        }
        finally
        {
            run.Client.Dispose();
        }

        try
        {
            Record(run, result);
            if (run.Automatic && run.Project.Mode == GrabberMode.Files && !result.Cancelled)
            {
                var wanted = run.Items.Where(i => !i.IsPage && i.Status is GrabberItemStatus.New or GrabberItemStatus.Changed).ToList();
                if (wanted.Count > 0)
                    Send(run, wanted, run.Project.QueueId, run.Project.CategoryId, run.Project.Folder, startQueue: true);
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
        {
            // The results could not be saved; the next run compares with what was saved before.
        }
        run.Finish(result);
        RunFinished?.Invoke(run);
        ProjectsChanged?.Invoke();
        return result;
    }

    /// <summary>Saves what the run saw: every file of an offline copy, when files were last seen, which ones are gone.</summary>
    private void Record(GrabberRun run, GrabberRunResult result)
    {
        var now = _clock.UtcNow;
        var updates = new Dictionary<string, GrabberFileState>(StringComparer.Ordinal);
        if (run.Project.Mode == GrabberMode.Mirror)
        {
            foreach (var state in run.Engine.States.Values)
                updates[state.Url] = state;
        }
        else
        {
            // Files mode remembers a file when it is sent; a run only says it is still there.
            foreach (var key in run.Engine.Present)
            {
                if (run.Previous.TryGetValue(key, out var known) && known.RemovedUtc is null)
                {
                    var seen = known.Clone();
                    seen.LastSeenUtc = now;
                    updates[key] = seen;
                }
            }
        }
        foreach (var removed in run.Items.Where(i => i.Status == GrabberItemStatus.Removed))
        {
            if (run.Previous.TryGetValue(removed.Url, out var known))
            {
                var gone = known.Clone();
                gone.RemovedUtc = now;
                updates[removed.Url] = gone;
            }
        }
        if (updates.Count > 0)
            _store.SaveGrabberFiles(run.Project.Id, updates.Values);

        lock (_lock)
        {
            if (_projects.FirstOrDefault(p => p.Id == run.Project.Id) is not { } project)
                return;
            project.LastRunUtc = now;
            project.LastResult = Summary(result);
            // The run (and the preview, which sends with the run's own copy) no longer needs the session.
            if (project.ForgetCookiesAfterRun)
                project.Cookies = [];
            _store.SaveGrabberProject(project);
        }
    }

    /// <summary>Deletes the browser cookies captured with a project.</summary>
    public void ClearCookies(long projectId)
    {
        lock (_lock)
        {
            if (_projects.FirstOrDefault(p => p.Id == projectId) is not { } project)
                return;
            project.Cookies = [];
            _store.SaveGrabberProject(project);
        }
        ProjectsChanged?.Invoke();
    }

    public static string Summary(GrabberRunResult result)
    {
        var text = string.Format(CultureInfo.CurrentCulture, Strings.GrabberSummary, result.Pages, result.Files, result.New, result.Changed, result.Removed);
        if (result.Cancelled)
            text += " · " + Strings.GrabberSummaryStopped;
        else if (result.LimitReached is { } limit)
            text += " · " + string.Format(CultureInfo.CurrentCulture, Strings.GrabberSummaryLimit, LimitText(limit));
        return text;
    }

    public static string LimitText(string limit) => limit switch
    {
        "pages" => Strings.GrabberLimitPages,
        "files" => Strings.GrabberLimitFiles,
        "size" => Strings.GrabberLimitSize,
        _ => limit,
    };

    /// <summary>
    /// Sends files found by a run to a queue. Each one is fetched like the page would (its page as referrer, the
    /// project's cookies) and remembered, so the next synchronization sends it again only if it changes.
    /// </summary>
    public int Send(long projectId, IReadOnlyList<GrabberItem> items, long? queueId, long? categoryId, string? folder, bool startQueue) =>
        Send(Find(projectId) ?? throw new ArgumentException(Strings.ErrorProjectGone), items, queueId, categoryId, folder, startQueue);

    /// <summary>Sends files of a run, with the cookies the run had (they may have been forgotten since).</summary>
    public int Send(GrabberRun run, IReadOnlyList<GrabberItem> items, long? queueId, long? categoryId, string? folder, bool startQueue)
    {
        if (Find(run.Project.Id) is null)
            throw new ArgumentException(Strings.ErrorProjectGone);
        return Send(run.Project, items, queueId, categoryId, folder, startQueue);
    }

    private int Send(GrabberProject project, IReadOnlyList<GrabberItem> items, long? queueId, long? categoryId, string? folder, bool startQueue)
    {
        var projectId = project.Id;
        var known = _store.LoadGrabberFiles(projectId);
        var now = _clock.UtcNow;
        var states = new List<GrabberFileState>();
        var target = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        var queue = queueId ?? _downloads.MainQueueId;
        var cookies = project.UseCookies ? project.Cookies : [];
        var userAgent = string.IsNullOrWhiteSpace(project.UserAgent) ? null : project.UserAgent;
        var sent = 0;
        foreach (var item in items.Where(i => !i.IsPage && i.Status is not (GrabberItemStatus.Removed or GrabberItemStatus.Failed)))
        {
            var referrer = Uri.TryCreate(item.SourcePage, UriKind.Absolute, out var page) ? page.AbsoluteUri : null;
            var downloadId = _downloads.Add(item.Url, target, null, startNow: false, new RequestContext(referrer, userAgent, cookies), categoryId, queue);
            known.TryGetValue(item.Url, out var old);
            states.Add(new GrabberFileState
            {
                Url = item.Url, ETag = item.ETag, LastModified = item.LastModified, Size = item.Size, ContentType = item.ContentType,
                SourcePage = item.SourcePage, FirstSeenUtc = old?.FirstSeenUtc ?? now, LastSeenUtc = now,
                DownloadId = downloadId, Obtained = false,
            });
            sent++;
        }
        _store.SaveGrabberFiles(projectId, states);
        if (startQueue && sent > 0)
            _downloads.StartQueue(queue);
        return sent;
    }

    /// <summary>
    /// One step of the scheduler: a project whose schedule enters a window (or a repeat slot) starts, once per slot,
    /// also when the app was started after the start time (but not again if it already ran in that slot).
    /// </summary>
    public void Tick()
    {
        var now = _clock.UtcNow;
        var due = new List<long>();
        lock (_lock)
        {
            foreach (var project in _projects)
            {
                var slot = project.Schedule.Enabled ? ScheduleCalculator.Slot(project.Schedule, now, _clock.Zone) : null;
                var seen = _lastSlot.TryGetValue(project.Id, out var previous);
                _lastSlot[project.Id] = slot;
                if (slot is not { } current || (seen && previous == current))
                    continue;
                var slotStart = current.WindowStart.AddMinutes(current.Slot * Math.Max(0, project.Schedule.RepeatMinutes));
                if (project.LastRunUtc is { } last && last >= slotStart)
                    continue;
                if (_runs.TryGetValue(project.Id, out var run) && run.Result is null)
                    continue;
                due.Add(project.Id);
            }
        }
        foreach (var id in due)
            Start(id, automatic: true);
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                Tick();
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
            {
                // Retried on the next tick.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tickerCts.Cancel();
        await _ticker.ContinueWith(_ => { }, TaskScheduler.Default);
        List<GrabberRun> runs;
        lock (_lock)
            runs = _runs.Values.Where(r => r.Result is null).ToList();
        foreach (var run in runs)
            run.Cancel();
        await Task.WhenAll(runs.Select(r => r.Completion)).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
