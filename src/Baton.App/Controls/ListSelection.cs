using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Baton.App.Controls;

/// <summary>
/// The mouse in a multi-selection list, as in Explorer. The grid's own gestures stay (click, Ctrl+click, Shift+click,
/// Ctrl+A); added here: a click on an empty part clears the selection; a plain click on a row of a multiple selection
/// narrows it only on release, so the whole selection can be dragged; dragging starts only past the system's distance
/// and never from a fresh click; a right click selects the row unless it is already selected (the menu then acts on the
/// whole selection), which also opens with the Menu key or Shift+F10 on the current row. (The grid itself keeps the selected
/// items still shown through filters and refreshes: see the tests.)
/// </summary>
public static class ListSelection
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ListSelection), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>What a drag of the selection carries out of the list; null (the default) or a null result: no drag.</summary>
    public static readonly DependencyProperty DragDataProperty = DependencyProperty.RegisterAttached(
        "DragData", typeof(Func<IDataObject?>), typeof(ListSelection));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ListSelection));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);
    public static Func<IDataObject?>? GetDragData(DependencyObject o) => (Func<IDataObject?>?)o.GetValue(DragDataProperty);
    public static void SetDragData(DependencyObject o, Func<IDataObject?>? value) => o.SetValue(DragDataProperty, value);

    /// <summary>A drag begins only past the system's minimum distance on either axis.</summary>
    public static bool IsDrag(Vector moved) =>
        Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private sealed class State
    {
        public Point? DragStart;
        public object? NarrowTo; // the row clicked inside a multiple selection, selected alone on release
    }

    private static void OnEnabledChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not DataGrid grid || e.NewValue is not true)
            return;
        grid.SetValue(StateProperty, new State());
        grid.PreviewMouseLeftButtonDown += OnLeftDown;
        grid.PreviewMouseLeftButtonUp += OnLeftUp;
        grid.PreviewMouseMove += OnMove;
        grid.PreviewMouseRightButtonDown += OnRightDown;
        grid.PreviewKeyDown += OnKey;
    }

    /// <summary>The Menu key or Shift+F10: the menu of the current row (or the first selected one), under that row.</summary>
    private static void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Apps && !(key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            return;
        var grid = (DataGrid)sender;
        var item = grid.CurrentItem is { } current && grid.SelectedItems.Contains(current) ? current : grid.SelectedItem;
        if (item is null || grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow { ContextMenu: { } menu } row)
            return;
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static State Of(DataGrid grid) => (State)grid.GetValue(StateProperty);

    private static bool Ctrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;
    private static bool Shift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private static void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        var state = Of(grid);
        state.DragStart = null;
        state.NarrowTo = null;
        var source = e.OriginalSource as DependencyObject;
        if (Find<ScrollBar>(source) is not null || Find<DataGridColumnHeader>(source) is not null || Find<ButtonBase>(source) is not null)
            return; // tick boxes and buttons in the rows handle their own clicks
        if (Find<DataGridRow>(source) is not { } row)
        {
            // The empty part of the list.
            if (!Ctrl && !Shift)
                grid.UnselectAll();
            return;
        }
        if (Ctrl || Shift || !row.IsSelected)
            return; // the grid's own selection gestures
        state.DragStart = e.GetPosition(grid);
        if (grid.SelectedItems.Count > 1)
        {
            // Keep the selection for a drag; a click without a drag selects this row alone on release.
            state.NarrowTo = row.Item;
            row.Focus();
            e.Handled = true;
        }
    }

    private static void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        var state = Of(grid);
        if (state.NarrowTo is { } item && grid.Items.Contains(item))
            SelectOnly(grid, item);
        state.NarrowTo = null;
        state.DragStart = null;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var grid = (DataGrid)sender;
        var state = Of(grid);
        if (state.DragStart is not { } start)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            state.DragStart = null;
            return;
        }
        if (!IsDrag(e.GetPosition(grid) - start))
            return;
        state.DragStart = null;
        state.NarrowTo = null;
        if (GetDragData(grid)?.Invoke() is { } data)
            DragDrop.DoDragDrop(grid, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private static void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (Find<DataGridRow>(e.OriginalSource as DependencyObject) is { IsSelected: false } row)
            SelectOnly(grid, row.Item);
    }

    /// <summary>One row selected, also as the current cell: keyboard and Shift+click continue from it.</summary>
    public static void SelectOnly(DataGrid grid, object item)
    {
        grid.SelectedItems.Clear();
        grid.SelectedItem = item;
        if (grid.Columns.Count > 0)
            grid.CurrentCell = new DataGridCellInfo(item, grid.Columns.OrderBy(c => c.DisplayIndex).First());
    }

    private static T? Find<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
