using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Baton.App.ViewModels;

namespace Baton.App.Views;

public partial class GrabberWizardWindow : Window
{
    private readonly GrabberWizardViewModel _vm;

    public GrabberWizardWindow(GrabberWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) =>
        {
            Activate();
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
        // The first field of each step gets the focus, so the keyboard can carry on.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GrabberWizardViewModel.Step))
                return;
            Dispatcher.InvokeAsync(() =>
            {
                UIElement first = viewModel.Step switch
                {
                    0 => UrlBox,
                    1 => DepthBox,
                    2 => ExtBox,
                    3 => PagesBox,
                    _ => FolderBox,
                };
                first.Focus();
                RunButton.IsDefault = viewModel.IsLastStep;
            });
        };
    }

    private void OnSave(object sender, RoutedEventArgs e) => Finish(runNow: false);

    private void OnSaveAndRun(object sender, RoutedEventArgs e) => Finish(runNow: true);

    private void Finish(bool runNow)
    {
        if (!_vm.Finish())
            return;
        _vm.RunNow = runNow;
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Alt+Left / Alt+Right move between steps, as in a browser.
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        {
            var command = e.SystemKey == Key.Left ? _vm.BackCommand : _vm.NextCommand;
            if (command.CanExecute(null))
                command.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}

/// <summary>Visible when the bound value equals the parameter (a step number, or True/False).</summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
