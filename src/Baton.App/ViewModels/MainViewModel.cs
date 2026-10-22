using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Portable;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string ColumnsPreference = "columns";

    private readonly DownloadService _service;
    private readonly DownloadStore _store;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IStartupService _startup;
    private readonly GrabberUi _grabber;
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

    public MainViewModel(DownloadService service, DownloadStore store, IDialogService dialogs, IShellService shell, IStartupService startup,
        GrabberUi grabber)
    {
        _service = service;
        _grabber = grabber;
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
        DeleteFilesCommand = new RelayCommand(DeleteFiles, () => _selected.Count > 0);
        RetryCommand = new RelayCommand(Retry, () => _selected.Any(i => i.Status == DownloadStatus.Failed));
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
        ExportListCommand = new RelayCommand(() => Export(Strings.ExportListFilter, "downloads.json",
            () => PortableData.Serialize(PortableData.ExportDownloads(_service.ExportRecords(), _service.Categories, _service.Queues, _service.UtcNow))));
        ExportUrlsCommand = new RelayCommand(() => Export(Strings.ExportUrlsFilter, "downloads.txt", () => PortableData.ExportUrls(_service.ExportRecords())));
        ExportSettingsCommand = new RelayCommand(() => Export(Strings.ExportListFilter, "baton-settings.json",
            () => PortableData.Serialize(PortableData.ExportSettings(_service.Settings, _service.Categories, _service.Queues, _grabber.Service.Projects, _service.UtcNow))));
        ImportCommand = new RelayCommand(Import);
        CustomizeToolbarCommand = new RelayCommand(CustomizeToolbar);
        OpenFileCommand = new RelayCommand(OpenFile, () => _selected.Any(i => i.IsCompleted));
        OpenFolderCommand = new RelayCommand(OpenFolders, () => _selected.Count > 0);
        CopyUrlCommand = new RelayCommand(() => _shell.SetClipboardText(string.Join(Environment.NewLine, _selected.Select(i => i.Url))), () => _selected.Count > 0);
        ProgressCommand = new RelayCommand(() => _dialogs.ShowProgress(_selected[0]), () => _selected.Count == 1);
        PropertiesCommand = new RelayCommand(ShowProperties, () => _selected.Count == 1);
        DefaultActionCommand = new RelayCommand(DefaultAction, () => _selected.Count == 1);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        GrabberCommand = new RelayCommand(() => _grabber.ShowProjects());

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
    public ICommand DeleteFilesCommand { get; }
    public ICommand RetryCommand { get; }
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
    public ICommand ExportListCommand { get; }
    public ICommand ExportUrlsCommand { get; }
    public ICommand ExportSettingsCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CustomizeToolbarCommand { get; }

    /// <summary>The settings the toolbar is laid out from.</summary>
    public AppSettings ToolbarSettings => _service.Settings;

    /// <summary>Raised after the toolbar layout was changed and saved.</summary>
    public event Action? ToolbarChanged;

    private void CustomizeToolbar()
    {
        var settings = _service.Settings;
        var dialog = new ToolbarViewModel(settings.ToolbarItems, settings.ToolbarSmallIcons, settings.ToolbarShowText);
        if (!_dialogs.ShowToolbar(dialog))
            return;
        settings = _service.Settings;
        settings.ToolbarItems = dialog.Layout == ToolbarLayout.Default ? "" : dialog.Layout;
        settings.ToolbarSmallIcons = dialog.SmallIcons;
        settings.ToolbarShowText = dialog.ShowText;
        _service.UpdateSettings(settings);
        ToolbarChanged?.Invoke();
    }

    /// <summary>Never logins, cookies or other secrets: the export carries only what <see cref="PortableData"/> puts in it.</summary>
    private void Export(string filter, string fileName, Func<string> content)
    {
        if (_dialogs.PickSaveFile(Strings.ExportTitle, filter, fileName) is not { } path)
            return;
        try
        {
            File.WriteAllText(path, content(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(StorageErrors.Describe(ex, path), Strings.ExportTitle);
        }
    }

    private void Import()
    {
        if (_dialogs.PickFile(Strings.ImportTitle, Strings.ImportFilter, null) is not { } path)
            return;
        List<ImportItem> items;
        try
        {
            items = PortableImport.Plan(PortableData.Read(path), _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Service.Projects);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message, Strings.ImportTitle);
            return;
        }
        var preview = new ImportViewModel(Path.GetFileName(path), items);
        if (!_dialogs.ShowImport(preview))
            return;
        var result = PortableImport.Apply(items, preview.Mode, _service, _grabber.Service);
        if (items.Any(i => i.Kind is ImportKind.Settings or ImportKind.NetworkSettings && i.Include && i.Problem is null))
            SettingsSaved?.Invoke(_service.Settings);
        Refresh();
        _dialogs.Inform(string.Format(CultureInfo.CurrentCulture, Strings.ImportDone, result.Added, result.Replaced, result.Skipped), Strings.ImportTitle);
    }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand ProgressCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand DefaultActionCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand GrabberCommand { get; }

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
    public bool ShowStartupWarning => !_startup.IsPortable && _queues.Any(q => q.Schedule.Enabled) && !_startup.IsEnabled;

    private bool IsRunning(long queueId) => _queueStatuses.FirstOrDefault(s => s.QueueId == queueId)?.State == QueueState.Running;

    public bool IsEmpty => _view.IsEmpty;

    public string EmptyTitle => Items.Count == 0 ? Strings.EmptyTitle : Strings.EmptyFilteredTitle;

    public string EmptyHint => Items.Count == 0 ? Strings.EmptyHint : Strings.EmptyFilteredHint;

    public bool QueueRunning => IsRunning(CurrentQueueId);

    private string CurrentQueueName => _queues.FirstOrDefault(q => q.Id == CurrentQueueId)?.DisplayName ?? "";

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
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectionToolTip));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// "3 selected · 1.2 GB of 4.8 GB" in the status bar: downloaded so far of the total size; "?" for the total when a
    /// server did not give the size of one of them. Empty without a selection.
    /// </summary>
    public string SelectionText
    {
        get
        {
            if (_selected.Count == 0)
                return "";
            var downloaded = Format.Bytes(SizeOf(_selected));
            var total = TotalOf(_selected) is { } bytes ? Format.Bytes(bytes) : "?";
            return _selected.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.SelectionStatusOne, downloaded, total)
                : string.Format(CultureInfo.CurrentCulture, Strings.SelectionStatus, _selected.Count, downloaded, total);
        }
    }

    /// <summary>Explains the "?" of <see cref="SelectionText"/>; null when the total is known.</summary>
    public string? SelectionToolTip => _selected.Count > 0 && TotalOf(_selected) is null ? Strings.SelectionTotalUnknown : null;

    /// <summary>The sum of the sizes, or null when a download's size is not known (yet).</summary>
    private static long? TotalOf(IEnumerable<DownloadItemViewModel> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            if (item.Snapshot.TotalSize is not { } size)
                return null;
            total += size;
        }
        return total;
    }

    /// <summary>What the downloads take on the disk: the whole file once finished, else what was downloaded so far.</summary>
    private static long SizeOf(IEnumerable<DownloadItemViewModel> items) =>
        items.Sum(i => i.IsCompleted ? i.Snapshot.TotalSize ?? i.Snapshot.Downloaded : i.Snapshot.Downloaded);

    /// <summary>"Rimuovi dalla lista" / "Rimuovi 5 download dalla lista", and the same for deleting from the disk.</summary>
    public string RemoveText => _selected.Count > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.MenuRemoveMany, _selected.Count) : Strings.MenuRemove;
    public string DeleteFilesText => _selected.Count > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.MenuDeleteFilesMany, _selected.Count) : Strings.MenuDeleteFiles;

    /// <summary>Re-files the downloads in another category (menu "Change category", or a drop on the category).</summary>
    public void ChangeCategory(IReadOnlyList<long> ids, long categoryId)
    {
        foreach (var id in ids)
            _service.SetCategory(id, categoryId);
        Refresh();
    }

    public IReadOnlyList<Category> Categories => _categories;

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
            ChangeCategory(ids, categoryId);
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

    /// <summary>"Capture this site": a new grabber project for the page, with its cookies.</summary>
    public void ReceiveGrab(GrabRequest grab, bool closing) => ReceiveRequest(grab, closing);

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
                    case GrabRequest grab:
                        _grabber.CreateFromBrowser(grab);
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
        // A form from another site is always confirmed, even with confirmations turned off.
        if (!_service.Settings.ConfirmBrowserDownloads && !next.IsCrossSitePost)
        {
            _service.Add(next.Url, null, fileName, startNow: true, context, queueId: queueId);
            return;
        }

        var note = next.IsCrossSitePost ? string.Format(CultureInfo.CurrentCulture, Strings.FromBrowserCrossSiteForm,
                Urls.SiteOf(next.Referrer) ?? Strings.Unknown, Urls.SiteOf(next.Url) ?? next.Url)
            : context.IsPost ? Strings.FromBrowserForm
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
        if (media.AudioId is { Length: > 0 } || media.SubtitleIds is { Count: > 0 })
        {
            // HLS ids are "language|name", DASH ids are representation ids: the readable part is shown.
            static string Readable(string id) => id.Split('|') is [var language, var name] ? (name.Length > 0 ? name : language) : id;
            var subtitles = media.SubtitleIds is { Count: > 0 } list
                ? string.Format(CultureInfo.CurrentCulture, Strings.FromBrowserMediaSubtitles, string.Join(", ", list.Select(Readable)),
                    media.SubtitleMode switch { "srt" => ".srt", "embed" => Strings.FromBrowserMediaEmbedded, _ => ".vtt" })
                : "";
            var audio = media.AudioId is { Length: > 0 } id ? string.Format(CultureInfo.CurrentCulture, Strings.FromBrowserMediaAudio, Readable(id)) : "";
            note += " " + string.Join(" ", new[] { audio, subtitles }.Where(s => s.Length > 0));
        }
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
            var queue = _queues.FirstOrDefault(q => q.Id == snap.QueueId)?.DisplayName ?? "";
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
        if (_selected.Count > 0)
        {
            OnPropertyChanged(nameof(SelectionText)); // what was downloaded grows
            OnPropertyChanged(nameof(SelectionToolTip));
        }
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

    private string CategoryName(long? id) => _categories.FirstOrDefault(c => c.Id == id)?.DisplayName ?? "";

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
                root.Children.Add(new NavigationNodeViewModel(category.DisplayName, CategoryGlyph(category), status, category.Id, OnNodeSelected));
            Navigation.Add(root);
        }

        var queuesRoot = new NavigationNodeViewModel(Strings.NavQueues, "\uE8FD", StatusFilter.All, null, OnNodeSelected)
        {
            IsQueuesRoot = true,
            IsExpanded = queuesExpanded,
        };
        foreach (var queue in _queues)
            queuesRoot.Children.Add(new NavigationNodeViewModel(queue.DisplayName, "\uE71A", StatusFilter.All, null, OnNodeSelected, queue.Id));
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
        _service.Add(url, folder, fileName, dialog.StartNow, context, categoryId, queueId, dialog.ExpectedHashOrNull);
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

    private void Retry()
    {
        foreach (var item in _selected.Where(i => i.Status == DownloadStatus.Failed))
            _service.Start(item.Id);
        Refresh();
    }

    /// <summary>Del: removes from the list (asked when more than one, or when partial data would go); downloaded files stay on the disk.</summary>
    private async void Remove()
    {
        var selected = _selected.ToList();
        if ((selected.Count > 1 || selected.Any(i => !i.IsCompleted)) && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmRemove, selected.Count), Strings.RemoveTitle))
            return;
        await Task.WhenAll(selected.Select(i => _service.RemoveAsync(i.Id, deleteFile: false)));
        Refresh();
    }

    /// <summary>
    /// Shift+Del: removes from the list and moves the files to the Recycle Bin, always after a confirmation that says how
    /// many and how much. Running downloads stop first and their partial data is cleaned; nothing is deleted for good.
    /// </summary>
    private async void DeleteFiles()
    {
        var selected = _selected.ToList();
        if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteFiles, selected.Count, Format.Bytes(SizeOf(selected))), Strings.DeleteTitle))
            return;
        var kept = new List<string>();
        foreach (var item in selected)
        {
            if (!await _service.RemoveAsync(item.Id, deleteFile: true) && item.Snapshot.FinalPath is { } path)
                kept.Add(path);
        }
        Refresh();
        if (kept.Count > 0)
            _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.DeleteKeptOnDisk, kept.Count, string.Join("\n", kept.Take(10))), Strings.DeleteTitle);
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
        if (_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteQueue, queue.DisplayName), Strings.QueuesTitle))
            _service.DeleteQueue(queue.Id);
    }

    private void ShowSettings()
    {
        var dialog = new SettingsViewModel(_dialogs, _service.Settings, _startup.IsEnabled, _service.Credentials, _service.Certificates,
            Updates is { } updates ? url => updates.CheckAsync(url, interactive: true) : null,
            Updates is { } restore && UpdateService.CanRestore ? restore.RestorePrevious : null,
            _startup.IsPortable ? Environment.ProcessPath : null, new BundledExtensionViewModel(AppContext.BaseDirectory, _shell));
        if (!_dialogs.ShowSettings(dialog) || dialog.Result is null)
            return;
        _service.UpdateSettings(dialog.Result);
        if (dialog.RunAtStartup != _startup.IsEnabled)
            _startup.Set(dialog.RunAtStartup);
        SettingsSaved?.Invoke(dialog.Result);
        Refresh();
    }

    /// <summary>Set by the app: update checks and "restore previous version".</summary>
    public UpdateService? Updates { get; set; }

    /// <summary>Texts built here (side panel, status bar, list) in the new language.</summary>
    public void OnLanguageChanged()
    {
        BuildNavigation();
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
        if (_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteCategory, category.DisplayName), Strings.CategoriesTitle))
            _service.DeleteCategory(category.Id);
    }

    private void OpenFile()
    {
        var files = _selected.Where(i => i.IsCompleted).Select(i => i.Snapshot.FinalPath).ToList();
        // Like File Explorer: past 15 files at once, asked first.
        if (files.Count > 15 && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmOpenMany, files.Count), Strings.AppName))
            return;
        foreach (var path in files.OfType<string>().Where(File.Exists))
            _shell.OpenFile(path);
        if (files.Any(p => p is null || !File.Exists(p)))
            _dialogs.ShowError(Strings.ErrorFileMissing, Strings.AppName);
    }

    /// <summary>Each folder once, with the first of its selected files highlighted.</summary>
    private void OpenFolders()
    {
        foreach (var item in _selected.GroupBy(i => i.Directory, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            _shell.Reveal(item.Snapshot.FinalPath, item.Directory);
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
