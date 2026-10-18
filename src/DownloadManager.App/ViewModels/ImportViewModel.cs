using System.Globalization;
using DownloadManager.App.Mvvm;
using DownloadManager.Core.Portable;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>One line of the import preview: the item, whether to import it, and why it is marked.</summary>
public sealed class ImportRow(ImportItem item) : ObservableObject
{
    public ImportItem Item { get; } = item;
    public bool CanInclude => Item.Problem is null;

    public bool Include
    {
        get => Item.Include && CanInclude;
        set
        {
            Item.Include = value;
            OnPropertyChanged();
        }
    }

    public string KindText => Item.Kind switch
    {
        ImportKind.Download => Strings.ImportKindDownload,
        ImportKind.Category => Strings.ImportKindCategory,
        ImportKind.Queue => Strings.ImportKindQueue,
        ImportKind.Project => Strings.ImportKindProject,
        _ => Strings.ImportKindSettings,
    };

    public string Note => Item.Problem ?? (Item.IsDuplicate ? Strings.ImportDuplicate : "");

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => string.Join(", ", new[] { KindText, Item.Name, Item.Detail, Note }.Where(t => t.Length > 0));
}

/// <summary>The preview of an import: the user picks the items and what to do with duplicates.</summary>
public sealed class ImportViewModel : ObservableObject
{
    private DuplicateMode _mode = DuplicateMode.Skip;

    public ImportViewModel(string fileName, IReadOnlyList<ImportItem> items)
    {
        Rows = items.Select(i => new ImportRow(i)).ToList();
        var duplicates = items.Count(i => i.IsDuplicate);
        var invalid = items.Count(i => i.Problem is not null);
        Summary = string.Format(CultureInfo.CurrentCulture, Strings.ImportSummary, fileName, items.Count, duplicates, invalid);
    }

    public IReadOnlyList<ImportRow> Rows { get; }
    public string Summary { get; }
    public DuplicateMode Mode => _mode;

    public bool ModeSkip { get => _mode == DuplicateMode.Skip; set { if (value) _mode = DuplicateMode.Skip; } }
    public bool ModeReplace { get => _mode == DuplicateMode.Replace; set { if (value) _mode = DuplicateMode.Replace; } }
    public bool ModeKeepBoth { get => _mode == DuplicateMode.KeepBoth; set { if (value) _mode = DuplicateMode.KeepBoth; } }
}
