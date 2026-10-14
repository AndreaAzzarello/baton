using System.Windows;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views;
using DownloadManager.Core;
using DownloadManager.Core.Resources;
using Microsoft.Win32;

namespace DownloadManager.App.Services;

public sealed class DialogService(Func<DownloadService> service, IShellService shell) : IDialogService
{
    private readonly Dictionary<long, ProgressWindow> _progress = [];

    /// <summary>The window dialogs belong to: the active one, else the main window if it is visible.</summary>
    private static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? (Application.Current.MainWindow is { IsVisible: true } main ? main : null);

    public bool ShowAddDownload(AddDownloadViewModel viewModel, bool standalone)
    {
        var window = new AddDownloadWindow(viewModel);
        if (standalone)
        {
            // Like a browser's own save prompt: only this small window comes forward, the main window stays where it is.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
            window.Topmost = true;
        }
        else
        {
            window.Owner = Owner;
        }
        return window.ShowDialog() == true;
    }

    public bool ShowSettings(SettingsViewModel viewModel) => new SettingsWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public bool ShowCategories(CategoriesViewModel viewModel) => new CategoriesWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public bool ShowQueues(QueuesViewModel viewModel) => new QueuesWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public bool ShowCountdown(EndAction action, string queueName, TimeSpan length)
    {
        // Topmost and without an owner: it must be seen even when the main window is hidden in the notification area.
        var window = new CountdownWindow(action, queueName, length);
        window.ShowDialog();
        return window.Confirmed;
    }

    public long? AskSpeedLimit(long current)
    {
        var window = new SpeedLimitWindow(current);
        return window.ShowDialog() == true ? window.Value : null;
    }

    public void ShowProperties(PropertiesViewModel viewModel) => new PropertiesWindow(viewModel) { Owner = Owner }.ShowDialog();

    public void ShowProgress(DownloadItemViewModel item)
    {
        if (_progress.TryGetValue(item.Id, out var open))
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new ProgressWindow(new ProgressViewModel(item, service(), shell)) { Owner = Application.Current.MainWindow };
        window.Closed += (_, _) => _progress.Remove(item.Id);
        _progress[item.Id] = window;
        window.Show();
    }

    public bool Confirm(string message, string title) =>
        Show(message, title, MessageKind.Question, [(Strings.Ok, Answer.Yes), (Strings.Cancel, Answer.Cancel)], Answer.Yes) == Answer.Yes;

    public Answer Ask(string message, string title) =>
        Show(message, title, MessageKind.Question, [(Strings.Yes, Answer.Yes), (Strings.No, Answer.No), (Strings.Cancel, Answer.Cancel)], Answer.Cancel);

    public void Inform(string message, string title) => Show(message, title, MessageKind.Information, [(Strings.Ok, Answer.Yes)], Answer.Yes);

    public void ShowError(string message, string title) => Show(message, title, MessageKind.Error, [(Strings.Ok, Answer.Yes)], Answer.Yes);

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initialFolder is not null)
            dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    private static Answer Show(string message, string title, MessageKind kind, IReadOnlyList<(string, Answer)> buttons, Answer defaultAnswer)
    {
        var owner = Owner;
        var window = new MessageWindow(message, title, kind, buttons, defaultAnswer) { Owner = owner };
        if (owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true ? window.Answer : Answer.Cancel;
    }
}
