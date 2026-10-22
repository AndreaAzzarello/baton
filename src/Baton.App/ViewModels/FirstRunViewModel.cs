using System.Globalization;
using System.IO;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Resources;
using Baton.Core.Setup;

namespace Baton.App.ViewModels;

/// <summary>A category's folder as the first-run wizard shows it.</summary>
public sealed class CategoryFolderRow(Category category)
{
    public long Id => category.Id;
    public string Name => category.DisplayName;

    /// <summary>Relative to the download folder, or a full path.</summary>
    public string Folder { get; set; } = category.Folder;

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => Name;
}

/// <summary>A browser found on this PC and how to add the extension to it (the folder and package are shown below).</summary>
public sealed class BrowserRow(DetectedBrowser browser, IShellService shell)
{
    public string Name => browser.Name;

    public string Instructions => string.Format(CultureInfo.CurrentCulture,
        browser.IsFirefox ? Strings.FirstRunFirefoxSteps : Strings.FirstRunChromiumSteps, browser.ExtensionsPage);

    /// <summary>Browsers do not let other programs open their internal pages: the address is copied to paste in the address bar.</summary>
    public string CopyPageName => string.Format(CultureInfo.CurrentCulture, Strings.FirstRunCopyPageName, Name);

    public ICommand CopyPageCommand { get; } = new RelayCommand(() => shell.SetClipboardText(browser.ExtensionsPage));

    public override string ToString() => Name;
}

/// <summary>
/// The first start of a new installation (no database yet): download folder (Downloads, never the Desktop), category
/// folders, the extension for each browser found with a connection check, start with Windows and theme.
/// </summary>
public sealed class FirstRunViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Func<DateTime?> _lastExtensionContact;
    private string _folder;
    private string? _connection;

    /// <param name="programFolder">The folder of Baton.exe, where the extension is shipped.</param>
    /// <param name="portableProgram">Baton.exe of a portable copy (its start entry would point there); null otherwise.</param>
    public FirstRunViewModel(IDialogService dialogs, IShellService shell, IReadOnlyList<Category> categories, IReadOnlyList<DetectedBrowser> browsers,
        string programFolder, Func<DateTime?> lastExtensionContact, string? portableProgram = null)
    {
        _dialogs = dialogs;
        _lastExtensionContact = lastExtensionContact;
        _folder = KnownFolders.Downloads;
        Categories = categories.Where(c => !c.IsGeneral).Select(c => new CategoryFolderRow(c)).ToList();
        Browsers = browsers.Select(b => new BrowserRow(b, shell)).ToList();
        Extension = new BundledExtensionViewModel(programFolder, shell);
        Startup = new StartWithWindowsViewModel(portableProgram, enabled: false); // never preselected: the user turns it on
        BrowseCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFolder(Strings.SettingsFolder, Directory.Exists(Folder) ? Folder : null) is { } picked)
                Folder = picked;
        });
        VerifyCommand = new RelayCommand(Verify);
    }

    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public IReadOnlyList<CategoryFolderRow> Categories { get; }
    public IReadOnlyList<BrowserRow> Browsers { get; }
    public bool NoBrowsers => Browsers.Count == 0;
    public BundledExtensionViewModel Extension { get; }

    public StartWithWindowsViewModel Startup { get; }
    public bool StartWithWindows { get => Startup.Enabled; set => Startup.Enabled = value; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool ThemeSystem { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }
    public bool ThemeLight { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
    public bool ThemeDark { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }
    public ICommand BrowseCommand { get; }
    public ICommand VerifyCommand { get; }
    public string? Connection { get => _connection; private set => Set(ref _connection, value); }

    private void Verify() =>
        Connection = _lastExtensionContact() is { } seen
            ? string.Format(CultureInfo.CurrentCulture, Strings.FirstRunConnected, seen.ToLocalTime().ToString("T", CultureInfo.CurrentCulture))
            : Strings.FirstRunNotConnected;

    /// <summary>The choices, or null with a message when the folder is not a full path.</summary>
    public FirstRunChoices? TryGetChoices()
    {
        if (!Path.IsPathFullyQualified(Folder.Trim()))
        {
            _dialogs.ShowError(Strings.ErrorFolderNotAbsolute, Strings.FirstRunTitle);
            return null;
        }
        return new FirstRunChoices
        {
            DownloadFolder = Folder.Trim(),
            CategoryFolders = Categories.ToDictionary(c => c.Id, c => c.Folder),
            Theme = Theme,
        };
    }
}
