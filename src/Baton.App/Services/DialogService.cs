using System.Windows;
using Baton.App.ViewModels;
using Baton.App.Views;
using Baton.Core;
using Baton.Core.Resources;
using Microsoft.Win32;

namespace Baton.App.Services;

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

    public bool ShowBatch(BatchViewModel viewModel)
    {
        // Like the add dialog for browser requests: it comes forward on its own, the main window stays where it is.
        var window = new BatchWindow(viewModel) { Topmost = true };
        window.Activated += (_, _) => window.Topmost = false; // once in front, it behaves like any window
        return window.ShowDialog() == true;
    }

    public bool ShowGrabberWizard(GrabberWizardViewModel viewModel, bool standalone)
    {
        var window = new GrabberWizardWindow(viewModel);
        if (standalone)
        {
            // Opened from the browser: it comes forward by itself, like the add dialog.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
            window.Topmost = true;
            window.Activated += (_, _) => window.Topmost = false;
        }
        else
        {
            window.Owner = Owner;
        }
        return window.ShowDialog() == true;
    }

    private GrabberProjectsWindow? _projects;
    private readonly Dictionary<long, GrabberRunWindow> _runs = [];

    public void ShowGrabberProjects(Func<GrabberProjectsViewModel> create)
    {
        if (_projects is not null)
        {
            Bring(_projects);
            return;
        }
        var viewModel = create();
        _projects = new GrabberProjectsWindow(viewModel) { Owner = Application.Current.MainWindow is { IsVisible: true } main ? main : null };
        _projects.Closed += (_, _) =>
        {
            viewModel.Detach();
            _projects = null;
        };
        _projects.Show();
    }

    public void ShowGrabberRun(Core.Grabber.GrabberRun run, Func<GrabberRunViewModel> create)
    {
        var projectId = run.Project.Id;
        if (_runs.TryGetValue(projectId, out var open))
        {
            // A new run of the same project replaces the finished one shown.
            if (open.ViewModel.Run == run)
            {
                Bring(open);
                return;
            }
            open.Close();
        }
        var viewModel = create();
        var window = new GrabberRunWindow(viewModel);
        window.Closed += (_, _) =>
        {
            viewModel.Stop();
            if (_runs.TryGetValue(projectId, out var current) && current == window)
                _runs.Remove(projectId);
        };
        _runs[projectId] = window;
        window.Show();
        window.Activate();
    }

    private static void Bring(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public bool ShowSettings(SettingsViewModel viewModel) => new SettingsWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    private UpdateWindow? _update;

    public void ShowUpdate(UpdateViewModel viewModel)
    {
        if (_update is not null)
        {
            Bring(_update);
            return;
        }
        // Also found at startup with the main window hidden: it comes forward on its own.
        _update = new UpdateWindow(viewModel) { Topmost = true };
        _update.Activated += (_, _) => _update.Topmost = false;
        _update.Closed += (_, _) => _update = null;
        _update.Show();
    }

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

    public (string Username, string Password, bool Remember)? AskLogin(Core.Net.LoginRequest request)
    {
        var window = new LoginWindow(request);
        return window.ShowDialog() == true ? (window.Username, window.Password, window.Remember) : null;
    }

    public Core.Security.CertificateDecision AskCertificate(Core.Security.CertificateQuestion question)
    {
        var window = new CertificateWindow(question);
        window.ShowDialog();
        return window.Decision;
    }

    public void ShowProperties(PropertiesViewModel viewModel)
    {
        new PropertiesWindow(viewModel) { Owner = Owner }.ShowDialog();
        viewModel.Detach();
    }

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

    public string? PickFile(string title, string filter, string? initialFile)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (initialFile is not null && System.IO.Path.GetDirectoryName(initialFile) is { Length: > 0 } folder && System.IO.Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
            dialog.FileName = System.IO.Path.GetFileName(initialFile);
        }
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true, AddExtension = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool ShowImport(ImportViewModel viewModel) => new ImportWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public bool ShowToolbar(ToolbarViewModel viewModel) => new ToolbarWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public Core.Setup.FirstRunChoices? ShowFirstRun(FirstRunViewModel viewModel)
    {
        var window = new FirstRunWindow(viewModel);
        return window.ShowDialog() == true ? window.Choices : null;
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
