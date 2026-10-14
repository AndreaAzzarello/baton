using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using DownloadManager.App.ViewModels;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.Views;

public partial class BatchWindow : Window
{
    private readonly BatchViewModel _vm;

    public BatchWindow(BatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) =>
        {
            Activate(); // opened from the browser: take the focus explicitly
            FilterBox.Focus();
        };
    }

    /// <summary>Space ticks or unticks every highlighted row, so a range chosen with Shift or Ctrl is ticked at once.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || List.SelectedItems.Count == 0)
            return;
        var rows = List.SelectedItems.OfType<BatchItemViewModel>().ToList();
        var tick = rows.Any(r => !r.IsSelected);
        foreach (var row in rows)
            row.IsSelected = tick;
        e.Handled = true;
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

/// <summary>"Selected" / "Not selected" for screen readers.</summary>
public sealed class SelectedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Strings.BatchItemSelected : Strings.BatchItemNotSelected;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
