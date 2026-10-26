using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>Confirms "Clear completed".</summary>
public partial class ClearCompletedWindow : Window
{
    public ClearCompletedWindow(ClearCompletedViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => (viewModel.HasScope ? OnlyScopeButton : (UIElement)RecycleBox).Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
