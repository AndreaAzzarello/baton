using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow(PropertiesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
