using System.Globalization;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core.Resources;
using Baton.Core.Setup;

namespace Baton.App.ViewModels;

/// <summary>
/// The extension shipped next to Baton.exe, as the first-run wizard and the settings show it: the full path of the folder
/// to load in Chromium browsers, the Firefox package, or, in a development build without them, the release page.
/// </summary>
public sealed class BundledExtensionViewModel
{
    public BundledExtensionViewModel(string programFolder, IShellService shell)
    {
        Folder = BundledExtension.Folder(programFolder);
        FirefoxPackage = BundledExtension.FirefoxPackage(programFolder);
        OpenFolderCommand = new RelayCommand(() => shell.OpenFolder(Folder!), () => Folder is not null);
        CopyPathCommand = new RelayCommand(() => shell.SetClipboardText(Folder!), () => Folder is not null);
        OpenReleasePageCommand = new RelayCommand(() => shell.OpenFile(BundledExtension.ReleasesPage));
    }

    /// <summary>Full path of the unpacked extension; null when it is not next to the program.</summary>
    public string? Folder { get; }

    public string? FirefoxPackage { get; }

    public bool IsIncluded => Folder is not null;

    public bool IsMissing => Folder is null;

    public string FirefoxText => FirefoxPackage is { } package
        ? string.Format(CultureInfo.CurrentCulture, Strings.ExtensionFirefoxIncluded, package)
        : Strings.ExtensionFirefoxMissing;

    public ICommand OpenFolderCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand OpenReleasePageCommand { get; }
}
