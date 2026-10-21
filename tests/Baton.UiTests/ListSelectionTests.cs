using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Baton.App.Controls;

namespace Baton.UiTests;

/// <summary>
/// The lists' mouse and keyboard selection, on a real DataGrid in a window off screen that never takes the focus. Clicks
/// are raised as WPF input events on the cells; Ctrl and Shift are set in this test thread's own keyboard state
/// (SetKeyboardState), which no other program sees: nothing is typed or clicked on the desktop.
/// </summary>
public sealed class ListSelectionTests
{
    public sealed record Item(int Number)
    {
        public override string ToString() => $"item {Number}";
    }

    private static void Sta(Action<DataGrid, ObservableCollection<Item>> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var items = new ObservableCollection<Item>(Enumerable.Range(0, 20).Select(i => new Item(i)));
                var grid = new DataGrid
                {
                    ItemsSource = CollectionViewSource.GetDefaultView(items),
                    SelectionMode = DataGridSelectionMode.Extended,
                    SelectionUnit = DataGridSelectionUnit.FullRow,
                    IsReadOnly = true,
                    AutoGenerateColumns = false,
                    EnableRowVirtualization = false,
                };
                grid.Columns.Add(new DataGridTextColumn { Header = "N", Binding = new Binding(nameof(Item.Number)) });
                grid.Columns.Add(new DataGridTemplateColumn { Header = "✓", CellTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(CheckBox)) } });
                ListSelection.SetIsEnabled(grid, true);
                window = new Window
                {
                    Content = new DockPanel { Children = { grid } }, Width = 400, Height = 900, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                };
                window.Show();
                grid.UpdateLayout();
                test(grid, items);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Keys();
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    /// <summary>Ctrl and/or Shift held, for this thread only; Keys() releases them.</summary>
    private static void Keys(bool ctrl = false, bool shift = false)
    {
        var state = new byte[256];
        if (ctrl)
            state[0x11] = state[0xA2] = 0x80; // VK_CONTROL, VK_LCONTROL
        if (shift)
            state[0x10] = state[0xA0] = 0x80; // VK_SHIFT, VK_LSHIFT
        SetKeyboardState(state);
    }

    private static DataGridCell Cell(DataGrid grid, int index)
    {
        grid.ScrollIntoView(grid.Items[index]);
        grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
        return Descendant<DataGridCell>(row)!;
    }

    private static T? Descendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found || (found = Descendant<T>(child)!) is not null)
                return found;
        }
        return null;
    }

    /// <summary>A press and release as WPF raises them: tunnelling then bubbling, stopped once handled.</summary>
    private static void Click(UIElement target, MouseButton button = MouseButton.Left)
    {
        foreach (var (preview, bubble) in new[] { (Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent), (Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent) })
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = preview };
            target.RaiseEvent(down);
            if (!down.Handled)
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = bubble });
        }
        Mouse.Capture(null);
    }

    private static int[] Selected(DataGrid grid) => grid.SelectedItems.Cast<Item>().Select(i => i.Number).Order().ToArray();

    [Fact]
    public void Click_CtrlClick_ShiftClick_AndCtrlA_SelectAsInExplorer() => Sta((grid, _) =>
    {
        Click(Cell(grid, 2));
        Assert.Equal([2], Selected(grid));

        Keys(ctrl: true);
        Click(Cell(grid, 5));
        Click(Cell(grid, 7));
        Assert.Equal([2, 5, 7], Selected(grid));
        Click(Cell(grid, 5)); // Ctrl+click on a selected row takes it out
        Assert.Equal([2, 7], Selected(grid));

        Keys();
        Click(Cell(grid, 10));
        Keys(shift: true);
        Click(Cell(grid, 13)); // a range from the last plain click
        Assert.Equal([10, 11, 12, 13], Selected(grid));

        Keys();
        ApplicationCommands.SelectAll.Execute(null, grid); // what Ctrl+A runs in a DataGrid
        Assert.Equal(20, grid.SelectedItems.Count);
    });

    [Fact]
    public void PlainClick_InsideAMultipleSelection_NarrowsItOnRelease_SoTheSelectionCanBeDragged() => Sta((grid, _) =>
    {
        Keys(ctrl: true);
        foreach (var i in new[] { 1, 3, 4 })
            Click(Cell(grid, i));
        Keys();
        var cell = Cell(grid, 3);
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        cell.RaiseEvent(press);
        Assert.True(press.Handled);
        Assert.Equal([1, 3, 4], Selected(grid)); // still the whole selection while the button is down: a drag carries all of it
        cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        Assert.Equal([3], Selected(grid)); // released without a drag: this row alone
    });

    [Fact]
    public void TickBoxInARow_GetsItsClick_AlsoInsideAMultipleSelection() => Sta((grid, _) =>
    {
        Keys(ctrl: true);
        foreach (var i in new[] { 1, 3, 4 })
            Click(Cell(grid, i));
        Keys();
        Cell(grid, 3);
        var box = Descendant<CheckBox>((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(3))!;
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        box.RaiseEvent(press);
        Assert.False(press.Handled); // left to the tick box
        box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        Assert.Equal([1, 3, 4], Selected(grid)); // and the selection is not narrowed under it
    });

    [Fact]
    public void RightClick_SelectsAnUnselectedRow_AndKeepsAMultipleSelection() => Sta((grid, _) =>
    {
        Keys(ctrl: true);
        foreach (var i in new[] { 1, 2, 3 })
            Click(Cell(grid, i));
        Keys();
        Click(Cell(grid, 2), MouseButton.Right);
        Assert.Equal([1, 2, 3], Selected(grid)); // the menu acts on all three
        Click(Cell(grid, 8), MouseButton.Right);
        Assert.Equal([8], Selected(grid)); // a row outside the selection: it alone
    });

    [Fact]
    public void ClickOnTheEmptyPartOfTheList_ClearsTheSelection() => Sta((grid, items) =>
    {
        while (items.Count > 3)
            items.RemoveAt(items.Count - 1); // short list: the lower part of the grid is empty
        grid.UpdateLayout();
        Click(Cell(grid, 1));
        Assert.Equal([1], Selected(grid));
        var empty = Descendant<System.Windows.Controls.Primitives.DataGridRowsPresenter>(grid)!;
        Click(empty);
        Assert.Empty(Selected(grid));
    });

    [Fact]
    public void Selection_SurvivesAFilterAndARefresh_ForTheItemsStillShown() => Sta((grid, items) =>
    {
        Keys(ctrl: true);
        foreach (var i in new[] { 2, 4, 5 })
            Click(Cell(grid, i));
        Keys();
        var view = (ListCollectionView)grid.ItemsSource;
        view.Filter = o => ((Item)o).Number != 5; // 5 is hidden by the filter
        Drain();
        Assert.Equal([2, 4], Selected(grid));
        view.Refresh(); // the list updates (progress, search)
        Drain();
        Assert.Equal([2, 4], Selected(grid));
        items[2] = items[2] with { }; // an update of an unrelated row
        Drain();
        Assert.Contains(4, Selected(grid));
    });

    [Fact]
    public void MenuKey_AndShiftF10_OpenTheRowMenu_KeepingTheSelection() => Sta((grid, _) =>
    {
        var menu = new ContextMenu { Opacity = 0, Items = { new MenuItem { Header = "Apri" } } }; // never visible on the screen
        grid.RowStyle = new Style(typeof(DataGridRow)) { Setters = { new Setter(FrameworkElement.ContextMenuProperty, menu) } };
        grid.UpdateLayout();
        Keys(ctrl: true);
        Click(Cell(grid, 4));
        Click(Cell(grid, 6));
        Keys();
        var source = PresentationSource.FromVisual(grid)!;
        foreach (var (key, shift) in new[] { (Key.Apps, false), (Key.F10, true) })
        {
            Keys(shift: shift);
            var press = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            grid.RaiseEvent(press);
            Keys();
            Assert.True(press.Handled);
            Assert.True(menu.IsOpen);
            Assert.Equal(6, ((Item)((DataGridRow)menu.PlacementTarget).Item).Number); // the current row
            Assert.Equal([4, 6], Selected(grid));
            menu.IsOpen = false;
        }
    });

    [Fact]
    public void Drag_StartsOnlyPastTheSystemDistance() => Sta((_, _) =>
    {
        var x = SystemParameters.MinimumHorizontalDragDistance;
        var y = SystemParameters.MinimumVerticalDragDistance;
        Assert.False(ListSelection.IsDrag(new Vector(0, 0)));
        Assert.False(ListSelection.IsDrag(new Vector(x - 1, y - 1)));
        Assert.True(ListSelection.IsDrag(new Vector(x, 0)));
        Assert.True(ListSelection.IsDrag(new Vector(0, -y)));
    });

    private static void Drain() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
}
