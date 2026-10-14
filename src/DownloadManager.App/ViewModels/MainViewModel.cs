using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DownloadManager.App.Mvvm;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string ColumnsPreference = "columns";

    private readonly DownloadService _service;
    private readonly DownloadStore _store;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IStartupService _startup;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<object> _fromBrowser = new(); // downloads, streams and "download all" requests, in arrival order
    private readonly ListCollectionView _view;
    private TimeSpan _lastTick;
    private IReadOnlyList<Category> _categories = [];
    private IReadOnlyList<DownloadQueue> _queues = [];
    private IReadOnlyList<QueueStatus> _queueStatuses = [];
    private List<DownloadItemViewModel> _selected = [];
    private NavigationNodeViewModel _selectedNode = null!;
    private DownloadFilter _filter = new();
    private string _search = "";
    private string? _sortKey;
    private bool _sortDescending;
    private bool _showingBrowserDialogs;

    public MainViewModel(DownloadService service, DownloadStore store, IDialogService dialogs, IShellService shell, IStartupService startup)
    {
        _service = service;
        _store = store;
        _dialogs = dialogs;
        _shell = shell;
        _startup = startup;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        _view.Filter = o => o is DownloadItemViewModel item && _filter.Matches(item.Status, item.CategoryId, item.Name, item.Url, item.QueueId);
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.Status));
        _view.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.CategoryId));
        _view.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.QueueId));
        _view.IsLiveSorting = true;

        AddCommand = new RelayCommand(() => ShowAddDialog(Urls.SingleHttpUrl(_shell.GetClipboardText())));
        PasteCommand = new RelayCommand(Paste);
        StartCommand = new RelayCommand(Start, () => _selected.Any(i => i.Status is not (DownloadStatus.Completed or DownloadStatus.Downloading)));
        PauseCommand = new RelayCommand(Pause, () => _selected.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued));
        EnqueueCommand = new RelayCommand(Enqueue, () => _selected.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed));
        RemoveCommand = new RelayCommand(Remove, () => _selected.Count > 0);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected.Count == 1 && IsQueueOrder);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _selected.Count == 1 && IsQueueOrder);
        ToggleQueueCommand = new RelayCommand(ToggleQueue);
        QueuesCommand = new RelayCommand(p => ShowQueues(p as long?, addNew: false));
        NewQueueCommand = new RelayCommand(() => ShowQueues(null, addNew: true));
        DeleteQueueCommand = new RelayCommand(p => DeleteQueue(p as long?), p => p is long id && _queues.Any(q => q.Id == id && !q.IsMain));
        StartQueueCommand = new RelayCommand(p => { if (p is long id) _service.StartQueue(id); Refresh(); }, p => p is long id && !IsRunning(id));
        StopQueueCommand = new RelayCommand(async p => { if (p is long id) await _service.StopQueueAsync(id); Refresh(); }, p => p is long id && IsRunning(id));
        EnableStartupCommand = new RelayCommand(() =>
        {
            _startup.Set(true);
            OnPropertyChanged(nameof(ShowStartupWarning));
        });
        SettingsCommand = new RelayCommand(ShowSettings);
        CategoriesCommand = new RelayCommand(p => ShowCategories(p as long?, addNew: false));
        NewCategoryCommand = new RelayCommand(() => ShowCategories(null, addNew: true));
        DeleteCategoryCommand = new RelayCommand(p => DeleteCategory(p as long?), p => p is long id && _categories.Any(c => c.Id == id && !c.IsGeneral));
        OpenDownloadFolderCommand = new RelayCommand(() => _shell.OpenFolder(_service.Settings.DownloadFolder));
        OpenFileCommand = new RelayCommand(OpenFile, () => _selected.FirstOrDefault() is { IsCompleted: true });
        OpenFolderCommand = new RelayCommand(() => _shell.Reveal(_selected[0].Snapshot.FinalPath, _selected[0].Directory), () => _selected.Count > 0);
        CopyUrlCommand = new RelayCommand(() => _shell.SetClipboardText(string.Join(Environment.NewLine, _selected.Select(i => i.Url))), () => _selected.Count > 0);
        ProgressCommand = new RelayCommand(() => _dialogs.ShowProgress(_selected[0]), () => _selected.Count == 1);
        PropertiesCommand = new RelayCommand(ShowProperties, () => _selected.Count == 1);
        DefaultActionCommand = new RelayCommand(DefaultAction, () => _selected.Count == 1);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");

        BuildNavigation();
        _service.CategoriesChanged += () => _timer.Dispatcher.InvokeAsync(BuildNavigation);
        _service.QueuesChanged += () => _timer.Dispatcher.InvokeAsync(OnQueuesChanged);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    /// <summary>The filtered and sorted list the grid shows.</summary>
    public ICollectionView ItemsView => _view;

    public ObservableCollection<NavigationNodeViewModel> Navigation { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand EnqueueCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ToggleQueueCommand { get; }
    public ICommand QueuesCommand { get; }
    public ICommand NewQueueCommand { get; }
    public ICommand DeleteQueueCommand { get; }
    public ICommand StartQueueCommand { get; }
    public ICommand StopQueueCommand { get; }
    public ICommand EnableStartupCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CategoriesCommand { get; }
    public ICommand NewCategoryCommand { get; }
    public ICommand DeleteCategoryCommand { get; }
    public ICommand OpenDownloadFolderCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand ProgressCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand DefaultActionCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value))
                return;
            ApplyFilter();
        }
    }

    public string Heading => _selectedNode.IsCategory || _selectedNode.IsQueue
        ? $"{FindRoot(_selectedNode)?.Title} · {_selectedNode.Title}"
        : _selectedNode.Title;

    /// <summary>The queue the toolbar button acts on: the one selected in the side panel, otherwise the main queue.</summary>
    public long CurrentQueueId => _selectedNode?.QueueId ?? _service.MainQueueId;

    public IReadOnlyList<DownloadQueue> Queues => _queues;

    /// <summary>A schedule works only while the app runs: warn when it does not start with Windows.</summary>
    public bool ShowStartupWarning => _queues.Any(q => q.Schedule.Enabled) && !_startup.IsEnabled;

    private bool IsRunning(long queueId) => _queueStatuses.FirstOrDefault(s => s.QueueId == queueId)?.State == QueueState.Running;

    public bool IsEmpty => _view.IsEmpty;

    public string EmptyTitle => Items.Count == 0 ? Strings.EmptyTitle : Strings.EmptyFilteredTitle;

    public string EmptyHint => Items.Count == 0 ? Strings.EmptyHint : Strings.EmptyFilteredHint;

    public bool QueueRunning => IsRunning(CurrentQueueId);

    private string CurrentQueueName => _queues.FirstOrDefault(q => q.Id == CurrentQueueId)?.Name ?? "";

    public string QueueText => QueueRunning ? Strings.QueueStop : Strings.QueueStart;

    public string QueueGlyph => QueueRunning ? "" : "";

    public string QueueToolTip => string.Format(CultureInfo.CurrentCulture, QueueRunning ? Strings.QueueStopToolTip : Strings.QueueStartToolTip, CurrentQueueName);

    public string StatusText { get; private set; } = "";

    public string SpeedText { get; private set; } = "";

    public string? SortKey => _sortKey;

    public bool SortDescending => _sortDescending;

    /// <summary>Moving items up and down only makes sense when the list shows the queue order.</summary>
    public bool IsQueueOrder => _sortKey is null;

    public void UpdateSelection(IEnumerable<DownloadItemViewModel> selected)
    {
        _selected = selected.ToList();
        CommandManager.InvalidateRequerySuggested();
    }

    public IReadOnlyList<DownloadItemViewModel> SelectedItems => _selected;

    public ColumnLayout LoadColumnLayout(IReadOnlyList<ColumnState> defaults)
    {
        var layout = ColumnLayout.Restore(_store.GetPreference(ColumnsPreference), defaults);
        ApplySort(layout.SortKey, layout.SortDescending);
        return layout;
    }

    public void SaveColumnLayout(ColumnLayout layout)
    {
        layout.SortKey = _sortKey;
        layout.SortDescending = _sortDescending;
        _store.SetPreference(ColumnsPreference, layout.Serialize());
    }

    /// <summary>Sorts by a column: ascending first, then descending. A null key restores the queue order.</summary>
    public void SortBy(string? key)
    {
        if (key is null)
            ApplySort(null, false);
        else
            ApplySort(key, key == _sortKey && !_sortDescending);
    }

    private void ApplySort(string? key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.LiveSortingProperties.Clear();
            if (key is not null)
            {
                _view.SortDescriptions.Add(new SortDescription(key, descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
                _view.LiveSortingProperties.Add(key);
            }
            _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.SortOrder), ListSortDirection.Ascending));
            _view.LiveSortingProperties.Add(nameof(DownloadItemViewModel.SortOrder));
        }
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(IsQueueOrder));
        CommandManager.InvalidateRequerySuggested();
    }

    public void MoveToQueue(IEnumerable<long> ids, long queueId)
    {
        _service.MoveToQueue(ids, queueId);
        Refresh();
    }

    /// <summary>Downloads dragged onto a side panel node: a queue node moves them, a category node re-files them.</summary>
    public bool DropOnNode(NavigationNodeViewModel node, IReadOnlyList<long> ids)
    {
        if (node.QueueId is { } queueId)
            MoveToQueue(ids, queueId);
        else if (node.CategoryId is { } categoryId)
        {
            foreach (var id in ids)
                _service.SetCategory(id, categoryId);
            Refresh();
        }
        else
            return false;
        return true;
    }

    public static bool AcceptsDrop(NavigationNodeViewModel node) => node.IsQueue || node.IsCategory;

    /// <summary>Links dropped on the window: one opens the add dialog, several go to the queue after a confirmation.</summary>
    public void AddLinks(IReadOnlyList<string> urls)
    {
        if (urls.Count == 1)
        {
            ShowAddDialog(urls[0]);
            return;
        }
        if (urls.Count == 0 || !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmAddLinks, urls.Count), Strings.AddTitle))
            return;
        foreach (var url in urls)
            _service.Add(url, startNow: false, queueId: CurrentQueueId);
        Refresh();
    }

    /// <summary>A download sent by the browser extension: confirmed in the add dialog, or started directly.</summary>
    public void ReceiveFromBrowser(BrowserDownload download, bool closing) => ReceiveRequest(download, closing);

    /// <summary>A video or audio stream in the quality chosen in the page: confirmed like any download from the browser.</summary>
    public void ReceiveMedia(MediaRequest media, bool closing) => ReceiveRequest(media, closing);

    /// <summary>"Download all": the links and images of a page, to choose from.</summary>
    public void ReceiveBatch(BatchRequest batch, bool closing) => ReceiveRequest(batch, closing);

    private void ReceiveRequest(object request, bool closing)
    {
        _fromBrowser.Enqueue(request);
        // Requests that arrive while a dialog is open wait for it to close.
        if (_showingBrowserDialogs || closing)
            return;
        _showingBrowserDialogs = true;
        try
        {
            while (_fromBrowser.TryDequeue(out var next))
            {
                switch (next)
                {
                    case BrowserDownload download:
                        HandleDownload(download);
                        break;
                    case MediaRequest media:
                        HandleMedia(media);
                        break;
                    case BatchRequest batch:
                        HandleBatch(batch);
                        break;
                }
            }
        }
        finally
        {
            _showingBrowserDialogs = false;
        }
        Refresh();
    }

    private void HandleDownload(BrowserDownload next)
    {
        var fileName = string.IsNullOrWhiteSpace(next.FileName) ? null : Path.GetFileName(next.FileName);
        // Extensions before 0.4 send no queue: the main queue.
        var queueId = _service.ResolveQueue(next.Queue);
        var context = next.ToContext();
        if (!_service.Settings.ConfirmBrowserDownloads)
        {
            _service.Add(next.Url, null, fileName, startNow: true, context, queueId: queueId);
            return;
        }

        var note = context.IsPost ? Strings.FromBrowserForm
            : next.Cookies is { Count: > 0 } ? Strings.FromBrowserWithCookies : Strings.FromBrowser;
        var dialog = new AddDownloadViewModel(_dialogs, _service.Categories, _service.Settings.DownloadFolder, next.Url, fileName, note,
            _service.Queues, queueId)
        {
            // A form's address only makes sense with the form's data.
            UrlReadOnly = context.IsPost,
        };
        if (_dialogs.ShowAddDownload(dialog, standalone: true))
            Submit(dialog, context);
    }

    private void HandleMedia(MediaRequest media)
    {
        var variant = media.VariantId ?? "";
        if (variant == "file")
        {
            // A plain video or audio file: an ordinary download.
            HandleDownload(new BrowserDownload(media.Url, null, media.Referrer ?? media.PageUrl, media.UserAgent, media.Cookies, media.Queue));
            return;
        }
        var kind = variant.StartsWith("dash:", StringComparison.Ordinal) ? "dash" : "hls";
        var queueId = _service.ResolveQueue(media.Queue);
        var fileName = Core.Media.MediaCatalog.FileNameFor(media.Title, media.Url, ".mp4");
        if (!_service.Settings.ConfirmBrowserDownloads)
        {
            _service.AddMedia(media, kind, media.Label, fileName: fileName, queueId: queueId);
            return;
        }

        var note = string.Format(CultureInfo.CurrentCulture, Strings.FromBrowserMedia, kind.ToUpperInvariant(), media.Label ?? "");
        var dialog = new AddDownloadViewModel(_dialogs, _service.Categories, _service.Settings.DownloadFolder, media.Url, fileName, note,
            _service.Queues, queueId)
        {
            UrlReadOnly = true, // the manifest of the quality chosen in the page
        };
        if (!_dialogs.ShowAddDownload(dialog, standalone: true))
            return;
        var (_, folder, name, categoryId, chosenQueue) = dialog.ToRequest();
        try
        {
            _service.AddMedia(media, kind, media.Label, folder, name, dialog.StartNow, categoryId, chosenQueue);
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError(ex.Message, Strings.AddTitle);
        }
    }

    private void HandleBatch(BatchRequest batch)
    {
        var queueId = _service.ResolveQueue(batch.Queue);
        var dialog = new BatchViewModel(batch, _dialogs, _service.Categories, _service.Settings.DownloadFolder, _service.Queues, queueId);
        if (dialog.Items.Count == 0)
        {
            _dialogs.Inform(Strings.BatchNothing, Strings.BatchTitle);
            return;
        }
        if (!_dialogs.ShowBatch(dialog))
            return;

        // Every file is fetched like the page would: its address as referrer, the browser's cookies (each sent only
        // to its own site).
        var context = new RequestContext(batch.PageUrl, batch.UserAgent, batch.Cookies ?? []);
        var folder = dialog.Folder.Trim().Length > 0 ? dialog.Folder.Trim() : null;
        var queue = dialog.Queue?.Id ?? queueId;
        foreach (var item in dialog.Selected)
            _service.Add(item.Url, folder, null, startNow: false, context, dialog.Category.Category?.Id, queue);
        // "Download now" starts the queue, so the files still respect its limit instead of all starting at once.
        if (dialog.StartNow)
            _service.StartQueue(queue);
    }

    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        var now = _clock.Elapsed;
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        _queueStatuses = _service.GetQueueStatuses();
        var snapshots = _service.GetSnapshots();
        var byId = Items.ToDictionary(i => i.Id);
        var keep = snapshots.Select(s => s.Id).ToHashSet();
        foreach (var gone in Items.Where(i => !keep.Contains(i.Id)).ToList())
            Items.Remove(gone);
        foreach (var snap in snapshots)
        {
            var category = CategoryName(snap.CategoryId);
            var queue = _queues.FirstOrDefault(q => q.Id == snap.QueueId)?.Name ?? "";
            if (byId.TryGetValue(snap.Id, out var vm))
                vm.Update(snap, category, queue, elapsed);
            else
                Items.Add(new DownloadItemViewModel(snap, category, queue));
        }

        foreach (var root in Navigation)
        {
            root.Count = Items.Count(i => root.Filter.Matches(i.Status, i.CategoryId, "", "", i.QueueId));
            foreach (var child in root.Children)
            {
                child.Count = Items.Count(i => child.Filter.Matches(i.Status, i.CategoryId, "", "", i.QueueId));
                if (child.QueueId is { } queueId && _queueStatuses.FirstOrDefault(s => s.QueueId == queueId) is { } status)
                {
                    child.Detail = QueueStateText(status);
                    child.Glyph = status.State switch
                    {
                        QueueState.Running => "\uE768",
                        QueueState.Scheduled => "\uE823",
                        _ => "\uE71A",
                    };
                }
            }
        }

        var active = Items.Count(i => i.IsActive);
        var queued = Items.Count(i => i.Status == DownloadStatus.Queued);
        StatusText = string.Format(CultureInfo.CurrentCulture, Strings.StatusBar,
            _queueStatuses.Count(s => s.State == QueueState.Running), _queueStatuses.Count, active, queued, Items.Count);
        var limit = _service.Settings.SpeedLimitKBps;
        SpeedText = Format.Speed(Items.Sum(i => i.Speed))
                    + (limit > 0 ? "   " + string.Format(CultureInfo.CurrentCulture, Strings.SpeedLimitStatus, limit) : "");

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(QueueRunning));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(QueueGlyph));
        OnPropertyChanged(nameof(QueueToolTip));
        OnPropertyChanged(nameof(ShowStartupWarning));
        RaiseEmptyState();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseEmptyState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private string CategoryName(long? id) => _categories.FirstOrDefault(c => c.Id == id)?.Name ?? "";

    /// <summary>"Running", "Scheduled: Mon 22:00" or "Stopped", for the side panel and the tray.</summary>
    public string QueueStateText(QueueStatus status) => status.State switch
    {
        QueueState.Running => Strings.QueueStateRunning,
        QueueState.Scheduled when status.NextStartUtc is { } next =>
            string.Format(CultureInfo.CurrentCulture, Strings.QueueStateScheduled, TimeZoneInfo.ConvertTimeFromUtc(next, TimeZoneInfo.Local)),
        _ => Strings.QueueStateStopped,
    };

    public IReadOnlyList<QueueStatus> QueueStatuses => _queueStatuses;

    /// <summary>Queues added, removed or renamed rebuild the side panel; a start or stop only refreshes it.</summary>
    private void OnQueuesChanged()
    {
        var fresh = _service.Queues;
        var changed = fresh.Count != _queues.Count || fresh.Zip(_queues).Any(p => p.First.Id != p.Second.Id || p.First.Name != p.Second.Name);
        _queues = fresh;
        if (changed)
            BuildNavigation();
        else
            Refresh();
    }

    private void BuildNavigation()
    {
        _categories = _service.Categories;
        _queues = _service.Queues;
        var previous = _selectedNode is null ? new DownloadFilter() : _selectedNode.Filter;
        var expanded = Navigation.Where(n => n.IsExpanded && !n.IsQueuesRoot).Select(n => n.Status).ToHashSet();
        var queuesExpanded = Navigation.Count == 0 || Navigation.Any(n => n.IsQueuesRoot && n.IsExpanded);
        if (Navigation.Count == 0)
            expanded.Add(StatusFilter.All);

        Navigation.Clear();
        foreach (var (status, title, glyph) in new[]
                 {
                     (StatusFilter.All, Strings.NavAll, ""),
                     (StatusFilter.Unfinished, Strings.NavUnfinished, ""),
                     (StatusFilter.Finished, Strings.NavFinished, ""),
                 })
        {
            var root = new NavigationNodeViewModel(title, glyph, status, null, OnNodeSelected) { IsExpanded = expanded.Contains(status) };
            foreach (var category in _categories)
                root.Children.Add(new NavigationNodeViewModel(category.Name, CategoryGlyph(category), status, category.Id, OnNodeSelected));
            Navigation.Add(root);
        }

        var queuesRoot = new NavigationNodeViewModel(Strings.NavQueues, "\uE8FD", StatusFilter.All, null, OnNodeSelected)
        {
            IsQueuesRoot = true,
            IsExpanded = queuesExpanded,
        };
        foreach (var queue in _queues)
            queuesRoot.Children.Add(new NavigationNodeViewModel(queue.Name, "\uE71A", StatusFilter.All, null, OnNodeSelected, queue.Id));
        Navigation.Add(queuesRoot);

        var all = Navigation.SelectMany(n => n.Children.Prepend(n)).ToList();
        var select = all.FirstOrDefault(n => n.Filter == previous with { Search = null } && n.IsQueuesRoot == _selectedNode?.IsQueuesRoot)
                     ?? Navigation[0];
        if (select.IsCategory || select.IsQueue)
            FindRoot(select)!.IsExpanded = true;
        select.IsSelected = true;
        OnNodeSelected(select);
        if (Items.Count > 0)
            Refresh(); // category names in the list
    }

    private static string CategoryGlyph(Category category) => category.Key switch
    {
        Core.Categories.CompressedKey => "",
        Core.Categories.DocumentsKey => "",
        Core.Categories.MusicKey => "",
        Core.Categories.VideoKey => "",
        Core.Categories.ProgramsKey => "",
        Core.Categories.GeneralKey => "",
        _ => "", // tag, for the user's own categories
    };

    private NavigationNodeViewModel? FindRoot(NavigationNodeViewModel node) => Navigation.FirstOrDefault(n => n.Children.Contains(node));

    private void OnNodeSelected(NavigationNodeViewModel node)
    {
        _selectedNode = node;
        ApplyFilter();
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(QueueGlyph));
        OnPropertyChanged(nameof(QueueToolTip));
    }

    private void ApplyFilter()
    {
        _filter = _selectedNode.Filter with { Search = _search };
        _view.Refresh();
        RaiseEmptyState();
    }

    private void Paste()
    {
        var urls = Urls.ExtractHttpUrls(_shell.GetClipboardText());
        if (urls.Count > 0)
            AddLinks(urls);
    }

    private void ShowAddDialog(string? url)
    {
        var dialog = new AddDownloadViewModel(_dialogs, _service.Categories, _service.Settings.DownloadFolder, url,
            queues: _service.Queues, queueId: CurrentQueueId);
        if (_dialogs.ShowAddDownload(dialog, standalone: false))
            Submit(dialog, null);
    }

    private void Submit(AddDownloadViewModel dialog, RequestContext? context)
    {
        var (url, folder, fileName, categoryId, queueId) = dialog.ToRequest();
        _service.Add(url, folder, fileName, dialog.StartNow, context, categoryId, queueId);
        Refresh();
    }

    private void Start()
    {
        foreach (var item in _selected)
            _service.Start(item.Id);
        Refresh();
    }

    private async void Pause()
    {
        await Task.WhenAll(_selected.Select(i => _service.PauseAsync(i.Id)));
        Refresh();
    }

    private void Enqueue()
    {
        foreach (var item in _selected)
            _service.Enqueue(item.Id);
        Refresh();
    }

    private async void Remove()
    {
        var selected = _selected.ToList();
        var deleteFiles = false;
        if (selected.Any(i => i.IsCompleted))
        {
            var answer = _dialogs.Ask(Strings.ConfirmRemoveCompleted, Strings.RemoveTitle);
            if (answer == Answer.Cancel)
                return;
            deleteFiles = answer == Answer.Yes;
        }
        else if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmRemove, selected.Count), Strings.RemoveTitle))
        {
            return;
        }

        await Task.WhenAll(selected.Select(i => _service.RemoveAsync(i.Id, deleteFiles)));
        Refresh();
    }

    private void Move(int delta)
    {
        _service.Move(_selected[0].Id, delta);
        Refresh();
    }

    private async void ToggleQueue()
    {
        var queueId = CurrentQueueId;
        if (IsRunning(queueId))
            await _service.StopQueueAsync(queueId);
        else
            _service.StartQueue(queueId);
        Refresh();
    }

    private void ShowQueues(long? selectId, bool addNew)
    {
        var dialog = new QueuesViewModel(_dialogs, _service.Queues, () => _service.UtcNow, _startup.IsEnabled, () => _startup.Set(true),
            selectId ?? (_selectedNode.QueueId));
        if (addNew)
            dialog.AddCommand.Execute(null);
        if (!_dialogs.ShowQueues(dialog))
            return;
        foreach (var deleted in dialog.Deleted)
            _service.DeleteQueue(deleted.Id);
        foreach (var queue in dialog.Result)
            _service.SaveQueue(queue);
        OnPropertyChanged(nameof(ShowStartupWarning));
    }

    private void DeleteQueue(long? id)
    {
        if (_queues.FirstOrDefault(q => q.Id == id) is not { IsMain: false } queue)
            return;
        if (_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteQueue, queue.Name), Strings.QueuesTitle))
            _service.DeleteQueue(queue.Id);
    }

    private void ShowSettings()
    {
        var dialog = new SettingsViewModel(_dialogs, _service.Settings, _startup.IsEnabled, _service.Credentials, _service.Certificates);
        if (!_dialogs.ShowSettings(dialog) || dialog.Result is null)
            return;
        _service.UpdateSettings(dialog.Result);
        if (dialog.RunAtStartup != _startup.IsEnabled)
            _startup.Set(dialog.RunAtStartup);
        SettingsSaved?.Invoke(dialog.Result);
        Refresh();
    }

    /// <summary>Lets the app apply settings that concern the UI, such as the theme.</summary>
    public event Action<AppSettings>? SettingsSaved;

    private void ShowCategories(long? selectId, bool addNew)
    {
        var dialog = new CategoriesViewModel(_dialogs, _service.Categories, _service.Settings.DownloadFolder, selectId);
        if (addNew)
            dialog.AddCommand.Execute(null);
        if (!_dialogs.ShowCategories(dialog))
            return;
        foreach (var deleted in dialog.Deleted)
            _service.DeleteCategory(deleted.Id);
        foreach (var changed in dialog.Changed)
            _service.SaveCategory(changed);
    }

    private void DeleteCategory(long? id)
    {
        if (_categories.FirstOrDefault(c => c.Id == id) is not { IsGeneral: false } category)
            return;
        if (_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteCategory, category.Name), Strings.CategoriesTitle))
            _service.DeleteCategory(category.Id);
    }

    private void OpenFile()
    {
        if (_selected.FirstOrDefault() is not { IsCompleted: true } item)
            return;
        if (item.Snapshot.FinalPath is { } path && File.Exists(path))
            _shell.OpenFile(path);
        else
            _dialogs.ShowError(Strings.ErrorFileMissing, Strings.AppName);
    }

    private void ShowProperties()
    {
        if (_service.GetSnapshot(_selected[0].Id) is { } snapshot)
            _dialogs.ShowProperties(new PropertiesViewModel(snapshot, _service, _dialogs, _shell));
        Refresh();
    }

    /// <summary>Double click or Enter: open a finished file's folder, otherwise show the progress window.</summary>
    private void DefaultAction()
    {
        var item = _selected[0];
        if (item.IsCompleted)
            _shell.Reveal(item.Snapshot.FinalPath, item.Directory);
        else
        {
            if (!item.IsActive && item.Status != DownloadStatus.Queued)
                _service.Start(item.Id);
            _dialogs.ShowProgress(item);
        }
    }
}
