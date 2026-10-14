using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>One category being edited; nothing is saved until the dialog is confirmed.</summary>
public sealed class CategoryEditViewModel(Category category, string defaultFolder) : ObservableObject
{
    private string _name = category.Name;
    private string _extensions = Categories.FormatExtensions(category.Extensions);
    private string _folder = category.ResolveFolder(defaultFolder);
    private bool _folderEdited;

    public Category Original { get; } = category;
    public bool IsGeneral => Original.IsGeneral;
    public bool IsNew => Original.Id == 0;

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value) || !IsNew || _folderEdited)
                return;
            // A new category gets a subfolder named after it until the user picks a folder.
            _folder = Path.Combine(defaultFolder, FileNames.Sanitize(value.Trim()));
            OnPropertyChanged(nameof(Folder));
        }
    }

    public string Extensions
    {
        get => _extensions;
        set => Set(ref _extensions, value);
    }

    public string Folder
    {
        get => _folder;
        set
        {
            if (Set(ref _folder, value))
                _folderEdited = true;
        }
    }

    /// <summary>The edited category. A folder left as proposed stays relative, so it follows the default download folder.</summary>
    public Category ToCategory(string defaultFolder)
    {
        var result = Original.Clone();
        result.Name = Name.Trim();
        result.Extensions = Categories.ParseExtensions(Extensions);
        if (IsNew && !_folderEdited)
            result.Folder = FileNames.Sanitize(Name.Trim());
        else if (!string.Equals(Folder.Trim(), Original.ResolveFolder(defaultFolder), StringComparison.OrdinalIgnoreCase))
            result.Folder = Folder.Trim();
        return result;
    }

    public override string ToString() => Name;

    public bool IsChanged(string defaultFolder)
    {
        var edited = ToCategory(defaultFolder);
        return IsNew || edited.Name != Original.Name || edited.Folder != Original.Folder || !edited.Extensions.SequenceEqual(Original.Extensions);
    }
}

public sealed class CategoriesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly string _defaultFolder;
    private readonly List<Category> _deleted = [];
    private CategoryEditViewModel? _selected;
    private string? _error;

    public CategoriesViewModel(IDialogService dialogs, IReadOnlyList<Category> categories, string defaultFolder, long? selectId = null)
    {
        _dialogs = dialogs;
        _defaultFolder = defaultFolder;
        Items = new ObservableCollection<CategoryEditViewModel>(categories.Select(c => new CategoryEditViewModel(c, defaultFolder)));
        _selected = Items.FirstOrDefault(i => i.Original.Id == selectId) ?? Items.FirstOrDefault();
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, () => Selected is { IsGeneral: false });
        BrowseCommand = new RelayCommand(Browse, () => Selected is not null);
    }

    public ObservableCollection<CategoryEditViewModel> Items { get; }

    public CategoryEditViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BrowseCommand { get; }

    /// <summary>Categories to delete, then to save (new or changed), once the dialog is confirmed.</summary>
    public IReadOnlyList<Category> Deleted => _deleted;

    public IReadOnlyList<Category> Changed => Items.Where(i => i.IsChanged(_defaultFolder)).Select(i => i.ToCategory(_defaultFolder)).ToList();

    public bool Validate()
    {
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                Error = Strings.ErrorCategoryName;
            else if (Items.Count(i => string.Equals(i.Name.Trim(), item.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)) > 1)
                Error = string.Format(Strings.ErrorCategoryDuplicate, item.Name.Trim());
            else if (!string.IsNullOrWhiteSpace(item.Folder) && !Path.IsPathFullyQualified(item.Folder.Trim()))
                Error = string.Format(Strings.ErrorCategoryFolder, item.Name.Trim());
            else
                continue;
            Selected = item;
            return false;
        }
        Error = null;
        return true;
    }

    private void Add()
    {
        var category = new Category { Name = Strings.CategoryNewName, Folder = Strings.CategoryNewName };
        var item = new CategoryEditViewModel(category, _defaultFolder);
        Items.Add(item);
        Selected = item;
    }

    private void Delete()
    {
        if (Selected is not { IsGeneral: false } item)
            return;
        if (!item.IsNew && !_dialogs.Confirm(string.Format(Strings.ConfirmDeleteCategory, item.Name), Strings.CategoriesTitle))
            return;
        if (!item.IsNew)
            _deleted.Add(item.Original);
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    private void Browse()
    {
        if (Selected is { } item && _dialogs.PickFolder(Strings.PickDestinationFolder, Directory.Exists(item.Folder) ? item.Folder : null) is { } picked)
            item.Folder = picked;
    }
}
