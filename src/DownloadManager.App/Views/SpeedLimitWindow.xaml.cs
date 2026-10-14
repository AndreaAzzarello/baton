using System.Globalization;
using System.Windows;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.Views;

/// <summary>Asks for a speed limit in KB/s (0 = none).</summary>
public partial class SpeedLimitWindow : Window
{
    public SpeedLimitWindow(long current)
    {
        InitializeComponent();
        ValueBox.Text = current.ToString(CultureInfo.CurrentCulture);
        Loaded += (_, _) =>
        {
            Activate();
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public long Value { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (long.TryParse(ValueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value is >= 0 and <= 10_000_000)
        {
            Value = value;
            DialogResult = true;
        }
        else
        {
            ErrorText.Text = Strings.ErrorSpeedLimit;
        }
    }
}
