using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DownloadManager.Core;
using DownloadManager.Core.Integration;

namespace DownloadManager.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastTick;
    private bool _closingDone;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    private static DownloadService Service => App.Service;

    private IEnumerable<DownloadItemViewModel> Selected => Grid.SelectedItems.Cast<DownloadItemViewModel>().ToList();

    private void Refresh()
    {
        var now = _clock.Elapsed;
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        var snapshots = Service.GetSnapshots();
        var byId = Items.ToDictionary(i => i.Id);
        var keep = snapshots.Select(s => s.Id).ToHashSet();
        foreach (var gone in Items.Where(i => !keep.Contains(i.Id)).ToList())
            Items.Remove(gone);

        for (var index = 0; index < snapshots.Count; index++)
        {
            var snap = snapshots[index];
            if (byId.TryGetValue(snap.Id, out var vm))
            {
                vm.Update(snap, elapsed);
                var current = Items.IndexOf(vm);
                if (current != index)
                    Items.Move(current, index);
            }
            else
            {
                Items.Insert(index, new DownloadItemViewModel(snap));
            }
        }

        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var running = Service.QueueRunning;
        QueueGlyph.Text = running ? "" : "";
        QueueLabel.Text = running ? "Ferma coda" : "Avvia coda";
        QueueButton.ToolTip = running ? "Ferma la coda: i download avviati dalla coda tornano in attesa" : "Avvia la coda dei download";

        var active = Items.Count(i => i.IsActive);
        var queued = Items.Count(i => i.Status == DownloadStatus.Queued);
        QueueStatus.Text = $"Coda {(running ? "attiva" : "ferma")}  ·  {active} in download  ·  {queued} in attesa  ·  {Items.Count} totali";
        var limit = Service.Settings.SpeedLimitKBps;
        SpeedStatus.Text = $"{Format.Speed(Items.Sum(i => i.Speed))}{(limit > 0 ? $"   (limite {limit} KB/s)" : "")}";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = Selected.ToList();
        StartButton.IsEnabled = selected.Any(i => i.Status is not (DownloadStatus.Completed or DownloadStatus.Downloading));
        PauseButton.IsEnabled = selected.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        EnqueueButton.IsEnabled = selected.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed);
        RemoveButton.IsEnabled = selected.Count > 0;
        UpButton.IsEnabled = DownButton.IsEnabled = selected.Count == 1;
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void OnAdd(object sender, RoutedEventArgs e) => ShowAddDialog(ClipboardUrl());

    private void OnAddCommand(object sender, ExecutedRoutedEventArgs e) => ShowAddDialog(ClipboardUrl());

    private void OnPasteCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (ClipboardUrl() is { } url)
            ShowAddDialog(url);
    }

    private void ShowAddDialog(string? url)
    {
        var dialog = new AddDownloadWindow(url, Service.Settings.DownloadFolder) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        Service.Add(dialog.Url, dialog.Folder, dialog.FileName, dialog.StartNow);
        Refresh();
    }

    private readonly Queue<BrowserDownload> _fromBrowser = new();
    private bool _showingBrowserDialogs;

    /// <summary>A download sent by the browser extension: confirmed in the add dialog, or started directly.</summary>
    public void ReceiveFromBrowser(BrowserDownload download)
    {
        _fromBrowser.Enqueue(download);
        // Requests that arrive while a dialog is open wait for it to close.
        if (_showingBrowserDialogs || !IsEnabled) // not enabled while closing
            return;
        _showingBrowserDialogs = true;
        try
        {
            while (_fromBrowser.TryDequeue(out var next))
            {
                var fileName = string.IsNullOrWhiteSpace(next.FileName) ? null : Path.GetFileName(next.FileName);
                if (!Service.Settings.ConfirmBrowserDownloads)
                {
                    Service.Add(next.Url, null, fileName, startNow: true, next.ToContext());
                    continue;
                }

                var note = next.Cookies is { Count: > 0 }
                    ? "Inviato dal browser: verranno usati i cookie della sessione e la pagina di provenienza."
                    : "Inviato dal browser.";
                // Like a browser's own save prompt: only this small window comes forward, the main window stays where it is.
                var dialog = new AddDownloadWindow(next.Url, Service.Settings.DownloadFolder, fileName, note)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = true,
                    Topmost = true,
                };
                if (dialog.ShowDialog() == true)
                    Service.Add(dialog.Url, dialog.Folder, dialog.FileName, dialog.StartNow, next.ToContext());
            }
        }
        finally
        {
            _showingBrowserDialogs = false;
        }
        Refresh();
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        // Raise above other windows without staying on top.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static string? ClipboardUrl()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
            return text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? text
                : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // clipboard busy
        }
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            Service.Start(item.Id);
        Refresh();
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(Selected.Select(i => Service.PauseAsync(i.Id)));
        Refresh();
    }

    private void OnEnqueue(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            Service.Enqueue(item.Id);
        Refresh();
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        var selected = Selected.ToList();
        if (selected.Count == 0)
            return;

        var anyCompleted = selected.Any(i => i.Status == DownloadStatus.Completed);
        var deleteFiles = false;
        if (anyCompleted)
        {
            var answer = MessageBox.Show(this,
                "Eliminare dal disco anche i file già scaricati?\n\nSì: elimina file ed elemento\nNo: rimuovi solo dall'elenco",
                "Rimuovi download", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel)
                return;
            deleteFiles = answer == MessageBoxResult.Yes;
        }
        else if (MessageBox.Show(this, $"Rimuovere {selected.Count} download dall'elenco? I dati parziali verranno eliminati.",
                     "Rimuovi download", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await Task.WhenAll(selected.Select(i => Service.RemoveAsync(i.Id, deleteFiles)));
        Refresh();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Grid.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            OnRemove(sender, e);
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (Selected.FirstOrDefault() is not { } item)
            return;
        Service.Move(item.Id, delta);
        Refresh();
        Grid.SelectedItem = item;
    }

    private async void OnToggleQueue(object sender, RoutedEventArgs e)
    {
        if (Service.QueueRunning)
            await Service.StopQueueAsync();
        else
            Service.StartQueue();
        Refresh();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(Service.Settings) { Owner = this };
        if (dialog.ShowDialog() == true)
            Service.UpdateSettings(dialog.Result);
        Refresh();
    }

    private void OnOpenDownloadFolder(object sender, RoutedEventArgs e)
    {
        var folder = Service.Settings.DownloadFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item)
            RevealInExplorer(item.Snapshot);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not DownloadItemViewModel item)
            return;
        if (item.Status == DownloadStatus.Completed)
            RevealInExplorer(item.Snapshot);
        else if (!item.IsActive)
            Service.Start(item.Id);
    }

    private static void RevealInExplorer(DownloadSnapshot snapshot)
    {
        var args = snapshot.FinalPath is { } path && File.Exists(path)
            ? $"/select,\"{path}\""
            : $"\"{snapshot.Directory}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        var urls = string.Join(Environment.NewLine, Selected.Select(i => i.Url));
        if (urls.Length > 0)
            Clipboard.SetText(urls);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingDone)
            return;

        // Checkpoint running downloads before the process exits so they resume next time.
        e.Cancel = true;
        _timer.Stop();
        IsEnabled = false;
        Title = "DownloadManager — salvataggio in corso…";
        await Service.ShutdownAsync();
        _closingDone = true;
        // Shutdown may complete synchronously; Close() is not allowed inside the Closing event.
        _ = Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }
}
