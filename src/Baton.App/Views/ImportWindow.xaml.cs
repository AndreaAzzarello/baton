using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>The preview of an import, before anything changes.</summary>
public partial class ImportWindow : Window
{
    public ImportWindow(ImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;
}
