using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>One link or image of the page in "download all".</summary>
public sealed class BatchItemViewModel(string url, string name, string extension, string text, bool isImage, bool selected) : ObservableObject
{
    private bool _isSelected = selected;

    public string Url { get; } = url;
    public string Name { get; } = name;

    /// <summary>Lower case, without the dot; empty when the address has none.</summary>
    public string Extension { get; } = extension;

    public string Text { get; } = text;
    public bool IsImage { get; } = isImage;
    public string KindText => IsImage ? Strings.BatchKindImage : Strings.BatchKindLink;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => $"{Name}, {KindText}, {(IsSelected ? Strings.BatchItemSelected : Strings.BatchItemNotSelected)}";
}

/// <summary>A choice in the category box: a category, or "automatic" (each file to its own type's category).</summary>
public sealed record CategoryChoice(Category? Category, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice in the type filter: every type, or one extension with how many items have it.</summary>
public sealed record ExtensionFilter(string? Extension, string Label);

/// <summary>"Download all": every link and image of a page (see <see cref="BatchLinks"/>), filtered by type and text.</summary>
public sealed class BatchViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly string _defaultFolder;
    private string _filterText = "";
    private ExtensionFilter _extension;
    private int _kind; // 0 all, 1 links, 2 images
    private CategoryChoice _category;
    private string _folder = "";
    private bool _folderChosen;

    public BatchViewModel(BatchRequest request, IDialogService dialogs, IReadOnlyList<Category> categories, string defaultFolder,
        IReadOnlyList<DownloadQueue> queues, long queueId)
    {
        _dialogs = dialogs;
        _defaultFolder = defaultFolder;
        Request = request;
        PageTitle = string.IsNullOrWhiteSpace(request.PageTitle) ? request.PageUrl : request.PageTitle.Trim();
        Items = new ObservableCollection<BatchItemViewModel>(BatchLinks.Build(request.Items)
            .Select(l => new BatchItemViewModel(l.Url, l.Name, l.Extension, l.Text, l.IsImage, l.Preselected)));
        foreach (var item in Items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BatchItemViewModel.IsSelected))
                    UpdateCounts();
            };

        Extensions = [new ExtensionFilter(null, Strings.BatchAllTypes), .. BatchLinks
            .Extensions(Items.Select(i => new BatchLink(i.Url, i.Name, i.Extension, i.Text, i.IsImage, false)))
            .Select(e => new ExtensionFilter(e.Extension, $".{e.Extension} ({e.Count})"))];
        _extension = Extensions[0];

        // "Automatic": each file goes to the category its type belongs to.
        Categories = [new CategoryChoice(null, Strings.BatchCategoryAuto), .. categories.Select(c => new CategoryChoice(c, c.DisplayName))];
        _category = Categories[0];
        Queues = queues;
        Queue = queues.FirstOrDefault(q => q.Id == queueId) ?? queues.FirstOrDefault(q => q.IsMain) ?? queues.FirstOrDefault();

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => Matches((BatchItemViewModel)o);

        SelectVisibleCommand = new RelayCommand(() => SetVisible(true));
        ClearVisibleCommand = new RelayCommand(() => SetVisible(false));
        BrowseCommand = new RelayCommand(Browse);
        UpdateCounts();
    }

    public BatchRequest Request { get; }
    public string PageTitle { get; }
    public ObservableCollection<BatchItemViewModel> Items { get; }
    public ICollectionView View { get; }
    public IReadOnlyList<ExtensionFilter> Extensions { get; }
    public IReadOnlyList<CategoryChoice> Categories { get; }
    public IReadOnlyList<DownloadQueue> Queues { get; }
    public DownloadQueue? Queue { get; set; }

    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearVisibleCommand { get; }
    public ICommand BrowseCommand { get; }

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

    /// <summary>0 = links and images, 1 = links only, 2 = images only.</summary>
    public int Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
                Refilter();
        }
    }

    /// <summary>The chosen category; its <see cref="CategoryChoice.Category"/> is null for automatic, by file type.</summary>
    public CategoryChoice Category
    {
        get => _category;
        set
        {
            if (value is null || !Set(ref _category, value))
                return;
            if (!_folderChosen)
            {
                _folder = value.Category?.ResolveFolder(_defaultFolder) ?? "";
                OnPropertyChanged(nameof(Folder));
            }
        }
    }

    /// <summary>Empty = the folder of each file's category.</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            if (Set(ref _folder, value))
                _folderChosen = value.Trim().Length > 0;
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(CanSubmit));
        }
    }

    public string CountText { get; private set; } = "";

    public string? Error => Folder.Trim().Length > 0 && !Path.IsPathFullyQualified(Folder.Trim()) ? Strings.ErrorFolderNotAbsolute : null;

    public bool CanSubmit => Error is null && Items.Any(i => i.IsSelected);

    /// <summary>Set by the window: start the queue right away, or only add to it.</summary>
    public bool StartNow { get; set; }

    public IReadOnlyList<BatchItemViewModel> Selected => Items.Where(i => i.IsSelected).ToList();

    private bool Matches(BatchItemViewModel item) =>
        BatchLinks.Matches(new BatchLink(item.Url, item.Name, item.Extension, item.Text, item.IsImage, false), _kind, _extension.Extension, _filterText);

    private void Refilter()
    {
        View.Refresh();
        UpdateCounts();
    }

    private void SetVisible(bool selected)
    {
        foreach (var item in View.Cast<BatchItemViewModel>())
            item.IsSelected = selected;
    }

    private void UpdateCounts()
    {
        var visible = View?.Cast<object>().Count() ?? Items.Count;
        CountText = string.Format(CultureInfo.CurrentCulture, Strings.BatchCount, Items.Count(i => i.IsSelected), Items.Count, visible);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanSubmit));
    }

    private void Browse()
    {
        if (_dialogs.PickFolder(Strings.PickDestinationFolder, Directory.Exists(Folder) ? Folder : null) is { } picked)
            Folder = picked;
    }
}
