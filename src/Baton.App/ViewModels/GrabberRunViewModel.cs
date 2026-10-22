using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>One file found by a grabber run, as a row of the preview.</summary>
public sealed class GrabberItemViewModel : ObservableObject
{
    private readonly Action<GrabberItemViewModel, bool> _selectionChanged;
    private bool _isSelected;

    public GrabberItemViewModel(GrabberItem item, bool selectable, Action<GrabberItemViewModel, bool> selectionChanged)
    {
        Item = item;
        CanSelect = selectable && item.Status is not (GrabberItemStatus.Removed or GrabberItemStatus.Failed) && !item.IsPage;
        _isSelected = CanSelect && item.Status is GrabberItemStatus.New or GrabberItemStatus.Changed;
        _selectionChanged = selectionChanged;
        SourcePage = item.SourcePage.Length > 0 ? item.SourcePage : "—";
    }

    public GrabberItem Item { get; }
    public string Url => Item.Url;
    public string Name => Item.Name;
    public string Extension => Item.Extension;
    public long SizeSort => Item.Size ?? -1;
    public string SizeText => Item.Size is { } size ? Format.Bytes(size) : Strings.Unknown;
    public string TypeText => Item.ContentType ?? "";
    public string SourcePage { get; }
    public string? LocalPath => Item.LocalPath;
    public GrabberItemStatus Status => Item.Status;
    public bool CanSelect { get; }

    public string StatusText => Item.Status switch
    {
        GrabberItemStatus.New => Strings.GrabberStatusNew,
        GrabberItemStatus.Changed => Strings.GrabberStatusChanged,
        GrabberItemStatus.Unchanged => Strings.GrabberStatusUnchanged,
        GrabberItemStatus.Removed => Strings.GrabberStatusRemoved,
        _ => Strings.GrabberStatusFailed,
    };

    public string ToolTip => Item.Error is { } error ? $"{Url}\n{error}" : Url;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || !Set(ref _isSelected, value))
                return;
            _selectionChanged(this, value);
        }
    }

    /// <summary>Changes the tick without reporting it back: the caller keeps the count (selecting thousands of rows at once).</summary>
    internal void SetSelected(bool value)
    {
        if (!CanSelect || _isSelected == value)
            return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    public override string ToString() => $"{Name}, {StatusText}, {(IsSelected ? Strings.BatchItemSelected : Strings.BatchItemNotSelected)}";
}

/// <summary>
/// A grabber run as it happens: progress, and the files found so far in a table that stays fluid with thousands of
/// rows (rows arrive in batches a few times a second, counters are kept up to date without recounting, the grid
/// virtualizes its rows). Files can then be filtered, chosen and sent to a queue and a category.
/// </summary>
public sealed class GrabberRunViewModel : ObservableObject
{
    private const int BatchSize = 2000;

    private readonly GrabberService _service;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ConcurrentQueue<GrabberItem> _incoming = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _extensionCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListCollectionView _view;
    private string _filterText = "";
    private ExtensionFilter _extension;
    private int _statusFilter; // 0 all, then GrabberItemStatus + 1
    private int _selectedCount;
    private CategoryChoice _category;
    private DownloadQueue? _queue;
    private string _folder;
    private bool _sent;

    public GrabberRunViewModel(GrabberService service, GrabberRun run, IDialogService dialogs, IShellService shell, IReadOnlyList<Category> categories,
        IReadOnlyList<DownloadQueue> queues)
    {
        _service = service;
        _dialogs = dialogs;
        _shell = shell;
        Run = run;
        Project = run.Project;
        IsMirror = Project.Mode == GrabberMode.Mirror;

        Categories = [new CategoryChoice(null, Strings.BatchCategoryAuto), .. categories.Select(c => new CategoryChoice(c, c.DisplayName))];
        _category = Categories.FirstOrDefault(c => c.Category?.Id == Project.CategoryId) ?? Categories[0];
        Queues = queues;
        _queue = queues.FirstOrDefault(q => q.Id == Project.QueueId) ?? queues.FirstOrDefault(q => q.IsMain) ?? queues.FirstOrDefault();
        _folder = IsMirror ? "" : Project.Folder;

        Extensions = [new ExtensionFilter(null, Strings.BatchAllTypes)];
        _extension = Extensions[0];
        StatusFilters =
        [
            Strings.GrabberFilterAllStatuses, Strings.GrabberStatusNew, Strings.GrabberStatusChanged, Strings.GrabberStatusUnchanged,
            Strings.GrabberStatusRemoved, Strings.GrabberStatusFailed,
        ];

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        _view.Filter = o => Matches((GrabberItemViewModel)o);

        StopCommand = new RelayCommand(() => Run.Cancel(), () => IsRunning);
        SelectVisibleCommand = new RelayCommand(() => SetVisible(true), () => !IsMirror);
        ClearVisibleCommand = new RelayCommand(() => SetVisible(false), () => !IsMirror);
        SendCommand = new RelayCommand(p => Send(p is true or "true"), _ => !IsMirror && _selectedCount > 0 && FolderError is null && !_sent);
        OpenCopyCommand = new RelayCommand(OpenCopy, () => IsMirror && Run.Result?.StartPage is { } page && File.Exists(page));
        OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(Project.Folder), () => IsMirror && Directory.Exists(Project.Folder));
        BrowseCommand = new RelayCommand(Browse);

        // Rows found before the window opened, then the live ones.
        Run.ItemFound += _incoming.Enqueue;
        foreach (var item in Run.Items)
            _incoming.Enqueue(item);
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        Pump();
    }

    public GrabberRun Run { get; }
    public GrabberProject Project { get; }
    public bool IsMirror { get; }
    public bool IsFiles => !IsMirror;
    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.GrabberRunTitle, Project.Name);
    public string Heading => IsMirror ? Strings.GrabberRunHeadingMirror : Strings.GrabberRunHeadingFiles;

    public ObservableCollection<GrabberItemViewModel> Items { get; } = [];
    public ICollectionView View => _view;
    public ObservableCollection<ExtensionFilter> Extensions { get; }
    public IReadOnlyList<string> StatusFilters { get; }
    public IReadOnlyList<CategoryChoice> Categories { get; }
    public IReadOnlyList<DownloadQueue> Queues { get; }

    public ICommand StopCommand { get; }
    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearVisibleCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand OpenCopyCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BrowseCommand { get; }

    /// <summary>Raised when the files were sent and the window can close.</summary>
    public event Action? Done;

    public bool IsRunning => Run.Result is null;

    public string ProgressText { get; private set; } = "";
    public string CurrentText { get; private set; } = "";
    public string CountText { get; private set; } = "";
    public string ResultText { get; private set; } = "";
    public bool ShowResult => !IsRunning;

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value))
                Refilter();
        }
    }

    public ExtensionFilter Extension
    {
        get => _extension;
        set
        {
            if (value is not null && Set(ref _extension, value))
                Refilter();
        }
    }

    public int StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (Set(ref _statusFilter, value))
                Refilter();
        }
    }

    public CategoryChoice Category
    {
        get => _category;
        set
        {
            if (value is not null)
                Set(ref _category, value);
        }
    }

    public DownloadQueue? Queue { get => _queue; set => Set(ref _queue, value); }

    public string Folder
    {
        get => _folder;
        set
        {
            if (Set(ref _folder, value))
                OnPropertyChanged(nameof(FolderError));
        }
    }

    public string? FolderError => Folder.Trim().Length > 0 && !Path.IsPathFullyQualified(Folder.Trim()) ? Strings.ErrorFolderNotAbsolute : null;

    public void Stop()
    {
        _timer.Stop();
        Run.ItemFound -= _incoming.Enqueue;
    }

    /// <summary>Moves found rows into the table in batches, and refreshes progress and counters.</summary>
    private void Pump()
    {
        var added = 0;
        var newExtension = false;
        while (added < BatchSize && _incoming.TryDequeue(out var item))
        {
            if (!_known.Add(item.Url))
                continue;
            var row = new GrabberItemViewModel(item, !IsMirror, OnSelectionChanged);
            if (row.IsSelected)
                _selectedCount++;
            Items.Add(row);
            added++;
            var ext = item.Extension;
            if (ext.Length > 0)
            {
                _extensionCounts.TryGetValue(ext, out var count);
                _extensionCounts[ext] = count + 1;
                newExtension |= count == 0;
            }
        }
        if (newExtension)
        {
            var current = _extension.Extension;
            var keep = Extensions.Skip(1).Select(e => e.Extension).ToHashSet();
            foreach (var (ext, _) in _extensionCounts.OrderBy(e => e.Key))
            {
                if (!keep.Contains(ext))
                {
                    var index = 1;
                    while (index < Extensions.Count && string.CompareOrdinal(Extensions[index].Extension, ext) < 0)
                        index++;
                    Extensions.Insert(index, new ExtensionFilter(ext, $".{ext}"));
                }
            }
            _extension = Extensions.FirstOrDefault(e => e.Extension == current) ?? Extensions[0];
        }
        UpdateTexts();
        if (!IsRunning && _incoming.IsEmpty)
        {
            _timer.Interval = TimeSpan.FromSeconds(1); // finished: nothing more arrives
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void UpdateTexts()
    {
        var p = Run.Progress;
        ProgressText = string.Format(CultureInfo.CurrentCulture, IsMirror ? Strings.GrabberProgressMirror : Strings.GrabberProgressFiles,
            p.PagesVisited, p.FilesFound, Format.Bytes(p.Bytes), p.Errors, p.RobotsBlocked);
        CurrentText = p.CurrentUrl is { } url && IsRunning ? string.Format(CultureInfo.CurrentCulture, Strings.GrabberCurrent, url) : "";
        var visible = _view.Count;
        CountText = IsMirror
            ? string.Format(CultureInfo.CurrentCulture, Strings.GrabberCountMirror, Items.Count, visible)
            : string.Format(CultureInfo.CurrentCulture, Strings.BatchCount, _selectedCount, Items.Count, visible);
        ResultText = Run.Result is { } result
            ? (IsMirror ? Strings.GrabberDoneMirror : Strings.GrabberDoneFiles) + " " + GrabberService.Summary(result)
            : "";
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ShowResult));
    }

    private void OnSelectionChanged(GrabberItemViewModel row, bool selected)
    {
        _selectedCount += selected ? 1 : -1;
        UpdateTexts();
        CommandManager.InvalidateRequerySuggested();
    }

    private bool Matches(GrabberItemViewModel row)
    {
        if (_statusFilter > 0 && (int)row.Status != _statusFilter - 1)
            return false;
        if (_extension.Extension is { } ext && !string.Equals(row.Extension, ext, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_filterText.Length == 0)
            return true;
        foreach (var word in _filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!row.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase) && !row.Url.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !row.SourcePage.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private void Refilter()
    {
        _view.Refresh();
        UpdateTexts();
    }

    private void SetVisible(bool selected)
    {
        foreach (var row in _view.Cast<GrabberItemViewModel>())
        {
            if (row.CanSelect && row.IsSelected != selected)
            {
                row.SetSelected(selected);
                _selectedCount += selected ? 1 : -1;
            }
        }
        UpdateTexts();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Send(bool startNow)
    {
        var chosen = Items.Where(i => i.IsSelected).Select(i => i.Item).ToList();
        if (chosen.Count == 0 || FolderError is not null)
            return;
        try
        {
            var count = _service.Send(Project.Id, chosen, Queue?.Id, Category.Category?.Id, Folder, startNow);
            _sent = true;
            _dialogs.Inform(string.Format(CultureInfo.CurrentCulture, Strings.GrabberSent, count, Queue?.DisplayName ?? ""), Title);
            Done?.Invoke();
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError(ex.Message, Title);
        }
    }

    /// <summary>A file of the offline copy, given by its path inside the copy's folder (never outside it).</summary>
    public void OpenLocal(string relative)
    {
        var paths = new LocalPaths(Project.Folder);
        if (paths.IsInside(relative) && File.Exists(paths.FullPath(relative)))
            _shell.Reveal(paths.FullPath(relative), Project.Folder);
    }

    private void OpenCopy()
    {
        if (Run.Result?.StartPage is not { } page || !File.Exists(page))
            return;
        // Only a page is opened (in the browser); anything else is shown in Explorer, never run.
        if (LocalPaths.IsPageFile(page))
            _shell.OpenFile(page);
        else
            _shell.Reveal(page, Project.Folder);
    }

    private void Browse()
    {
        if (_dialogs.PickFolder(Strings.PickDestinationFolder, Directory.Exists(Folder) ? Folder : null) is { } picked)
            Folder = picked;
    }
}
