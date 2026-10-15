using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Services;

public enum Answer
{
    Yes,
    No,
    Cancel,
}

/// <summary>Windows and message boxes, so view models never create views themselves.</summary>
public interface IDialogService
{
    /// <summary>
    /// Shows the add dialog. <paramref name="standalone"/> is for requests from the browser: the dialog comes
    /// forward on its own, above other windows, while the main window stays where it is.
    /// </summary>
    bool ShowAddDownload(AddDownloadViewModel viewModel, bool standalone);

    /// <summary>"Download all": the links and images of a page sent by the browser; true when the user chose some.</summary>
    bool ShowBatch(BatchViewModel viewModel);

    /// <summary>The grabber's project wizard; true when the project was completed (its <see cref="GrabberWizardViewModel.Result"/>).</summary>
    bool ShowGrabberWizard(GrabberWizardViewModel viewModel, bool standalone);

    /// <summary>The grabber's project list, one window at a time (brought forward if open).</summary>
    void ShowGrabberProjects(Func<GrabberProjectsViewModel> create);

    /// <summary>The preview of a project's run, one window per project (brought forward if open).</summary>
    void ShowGrabberRun(Core.Grabber.GrabberRun run, Func<GrabberRunViewModel> create);

    bool ShowSettings(SettingsViewModel viewModel);

    bool ShowCategories(CategoriesViewModel viewModel);

    bool ShowQueues(QueuesViewModel viewModel);

    /// <summary>The countdown before a queue's end action; true when it ran out or the user chose to go ahead.</summary>
    bool ShowCountdown(Core.EndAction action, string queueName, TimeSpan length);

    /// <summary>Asks for a speed limit in KB/s; null when cancelled.</summary>
    long? AskSpeedLimit(long current);

    /// <summary>Asks for the login a server or proxy wants; null when cancelled.</summary>
    (string Username, string Password, bool Remember)? AskLogin(Core.Net.LoginRequest request);

    /// <summary>Asks what to do with a certificate Windows does not trust; refusing is the default.</summary>
    Core.Security.CertificateDecision AskCertificate(Core.Security.CertificateQuestion question);

    void ShowProperties(PropertiesViewModel viewModel);

    /// <summary>Opens (or brings forward) the progress window of a download.</summary>
    void ShowProgress(DownloadItemViewModel item);

    bool Confirm(string message, string title);

    Answer Ask(string message, string title);

    void Inform(string message, string title);

    void ShowError(string message, string title);

    string? PickFolder(string title, string? initialFolder);

    /// <summary>Asks for an existing file, e.g. ffmpeg.exe; <paramref name="filter"/> uses the "Name|*.ext" format.</summary>
    string? PickFile(string title, string filter, string? initialFile);
}

/// <summary>Whether the app starts with Windows (in the notification area).</summary>
public interface IStartupService
{
    bool IsEnabled { get; }

    void Set(bool enabled);
}

public sealed class StartupService : IStartupService
{
    public bool IsEnabled => AppEnvironment.Registration.StartsWithWindows;

    public void Set(bool enabled) => AppEnvironment.Registration.SetStartWithWindows(enabled);
}

/// <summary>Explorer, default programs and the clipboard.</summary>
public interface IShellService
{
    void OpenFile(string path);

    /// <summary>Shows the file selected in Explorer, or the folder when the file is gone.</summary>
    void Reveal(string? path, string folder);

    void OpenFolder(string folder);

    string? GetClipboardText();

    void SetClipboardText(string text);
}
