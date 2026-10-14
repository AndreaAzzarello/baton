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

    bool ShowSettings(SettingsViewModel viewModel);

    bool ShowCategories(CategoriesViewModel viewModel);

    void ShowProperties(PropertiesViewModel viewModel);

    /// <summary>Opens (or brings forward) the progress window of a download.</summary>
    void ShowProgress(DownloadItemViewModel item);

    bool Confirm(string message, string title);

    Answer Ask(string message, string title);

    void Inform(string message, string title);

    void ShowError(string message, string title);

    string? PickFolder(string title, string? initialFolder);
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
