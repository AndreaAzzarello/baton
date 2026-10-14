using System.Windows;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow(PropertiesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
