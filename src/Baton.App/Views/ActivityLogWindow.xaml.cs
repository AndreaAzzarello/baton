using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>The activity log of the local API, MCP and the rules.</summary>
public partial class ActivityLogWindow : Window
{
    public ActivityLogWindow(ActivityLogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => List.Focus();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
