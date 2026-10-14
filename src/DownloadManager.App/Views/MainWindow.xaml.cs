using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DownloadManager.App.ViewModels;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.Views;

/// <summary>
/// The main window. Behaviour lives in <see cref="MainViewModel"/>; this class only handles what is about the
/// view itself: column layout, drag and drop gestures and context menus.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Func<Task> _shutdown;
    private readonly Func<bool> _closeToTray;
    private readonly DispatcherTimer _saveLayout = new() { Interval = TimeSpan.FromSeconds(1) };
    private ColumnLayout _layout = new();
    private Point? _dragStart;
    private bool _closingDone;
    private bool _exiting;

    /// <param name="closeToTray">Whether closing the window only hides it (the app keeps running in the notification area).</param>
    public MainWindow(MainViewModel viewModel, Func<Task> shutdown, Func<bool> closeToTray)
    {
        InitializeComponent();
        _vm = viewModel;
        _shutdown = shutdown;
        _closeToTray = closeToTray;
        DataContext = viewModel;
        RowMenu = BuildRowMenu();
        NavigationMenu = BuildNavigationMenu();
        ColumnMenu = new ContextMenu();
        ColumnMenu.Opened += (_, _) => FillColumnMenu();

        ApplyColumnLayout(_vm.LoadColumnLayout(DefaultColumns()));
        foreach (var column in Grid.Columns)
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))
                .AddValueChanged(column, (_, _) => ScheduleLayoutSave());
        _saveLayout.Tick += (_, _) => SaveColumnLayout();
    }

    public ContextMenu RowMenu { get; }

    public ContextMenu NavigationMenu { get; }

    public ContextMenu ColumnMenu { get; }

    /// <summary>True while the window is saving downloads before closing; new requests must wait.</summary>
    public bool IsClosing { get; private set; }

    /// <summary>Raised the first time the window hides in the notification area instead of closing.</summary>
    public event Action? HiddenToTray;

    /// <summary>Closes the window and ends the app (menu "Exit", or a queue's "close app" action).</summary>
    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        // Raise above other windows without staying on top.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.UpdateSelection(Grid.SelectedItems.Cast<DownloadItemViewModel>());

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<DataGridRow>(source) is not null && _vm.DefaultActionCommand.CanExecute(null))
            _vm.DefaultActionCommand.Execute(null);
    }

    // Columns: order, width, visibility and sorting are saved between sessions.

    private IReadOnlyList<ColumnState> DefaultColumns() => Grid.Columns.Select((c, i) => new ColumnState
    {
        Key = c.SortMemberPath,
        DisplayIndex = i,
        Width = c.Width.IsStar ? 0 : c.Width.Value, // 0: keep the proportional width from the XAML
        Visible = c.Visibility == Visibility.Visible,
    }).ToList();

    private void ApplyColumnLayout(ColumnLayout layout)
    {
        _layout = layout;
        foreach (var state in layout.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (Column(state.Key) is not { } column)
                continue;
            column.Visibility = state.Visible || IsNameColumn(column) ? Visibility.Visible : Visibility.Collapsed;
            if (state.Width > 0)
                column.Width = new DataGridLength(state.Width);
            column.DisplayIndex = Math.Min(state.DisplayIndex, Grid.Columns.Count - 1);
        }
        ShowSortArrow();
    }

    private ColumnLayout CaptureColumnLayout() => new()
    {
        Columns = Grid.Columns.Select(c => new ColumnState
        {
            Key = c.SortMemberPath,
            DisplayIndex = c.DisplayIndex,
            // The declared width, not the rendered one: the grid may squeeze columns when the window is narrow.
            Width = c.Width.IsStar ? 0 : c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth,
            Visible = c.Visibility == Visibility.Visible,
        }).ToList(),
    };

    private void ScheduleLayoutSave()
    {
        _saveLayout.Stop();
        _saveLayout.Start();
    }

    private void SaveColumnLayout()
    {
        _saveLayout.Stop();
        _layout = CaptureColumnLayout();
        _vm.SaveColumnLayout(_layout);
    }

    private void OnColumnsChanged(object? sender, DataGridColumnEventArgs e) => ScheduleLayoutSave();

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // the view model sorts, so the order survives live updates and restarts
        _vm.SortBy(e.Column.SortMemberPath);
        ShowSortArrow();
        ScheduleLayoutSave();
    }

    private void ShowSortArrow()
    {
        foreach (var column in Grid.Columns)
            column.SortDirection = column.SortMemberPath == _vm.SortKey
                ? _vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
    }

    private void FillColumnMenu()
    {
        ColumnMenu.Items.Clear();
        foreach (var column in Grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            var item = new MenuItem
            {
                Header = column.Header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                IsEnabled = !IsNameColumn(column), // the name column always stays
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                ScheduleLayoutSave();
            };
            ColumnMenu.Items.Add(item);
        }
        ColumnMenu.Items.Add(new Separator());
        var queueOrder = new MenuItem { Header = Strings.MenuQueueOrder, IsCheckable = true, IsChecked = _vm.IsQueueOrder };
        queueOrder.Click += (_, _) =>
        {
            _vm.SortBy(null);
            ShowSortArrow();
            ScheduleLayoutSave();
        };
        ColumnMenu.Items.Add(queueOrder);
        var reset = new MenuItem { Header = Strings.MenuResetColumns };
        reset.Click += (_, _) => ResetColumns();
        ColumnMenu.Items.Add(reset);
    }

    private void ResetColumns()
    {
        // Rebuild from the XAML defaults: reload the window's column definitions.
        var defaults = ColumnLayout.Restore(null, DefaultsFromXaml);
        _vm.SortBy(null);
        foreach (var state in defaults.Columns)
        {
            if (Column(state.Key) is { } column)
                column.Width = DefaultWidths[state.Key];
        }
        ApplyColumnLayout(defaults);
        SaveColumnLayout();
    }

    private IReadOnlyList<ColumnState>? _defaultsFromXaml;
    private Dictionary<string, DataGridLength>? _defaultWidths;

    private IReadOnlyList<ColumnState> DefaultsFromXaml => _defaultsFromXaml!;

    private Dictionary<string, DataGridLength> DefaultWidths => _defaultWidths!;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        // Remember the layout declared in XAML before the saved one is applied, for "reset columns".
        _defaultsFromXaml = DefaultColumns();
        _defaultWidths = Grid.Columns.ToDictionary(c => c.SortMemberPath, c => c.Width);
    }

    private DataGridColumn? Column(string key) => Grid.Columns.FirstOrDefault(c => c.SortMemberPath == key);

    private static bool IsNameColumn(DataGridColumn column) => column.SortMemberPath == nameof(DownloadItemViewModel.Name);

    private ContextMenu BuildRowMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, ICommand command, string? gesture = null) =>
            menu.Items.Add(new MenuItem { Header = header, Command = command, InputGestureText = gesture ?? "" });

        Add(Strings.MenuOpen, _vm.OpenFileCommand);
        Add(Strings.MenuOpenFolder, _vm.OpenFolderCommand);
        menu.Items.Add(new Separator());
        Add(Strings.MenuStart, _vm.StartCommand);
        Add(Strings.MenuPause, _vm.PauseCommand);
        Add(Strings.MenuEnqueue, _vm.EnqueueCommand);
        menu.Items.Add(new Separator());
        Add(Strings.MenuProgress, _vm.ProgressCommand, Strings.GestureEnter);
        Add(Strings.MenuProperties, _vm.PropertiesCommand, Strings.GestureAltEnter);
        Add(Strings.MenuCopyUrl, _vm.CopyUrlCommand);
        var moveTo = new MenuItem { Header = Strings.MenuMoveToQueue };
        menu.Items.Add(moveTo);
        menu.Items.Add(new Separator());
        Add(Strings.MenuRemove, _vm.RemoveCommand, Strings.GestureDelete);
        menu.Opened += (_, _) =>
        {
            // One entry per queue; the queue all selected downloads are already in is checked.
            moveTo.Items.Clear();
            var ids = _vm.SelectedItems.Select(i => i.Id).ToList();
            foreach (var queue in _vm.Queues)
            {
                var item = new MenuItem
                {
                    Header = queue.Name,
                    IsCheckable = true,
                    IsChecked = _vm.SelectedItems.Count > 0 && _vm.SelectedItems.All(i => i.QueueId == queue.Id),
                };
                var target = queue.Id;
                item.Click += (_, _) => _vm.MoveToQueue(ids, target);
                moveTo.Items.Add(item);
            }
            moveTo.IsEnabled = ids.Count > 0;
        };
        return menu;
    }

    /// <summary>Side panel menu; the category entries act on the node that was right-clicked.</summary>
    private ContextMenu BuildNavigationMenu()
    {
        var menu = new ContextMenu();
        var startQueue = new MenuItem { Header = Strings.MenuStartQueue, Command = _vm.StartQueueCommand };
        var stopQueue = new MenuItem { Header = Strings.MenuStopQueue, Command = _vm.StopQueueCommand };
        var editQueue = new MenuItem { Header = Strings.MenuEditQueue, Command = _vm.QueuesCommand };
        var deleteQueue = new MenuItem { Header = Strings.MenuDeleteQueue, Command = _vm.DeleteQueueCommand };
        var newQueue = new MenuItem { Header = Strings.MenuNewQueue, Command = _vm.NewQueueCommand };
        var queueSeparator = new Separator();
        var add = new MenuItem { Header = Strings.MenuNewCategory, Command = _vm.NewCategoryCommand };
        var edit = new MenuItem { Header = Strings.MenuEditCategory, Command = _vm.CategoriesCommand };
        var delete = new MenuItem { Header = Strings.MenuDeleteCategory, Command = _vm.DeleteCategoryCommand };
        foreach (var item in new Control[] { startQueue, stopQueue, editQueue, deleteQueue, queueSeparator, newQueue, add, edit, delete })
            menu.Items.Add(item);
        menu.Opened += (_, _) =>
        {
            var node = (menu.PlacementTarget as FrameworkElement)?.DataContext as NavigationNodeViewModel;
            static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
            var isQueue = node?.IsQueue == true;
            var queueArea = isQueue || node?.IsQueuesRoot == true;
            startQueue.CommandParameter = stopQueue.CommandParameter = editQueue.CommandParameter = deleteQueue.CommandParameter = node?.QueueId;
            startQueue.Visibility = stopQueue.Visibility = editQueue.Visibility = deleteQueue.Visibility = Show(isQueue);
            newQueue.Visibility = Show(queueArea);
            queueSeparator.Visibility = Show(isQueue); // between the actions on this queue and "New queue"
            add.Visibility = Show(!queueArea);
            edit.CommandParameter = delete.CommandParameter = node?.CategoryId;
            edit.Visibility = delete.Visibility = Show(node?.IsCategory == true);
        };
        return menu;
    }

    // Drag and drop: links can be dropped in; completed files can be dragged out to Explorer or another program.

    /// <summary>Marks a drag that started in this window, so its files are not dropped back in.</summary>
    private const string InternalDragFormat = "DownloadManager.InternalDrag";

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(InternalDragFormat) || DroppedUrls(e.Data).Count == 0
            ? DragDropEffects.None
            : DragDropEffects.Copy | DragDropEffects.Link;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormat))
            return;
        var urls = DroppedUrls(e.Data);
        if (urls.Count == 0)
            return;
        e.Handled = true;
        Activate();
        // Let the drag source finish before a dialog opens.
        Dispatcher.InvokeAsync(() => _vm.AddLinks(urls), DispatcherPriority.Background);
    }

    /// <summary>Browsers offer a dragged link as an Internet URL, as text, or both.</summary>
    private static IReadOnlyList<string> DroppedUrls(IDataObject data)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text })
        {
            try
            {
                if (!data.GetDataPresent(format))
                    continue;
                var text = data.GetData(format) switch
                {
                    string s => s,
                    MemoryStream stream when format == "UniformResourceLocatorW" => System.Text.Encoding.Unicode.GetString(stream.ToArray()),
                    MemoryStream stream => System.Text.Encoding.Default.GetString(stream.ToArray()),
                    _ => null,
                };
                var urls = Urls.ExtractHttpUrls(text?.TrimEnd('\0'));
                if (urls.Count > 0)
                    return urls;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException)
            {
                // A format the source advertises but cannot deliver.
            }
        }
        return [];
    }

    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject source ? FindAncestor<DataGridRow>(source) : null;
        _dragStart = row is { IsSelected: true } ? e.GetPosition(Grid) : null;
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStart = null;
            return;
        }
        var delta = e.GetPosition(Grid) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStart = null;

        var files = _vm.SelectedItems
            .Where(i => i.IsCompleted && i.Snapshot.FinalPath is { } p && File.Exists(p))
            .Select(i => i.Snapshot.FinalPath!)
            .ToArray();
        var data = new DataObject();
        if (files.Length > 0)
            data.SetFileDropList([.. files]);
        // The ids as a plain string: other programs may read every format offered.
        data.SetData(InternalDragFormat, string.Join(",", _vm.SelectedItems.Select(i => i.Id)));
        DragDrop.DoDragDrop(Grid, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private static NavigationNodeViewModel? NodeAt(DragEventArgs e) =>
        e.OriginalSource is DependencyObject source && FindAncestor<TreeViewItem>(source) is { DataContext: NavigationNodeViewModel node } ? node : null;

    private static IReadOnlyList<long> DraggedIds(IDataObject data) =>
        data.GetData(InternalDragFormat) is string ids
            ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList()
            : [];

    /// <summary>Downloads dragged from the list onto a queue (moves them) or a category (re-files them).</summary>
    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(InternalDragFormat) && NodeAt(e) is { } node && MainViewModel.AcceptsDrop(node)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetDataPresent(InternalDragFormat) && NodeAt(e) is { } node)
            _vm.DropOnNode(node, DraggedIds(e.Data));
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingDone)
            return;

        // With the notification area icon, closing the window keeps the app (and its schedules) running.
        if (!_exiting && _closeToTray())
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }

        // Checkpoint running downloads before the process exits so they resume next time.
        e.Cancel = true;
        if (IsClosing)
            return;
        IsClosing = true;
        SaveColumnLayout();
        _vm.Stop();
        IsEnabled = false;
        Title = Strings.ClosingTitle;
        await _shutdown();
        _closingDone = true;
        // Shutdown may complete synchronously; Close() is not allowed inside the Closing event.
        _ = Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }
}
