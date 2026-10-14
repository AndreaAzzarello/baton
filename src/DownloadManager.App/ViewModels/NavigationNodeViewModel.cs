using System.Collections.ObjectModel;
using System.Globalization;
using DownloadManager.App.Mvvm;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>A node of the side panel: a status filter, optionally narrowed to one category.</summary>
public sealed class NavigationNodeViewModel(string title, string glyph, StatusFilter status, long? categoryId, Action<NavigationNodeViewModel> onSelected)
    : ObservableObject
{
    private string _title = title;
    private int _count;
    private bool _isSelected;
    private bool _isExpanded;

    public StatusFilter Status { get; } = status;
    public long? CategoryId { get; } = categoryId;
    public string Glyph { get; } = glyph;
    public bool IsCategory => CategoryId is not null;
    public ObservableCollection<NavigationNodeViewModel> Children { get; } = [];

    public DownloadFilter Filter => new(Status, CategoryId);

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string CountText => _count == 0 ? "" : _count.ToString(CultureInfo.CurrentCulture);

    public string AccessibleName => string.Format(CultureInfo.CurrentCulture, Strings.NavigationAccessibleName, Title, Count);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
                onSelected(this);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }
}
