using System.Windows;
using System.Windows.Input;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

public partial class GrabberProjectsWindow : Window
{
    private readonly GrabberProjectsViewModel _vm;

    public GrabberProjectsWindow(GrabberProjectsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) => List.Focus();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.RunCommand.CanExecute(null))
            _vm.RunCommand.Execute(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
