using System.Windows;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

public partial class CategoriesWindow : Window
{
    private readonly CategoriesViewModel _vm;

    public CategoriesWindow(CategoriesViewModel viewModel)
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
