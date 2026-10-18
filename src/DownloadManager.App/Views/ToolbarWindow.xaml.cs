using System.Windows;
using System.Windows.Input;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

/// <summary>"Customize toolbar": visible buttons, order, icon size and labels.</summary>
public partial class ToolbarWindow : Window
{
    private readonly ToolbarViewModel _vm;

    public ToolbarWindow(ToolbarViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
    }

    /// <summary>Space ticks or unticks the selected item; Alt+Up/Down moves it.</summary>
    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _vm.Selected is { } row)
        {
            row.Visible = !row.Visible;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            var command = e.SystemKey == Key.Up ? _vm.MoveUpCommand : _vm.MoveDownCommand;
            if (command.CanExecute(null))
                command.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
