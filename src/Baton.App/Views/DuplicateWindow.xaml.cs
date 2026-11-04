using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>Says why a download looks like one already there, and asks what to do.</summary>
public partial class DuplicateWindow : Window
{
    private readonly DuplicateViewModel _viewModel;

    public DuplicateWindow(DuplicateViewModel viewModel, bool standalone = false)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        if (standalone)
        {
            // Opened without a window of ours in front (a download that stopped in the tray): take the focus explicitly.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Activated += (_, _) => Topmost = false;
        }
    }

    private void Choose(DuplicateChoice choice)
    {
        _viewModel.Choice = choice;
        DialogResult = true;
    }

    private void OnAddAnyway(object sender, RoutedEventArgs e) => Choose(DuplicateChoice.AddNumbered);

    private void OnOverwrite(object sender, RoutedEventArgs e) => Choose(DuplicateChoice.Overwrite);

    private void OnOpen(object sender, RoutedEventArgs e) => Choose(DuplicateChoice.OpenExisting);

    private void OnSkip(object sender, RoutedEventArgs e) => Choose(DuplicateChoice.Skip);
}
