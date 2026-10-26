using System.Globalization;
using Baton.App.Mvvm;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>
/// The confirmation of "Clear completed": how many downloads leave the list, whether their files go to the Recycle Bin
/// (off by default, with their total size), and, when the side panel shows a category or queue, only there or everywhere.
/// </summary>
public sealed class ClearCompletedViewModel : ObservableObject
{
    private readonly Func<long?, long?, IReadOnlyList<DownloadSnapshot>> _preview;
    private readonly long? _categoryId;
    private readonly long? _queueId;
    private bool _onlyScope = true;
    private bool _moveToRecycleBin;
    private IReadOnlyList<DownloadSnapshot> _items = [];

    /// <param name="preview">The downloads that would be removed for a category and queue (null = any).</param>
    /// <param name="scopeName">The category or queue selected in the side panel; null when it shows everything.</param>
    public ClearCompletedViewModel(Func<long?, long?, IReadOnlyList<DownloadSnapshot>> preview, long? categoryId, long? queueId, string? scopeName)
    {
        _preview = preview;
        _categoryId = categoryId;
        _queueId = queueId;
        ScopeName = scopeName;
        Refresh();
    }

    public string? ScopeName { get; }

    public bool HasScope => ScopeName is not null;

    public string OnlyScopeText => string.Format(CultureInfo.CurrentCulture, Strings.ClearCompletedOnlyScope, ScopeName);

    public bool OnlyScope
    {
        get => _onlyScope;
        set
        {
            if (!Set(ref _onlyScope, value))
                return;
            OnPropertyChanged(nameof(AllScope));
            Refresh();
        }
    }

    /// <summary>The second radio button: every completed download, whatever the side panel shows.</summary>
    public bool AllScope
    {
        get => !_onlyScope;
        set => OnlyScope = !value;
    }

    public bool MoveToRecycleBin
    {
        get => _moveToRecycleBin;
        set
        {
            if (Set(ref _moveToRecycleBin, value))
                OnPropertyChanged(nameof(Summary));
        }
    }

    public int Count => _items.Count;

    /// <summary>The downloads counted in <see cref="Summary"/>: only these are removed.</summary>
    public IReadOnlyList<long> Ids => _items.Select(i => i.Id).ToList();

    public bool CanConfirm => Count > 0;

    /// <summary>The category and queue to clear, as chosen.</summary>
    public (long? CategoryId, long? QueueId) Scope => HasScope && OnlyScope ? (_categoryId, _queueId) : (null, null);

    public string Summary => Count == 0 ? Strings.ClearCompletedNone
        : MoveToRecycleBin ? string.Format(CultureInfo.CurrentCulture, Strings.ClearCompletedSummaryRecycle, Count,
            Format.Bytes(_items.Where(i => i.FinalPath is not null).Sum(i => (double)(i.TotalSize ?? i.Downloaded))).Replace(' ', ' ')) // "9,8 KB" never split
        : string.Format(CultureInfo.CurrentCulture, Strings.ClearCompletedSummary, Count);

    private void Refresh()
    {
        var (category, queue) = Scope;
        _items = _preview(category, queue);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(Summary));
    }
}
