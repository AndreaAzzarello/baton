using System.Windows;
using Baton.App.ViewModels;

namespace Baton.App.Views;

/// <summary>The first start of a new installation.</summary>
public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _vm;

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
    }

    public Core.Setup.FirstRunChoices? Choices { get; private set; }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        if (_vm.TryGetChoices() is not { } choices)
            return;
        Choices = choices;
        DialogResult = true;
    }
}
