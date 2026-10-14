using System.Windows;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

public partial class AddDownloadWindow : Window
{
    private readonly AddDownloadViewModel _vm;

    public AddDownloadWindow(AddDownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) =>
        {
            Activate(); // without an owner, take the focus explicitly
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    private void OnStartNow(object sender, RoutedEventArgs e) => Finish(startNow: true);

    private void OnQueue(object sender, RoutedEventArgs e) => Finish(startNow: false);

    private void Finish(bool startNow)
    {
        if (!_vm.CanSubmit)
            return;
        _vm.StartNow = startNow;
        DialogResult = true;
    }
}
