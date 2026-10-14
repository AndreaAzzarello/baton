using System.Collections.ObjectModel;
using System.Globalization;
using DownloadManager.App.Mvvm;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>A node of the side panel: a status filter, optionally narrowed to one category.</summary>
public sealed class NavigationNodeViewModel(string title, string glyph, StatusFilter status, long? categoryId, Action<NavigationNodeViewModel> onSelected,
    long? queueId = null)
    : ObservableObject
{
    private string _title = title;
    private string _glyph = glyph;
    private string _detail = "";
    private int _count;
    private bool _isSelected;
    private bool _isExpanded;

    public StatusFilter Status { get; } = status;
    public long? CategoryId { get; } = categoryId;
    public long? QueueId { get; } = queueId;
    public bool IsCategory => CategoryId is not null;
    public bool IsQueue => QueueId is not null;

    /// <summary>True for the "Queues" heading, whose children are the queues.</summary>
    public bool IsQueuesRoot { get; init; }

    public string Glyph
    {
        get => _glyph;
        set => Set(ref _glyph, value);
    }

    /// <summary>Extra state shown next to the name, such as "scheduled at 22:00" for a queue.</summary>
    public string Detail
    {
        get => _detail;
        set
        {
            if (Set(ref _detail, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }
    public ObservableCollection<NavigationNodeViewModel> Children { get; } = [];

    public DownloadFilter Filter => new(Status, CategoryId, QueueId: QueueId);

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

    public string AccessibleName => string.Format(CultureInfo.CurrentCulture, Strings.NavigationAccessibleName, Title, Count)
                                    + (Detail.Length > 0 ? ", " + Detail : "");

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
