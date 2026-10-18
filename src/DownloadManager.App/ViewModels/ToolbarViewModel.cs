using System.Collections.ObjectModel;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>A toolbar item in the "Customize toolbar" list.</summary>
public sealed class ToolbarRow(string id, bool visible) : ObservableObject
{
    private bool _visible = visible;

    public string Id { get; } = id;
    public string Label => ToolbarViewModel.Label(Id);

    /// <summary>The gap that splits left and right cannot be removed, only moved.</summary>
    public bool CanHide => Id != ToolbarLayout.Spacer;

    public bool Visible { get => _visible; set => Set(ref _visible, value || !CanHide); }

    public override string ToString() => Label;
}

/// <summary>Which toolbar buttons are shown, in which order, with small or large icons, with or without text.</summary>
public sealed class ToolbarViewModel : ObservableObject
{
    private ToolbarRow? _selected;

    public ToolbarViewModel(string layout, bool smallIcons, bool showText)
    {
        SmallIcons = smallIcons;
        ShowText = showText;
        Load(layout);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected is not null && Rows.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _selected is not null && Rows.IndexOf(_selected) < Rows.Count - 1);
        AddSeparatorCommand = new RelayCommand(() =>
        {
            var row = new ToolbarRow(ToolbarLayout.Separator, true);
            Rows.Insert(_selected is null ? 0 : Rows.IndexOf(_selected) + 1, row);
            Selected = row;
        });
        ResetCommand = new RelayCommand(() =>
        {
            Load(ToolbarLayout.Default);
            SmallIcons = false;
            ShowText = true;
            OnPropertyChanged(nameof(SmallIcons));
            OnPropertyChanged(nameof(ShowText));
        });
    }

    public ObservableCollection<ToolbarRow> Rows { get; } = [];
    public ToolbarRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool SmallIcons { get; set; }
    public bool ShowText { get; set; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand AddSeparatorCommand { get; }
    public ICommand ResetCommand { get; }

    /// <summary>The layout to save: the visible items in their order.</summary>
    public string Layout => ToolbarLayout.Format(ToolbarLayout.Parse(ToolbarLayout.Format(Rows.Where(r => r.Visible).Select(r => r.Id))));

    public static string Label(string id) => id switch
    {
        "add" => Strings.ActionAdd,
        "start" => Strings.ActionStart,
        "pause" => Strings.ActionPause,
        "enqueue" => Strings.ActionEnqueue,
        "remove" => Strings.ActionRemove,
        "progress" => Strings.ActionProgress,
        "properties" => Strings.ActionProperties,
        "up" => Strings.ActionUp,
        "down" => Strings.ActionDown,
        "queue" => Strings.ToolbarQueue,
        "folder" => Strings.ActionFolder,
        "categories" => Strings.ActionCategories,
        "queues" => Strings.ActionQueues,
        "grabber" => Strings.ActionGrabber,
        "settings" => Strings.ActionSettings,
        ToolbarLayout.Separator => Strings.ToolbarSeparator,
        _ => Strings.ToolbarSpacer,
    };

    private void Load(string layout)
    {
        Rows.Clear();
        var shown = ToolbarLayout.Parse(layout);
        foreach (var id in shown)
            Rows.Add(new ToolbarRow(id, true));
        // Hidden buttons stay in the list, unticked, so they can be brought back.
        foreach (var id in ToolbarLayout.Buttons.Where(b => !shown.Contains(b)))
            Rows.Add(new ToolbarRow(id, false));
        Selected = null;
    }

    private void Move(int delta)
    {
        if (_selected is null)
            return;
        var index = Rows.IndexOf(_selected);
        var target = index + delta;
        if (target < 0 || target >= Rows.Count)
            return;
        Rows.Move(index, target);
    }
}
