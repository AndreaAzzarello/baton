using System.Windows;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.TrySave())
            DialogResult = true;
    }
}
