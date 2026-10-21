using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

public partial class QueuesWindow : Window
{
    private readonly QueuesViewModel _vm;

    public QueuesWindow(QueuesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Validate())
            DialogResult = true;
    }
}
