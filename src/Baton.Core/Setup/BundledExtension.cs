namespace Baton.Core.Setup;

/// <summary>
/// The browser extension shipped next to Baton.exe by the installer, the portable zip and Scoop: the unpacked Chromium build
/// in the "extension" folder (Load unpacked) and the Firefox package. A development build has neither: the release page has them.
/// </summary>
public static class BundledExtension
{
    public const string FolderName = "extension";
    public const string FirefoxPackageName = "baton-firefox.xpi";
    public const string ReleasesPage = "https://github.com/AndreaAzzarello/baton/releases/latest";

    /// <summary>The unpacked Chromium extension next to the program, or null when it is not there.</summary>
    public static string? Folder(string programFolder) =>
        File.Exists(Path.Combine(programFolder, FolderName, "manifest.json")) ? Path.Combine(programFolder, FolderName) : null;

    /// <summary>The Firefox package next to the program, or null when it is not there.</summary>
    public static string? FirefoxPackage(string programFolder) =>
        File.Exists(Path.Combine(programFolder, FirefoxPackageName)) ? Path.Combine(programFolder, FirefoxPackageName) : null;
}
