using System.Windows;
using System.Windows.Input;
using Baton.App.ViewModels;

namespace Baton.App.Views;

public partial class GrabberRunWindow : Window
{
    public GrabberRunWindow(GrabberRunViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        // An offline copy has nothing to choose: the tick column gives way to the local path, and vice versa.
        CheckColumn.Visibility = viewModel.IsMirror ? Visibility.Collapsed : Visibility.Visible;
        LocalColumn.Visibility = viewModel.IsMirror ? Visibility.Visible : Visibility.Collapsed;
        viewModel.Done += Close;
        Loaded += (_, _) => FilterBox.Focus();
    }

    public GrabberRunViewModel ViewModel { get; }

    /// <summary>Space ticks or unticks every highlighted row, so a range chosen with Shift or Ctrl is ticked at once.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || List.SelectedItems.Count == 0 || ViewModel.IsMirror)
            return;
        var rows = List.SelectedItems.OfType<GrabberItemViewModel>().Where(r => r.CanSelect).ToList();
        var tick = rows.Any(r => !r.IsSelected);
        foreach (var row in rows)
            row.IsSelected = tick;
        e.Handled = true;
    }

    /// <summary>Double click on a row of an offline copy opens the local file.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is GrabberItemViewModel { LocalPath: { } local } && ViewModel.IsMirror)
            ViewModel.OpenLocal(local);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
