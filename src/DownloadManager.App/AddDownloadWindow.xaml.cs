using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DownloadManager.App;

public partial class AddDownloadWindow : Window
{
    public AddDownloadWindow(string? url, string folder, string? fileName = null, string? note = null)
    {
        InitializeComponent();
        UrlBox.Text = url ?? "";
        FolderBox.Text = folder;
        NameBox.Text = fileName ?? "";
        if (note is not null)
        {
            NoteText.Text = note;
            NoteText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            Activate(); // without an owner, take the focus explicitly
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
        Validate();
    }

    public string Url => UrlBox.Text.Trim();
    public string Folder => FolderBox.Text.Trim();
    public string? FileName => string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
    public bool StartNow { get; private set; }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

    private bool Validate()
    {
        string? error = null;
        if (Url.Length > 0 && !(Uri.TryCreate(Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            error = "Inserisci un indirizzo http:// o https:// valido.";
        else if (Folder.Length == 0 || !Path.IsPathFullyQualified(Folder))
            error = "Indica una cartella di destinazione completa.";

        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        var ok = error is null && Url.Length > 0;
        StartButton.IsEnabled = QueueButton.IsEnabled = ok;
        return ok;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Scegli la cartella di destinazione" };
        if (Directory.Exists(Folder))
            dialog.InitialDirectory = Folder;
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnStartNow(object sender, RoutedEventArgs e) => Finish(startNow: true);

    private void OnQueue(object sender, RoutedEventArgs e) => Finish(startNow: false);

    private void Finish(bool startNow)
    {
        if (!Validate())
            return;
        StartNow = startNow;
        DialogResult = true;
    }
}
