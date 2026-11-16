using System.Windows;
using System.Windows.Input;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>The automatic rules: the list in order, one rule's form and the preview.</summary>
public partial class RulesWindow : Window
{
    private readonly RulesViewModel _vm;

    public RulesWindow(RulesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) => List.Focus();
    }

    /// <summary>Alt+Up and Alt+Down move the selected rule, like the arrow buttons.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Up && _vm.MoveUpCommand.CanExecute(null))
            _vm.MoveUpCommand.Execute(null);
        else if (key == Key.Down && _vm.MoveDownCommand.CanExecute(null))
            _vm.MoveDownCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Validate())
            DialogResult = true;
    }
}
