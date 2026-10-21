namespace Baton.Core.Setup;

/// <summary>A browser found on this PC, with the page where its extensions are managed.</summary>
/// <param name="HostKey">Where the browser looks for the native host (Opera uses Chrome's).</param>
public sealed record DetectedBrowser(string Name, bool IsFirefox, string Executable, string ExtensionsPage, string HostKey);

/// <summary>Finds the installed browsers the extension supports, by their usual install locations.</summary>
public static class BrowserDetector
{
    public static IReadOnlyList<DetectedBrowser> Detect(Func<string, bool>? exists = null, Func<Environment.SpecialFolder, string>? folder = null)
    {
        exists ??= File.Exists;
        folder ??= Environment.GetFolderPath;
        var programs = folder(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = folder(Environment.SpecialFolder.ProgramFilesX86);
        var local = folder(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new (string Name, bool Firefox, string Page, string Key, string[] Paths)[]
        {
            ("Microsoft Edge", false, "edge://extensions", @"Software\Microsoft\Edge",
                [Path.Combine(programsX86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(programs, @"Microsoft\Edge\Application\msedge.exe")]),
            ("Google Chrome", false, "chrome://extensions", @"Software\Google\Chrome",
                [Path.Combine(programs, @"Google\Chrome\Application\chrome.exe"), Path.Combine(programsX86, @"Google\Chrome\Application\chrome.exe"),
                 Path.Combine(local, @"Google\Chrome\Application\chrome.exe")]),
            ("Brave", false, "brave://extensions", @"Software\BraveSoftware\Brave-Browser",
                [Path.Combine(programs, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe")]),
            ("Opera", false, "opera://extensions", @"Software\Google\Chrome",
                [Path.Combine(local, @"Programs\Opera\opera.exe"), Path.Combine(programs, @"Opera\opera.exe")]),
            ("Chromium", false, "chrome://extensions", @"Software\Chromium", [Path.Combine(local, @"Chromium\Application\chrome.exe")]),
            ("Mozilla Firefox", true, "about:debugging#/runtime/this-firefox", @"Software\Mozilla",
                [Path.Combine(programs, @"Mozilla Firefox\firefox.exe"), Path.Combine(programsX86, @"Mozilla Firefox\firefox.exe")]),
        };
        var result = new List<DetectedBrowser>();
        foreach (var (name, firefox, page, key, paths) in candidates)
        {
            if (paths.FirstOrDefault(exists) is { } exe)
                result.Add(new DetectedBrowser(name, firefox, exe, page, key));
        }
        return result;
    }
}

/// <summary>What the first-run wizard asks.</summary>
public sealed class FirstRunChoices
{
    public string DownloadFolder { get; set; } = KnownFolders.Downloads;

    /// <summary>Category id → its folder (relative to the download folder, or absolute).</summary>
    public Dictionary<long, string> CategoryFolders { get; set; } = [];

    public AppTheme Theme { get; set; } = AppTheme.System;
}

public static class FirstRun
{
    /// <summary>The wizard appears only for a new installation: no database yet.</summary>
    public static bool IsNeeded(string databasePath) => !File.Exists(databasePath);

    public static void Apply(DownloadService service, FirstRunChoices choices)
    {
        var settings = service.Settings;
        if (Path.IsPathFullyQualified(choices.DownloadFolder.Trim()))
            settings.DownloadFolder = choices.DownloadFolder.Trim();
        settings.Theme = choices.Theme;
        service.UpdateSettings(settings);
        foreach (var category in service.Categories)
        {
            if (choices.CategoryFolders.TryGetValue(category.Id, out var folder) && folder.Trim() != category.Folder)
            {
                var changed = category.Clone();
                changed.Folder = folder.Trim();
                service.SaveCategory(changed);
            }
        }
    }
}

/// <summary>One folder that moves away from the Desktop.</summary>
public sealed record FolderMove(long? CategoryId, string Name, string From, string To);

/// <summary>A finished file that would move with its folder.</summary>
public sealed record FileMove(long DownloadId, string From, string To);

public sealed record RelocationPlan(string? DownloadFolder, IReadOnlyList<FolderMove> Folders, IReadOnlyList<FileMove> Files)
{
    public bool IsEmpty => DownloadFolder is null && Folders.Count == 0;
}

public sealed record RelocationResult(int FilesMoved, IReadOnlyList<string> Problems);

/// <summary>
/// Installations made before 1.0 could keep downloads on the Desktop. This finds the download folder and the category
/// folders that are on the Desktop and plans their move to Downloads\DownloadManager, keeping their sub-folders. The
/// app asks once; the folders change only after confirmation, and finished files move only if the user asks for that too.
/// </summary>
public static class DesktopRelocation
{
    public static string DefaultTarget => Path.Combine(KnownFolders.Downloads, "Baton");

    public static IReadOnlyList<string> DesktopFolders()
    {
        var folders = new List<string>();
        foreach (var f in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path))
                folders.Add(path);
        }
        return folders;
    }

    public static bool IsUnder(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same place under <paramref name="target"/>: Desktop\Video becomes Downloads\DownloadManager\Video.</summary>
    private static string? MapPath(string path, IReadOnlyList<string> desktops, string target)
    {
        foreach (var desktop in desktops)
        {
            if (!IsUnder(path, desktop))
                continue;
            var relative = Path.GetRelativePath(Path.GetFullPath(desktop), Path.GetFullPath(path));
            return relative == "." ? target : Path.Combine(target, relative);
        }
        return null;
    }

    public static RelocationPlan Plan(AppSettings settings, IReadOnlyList<Category> categories, IReadOnlyList<DownloadRecord> downloads,
        IReadOnlyList<string> desktops, string target)
    {
        var newDefault = MapPath(settings.DownloadFolder, desktops, target);
        var folders = new List<FolderMove>();
        if (newDefault is not null)
            folders.Add(new FolderMove(null, "", settings.DownloadFolder, newDefault));
        foreach (var category in categories)
        {
            // Relative folders follow the download folder by themselves; absolute ones on the Desktop are moved.
            if (category.Folder.Length > 0 && Path.IsPathFullyQualified(category.Folder) && MapPath(category.Folder, desktops, target) is { } to)
                folders.Add(new FolderMove(category.Id, category.Name, category.Folder, to));
        }

        var files = new List<FileMove>();
        foreach (var d in downloads)
        {
            if (d.Status != DownloadStatus.Completed || d.FinalPath is not { } from || !File.Exists(from))
                continue;
            if (MapPath(d.Directory, desktops, target) is { } toFolder)
                files.Add(new FileMove(d.Id, from, Path.Combine(toFolder, Path.GetFileName(from))));
        }
        return new RelocationPlan(newDefault, folders, files);
    }
}
