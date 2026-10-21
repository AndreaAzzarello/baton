using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Baton.App.ViewModels;

namespace Baton.App.Views;

public partial class ProgressWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public ProgressWindow(ProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _timer.Tick += (_, _) => viewModel.Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
}
