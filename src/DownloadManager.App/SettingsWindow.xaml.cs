using System.IO;
using System.Windows;
using DownloadManager.Core;
using Microsoft.Win32;

namespace DownloadManager.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        ConnectionsBox.Text = current.MaxConnections.ToString();
        ConcurrentBox.Text = current.MaxConcurrentDownloads.ToString();
        LimitBox.Text = current.SpeedLimitKBps.ToString();
        FolderBox.Text = current.DownloadFolder;
        ConfirmBrowserBox.IsChecked = current.ConfirmBrowserDownloads;
    }

    public AppSettings Result { get; private set; } = new();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Cartella predefinita dei download" };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (!int.TryParse(ConnectionsBox.Text, out var connections) || connections is < 1 or > 32)
            error = "Le connessioni per download devono essere un numero tra 1 e 32.";
        else if (!int.TryParse(ConcurrentBox.Text, out var concurrent) || concurrent is < 1 or > 20)
            error = "I download simultanei devono essere un numero tra 1 e 20.";
        else if (!long.TryParse(LimitBox.Text, out var limit) || limit < 0)
            error = "Il limite di velocità deve essere 0 o un numero positivo.";
        else if (!Path.IsPathFullyQualified(FolderBox.Text.Trim()))
            error = "Indica una cartella completa (es. C:\\Users\\…\\Downloads).";
        else
        {
            Result = new AppSettings
            {
                MaxConnections = connections,
                MaxConcurrentDownloads = concurrent,
                SpeedLimitKBps = limit,
                DownloadFolder = FolderBox.Text.Trim(),
                ConfirmBrowserDownloads = ConfirmBrowserBox.IsChecked == true,
            };
            DialogResult = true;
            return;
        }

        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
    }
}
