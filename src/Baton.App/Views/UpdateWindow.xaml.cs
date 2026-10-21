using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>The notes of a newer version and the confirmation to install it.</summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow(UpdateViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Cancel();
    }
}
