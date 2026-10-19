using System.Globalization;
using DownloadManager.Core.Grabber;
using DownloadManager.Core.Media;
using DownloadManager.Core.PostDownload;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core.Portable;

public enum ImportKind
{
    Download,
    Category,
    Queue,
    Project,
    Settings,

    /// <summary>Proxy, PAC file and the confirmation of browser downloads: a separate choice with its own warning.</summary>
    NetworkSettings,
}

/// <summary>What to do with an item that already exists (same address, or same name).</summary>
public enum DuplicateMode
{
    Skip,

    /// <summary>Categories, queues and projects take the imported values; downloads are skipped (never replaced).</summary>
    Replace,

    /// <summary>Added as well (a category, queue or project under a new name).</summary>
    KeepBoth,
}

/// <summary>One line of the import preview.</summary>
public sealed class ImportItem(ImportKind kind, string name, string detail, object source)
{
    public ImportKind Kind { get; } = kind;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    internal object Source { get; } = source;

    /// <summary>Something with the same address or name is already in the app.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Why the item cannot be imported (an invalid address or folder); such items are never imported.</summary>
    public string? Problem { get; init; }

    /// <summary>What importing it would change that the user must know (shown prominently), or null.</summary>
    public string? Warning { get; init; }

    public bool Include { get; set; } = true;
}

public sealed record ImportResult(int Added, int Replaced, int Skipped);

/// <summary>Builds the preview of an import (duplicates and invalid items marked) and applies what the user kept.</summary>
public static class PortableImport
{
    public static List<ImportItem> Plan(PortableFile file, IReadOnlyList<DownloadSnapshot> downloads, IReadOnlyList<Category> categories,
        IReadOnlyList<DownloadQueue> queues, IReadOnlyList<GrabberProject> projects)
    {
        var items = new List<ImportItem>();
        var known = downloads.Select(d => Urls.Display(d.Url)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in file.Downloads ?? [])
        {
            var problem = CheckDownload(d);
            var url = problem is null && Uri.TryCreate(d.Url, UriKind.Absolute, out var parsed) ? Urls.Display(parsed.AbsoluteUri) : d.Url ?? "";
            var duplicate = known.Contains(url) || !seen.Add(url);
            items.Add(new ImportItem(ImportKind.Download, string.IsNullOrWhiteSpace(d.FileName) ? url : d.FileName!, url, d)
            {
                IsDuplicate = duplicate, Problem = problem, Include = problem is null && !duplicate,
            });
        }
        foreach (var c in file.Categories ?? [])
        {
            var problem = string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100 || c.Extensions is null || c.Extensions.Count > 500
                ? Strings.ImportInvalidItem
                : FolderProblem(c.Folder, allowRelative: true);
            var duplicate = categories.Any(x => Same(x.Name, c.Name) || (c.Key is not null && x.Key == c.Key));
            var where = string.IsNullOrEmpty(c.Folder) ? "" : " → " + c.Folder; // where downloads of these types will go
            // Never ticked in advance: a category decides where downloads of its types are written.
            items.Add(new ImportItem(ImportKind.Category, c.Name ?? "", string.Join(" ", c.Extensions ?? []) + where, c)
            {
                IsDuplicate = duplicate, Problem = problem, Include = false,
            });
        }
        foreach (var q in file.Queues ?? [])
        {
            var problem = string.IsNullOrWhiteSpace(q.Name) || q.Name.Length > 100 || q.Schedule?.Days is null ? Strings.ImportInvalidItem : null;
            var duplicate = queues.Any(x => Same(x.Name, q.Name) || (q.Key is not null && x.Key == q.Key));
            items.Add(new ImportItem(ImportKind.Queue, q.Name ?? "", string.Format(CultureInfo.CurrentCulture, Strings.ImportQueueDetail, q.MaxConcurrent), q)
            {
                IsDuplicate = duplicate, Problem = problem, Include = problem is null,
            });
        }
        foreach (var p in file.Projects ?? [])
        {
            var copy = p.Clone();
            copy.Cookies = [];
            copy.Clamp();
            var problem = copy.Validate() is not null ? Strings.ImportInvalidItem
                : copy.Folder.Length == 0 ? null // the default folder
                : FolderProblem(copy.Folder, allowRelative: false);
            var duplicate = projects.Any(x => Same(x.Name, copy.Name));
            items.Add(new ImportItem(ImportKind.Project, copy.Name, $"{copy.StartUrl} → {copy.Folder}", copy) { IsDuplicate = duplicate, Problem = problem, Include = problem is null });
        }
        if (file.Settings is { } settings)
        {
            // Replacing settings is a choice the user makes: never ticked in advance, and the network part on its own.
            items.Add(new ImportItem(ImportKind.Settings, Strings.ImportSettingsName, settings.DownloadFolder, settings)
            {
                IsDuplicate = true, Problem = FolderProblem(settings.DownloadFolder, allowRelative: false), Include = false,
            });
            items.Add(new ImportItem(ImportKind.NetworkSettings, Strings.ImportNetworkName, NetworkSummary(settings), settings)
            {
                IsDuplicate = true, Problem = PacProblem(settings.PacUrl), Include = false, Warning = Strings.ImportNetworkWarning,
            });
        }
        return items;
    }

    private static bool Same(string a, string? b) => string.Equals(a.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static string? CheckDownload(ExportedDownload d)
    {
        if (!Urls.TryParseDownload(d.Url, out var uri) || d.Url.Length > Integration.MessageValidator.MaxUrlLength)
            return Strings.ImportInvalidAddress;
        if (d.Media is { } media && (media.Kind is not ("hls" or "dash") || uri.Scheme is not ("http" or "https") || MediaPolicy.IsRestricted(uri.AbsoluteUri)
                                     || string.IsNullOrEmpty(media.VariantId)))
            return Strings.ImportInvalidItem;
        return null;
    }

    /// <summary>Why an imported folder is refused, or null (see <see cref="SafePaths.CheckImportedFolder"/>).</summary>
    private static string? FolderProblem(string? folder, bool allowRelative)
    {
        if (string.IsNullOrEmpty(folder))
            return allowRelative ? null : Strings.ImportInvalidFolder;
        if (SafePaths.IsNetworkOrDevice(folder))
            return Strings.ImportFolderNotLocal;
        if (folder.Length > 1000 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || folder.Contains('*') || folder.Contains('?'))
            return Strings.ImportInvalidFolder;
        if (Path.IsPathFullyQualified(folder) || SafePaths.IsNetworkOrDevice(folder))
            return SafePaths.CheckImportedFolder(folder);
        // A category folder may be relative to the download folder, but never climb out of it.
        return allowRelative && !Path.IsPathRooted(folder) && !folder.Split('\\', '/').Contains("..") ? null : Strings.ImportInvalidFolder;
    }

    /// <summary>An imported PAC file may come only over https or from a file on this computer (never http, never a share).</summary>
    private static string? PacProblem(string? pac)
    {
        if (string.IsNullOrWhiteSpace(pac))
            return null;
        if (Uri.TryCreate(pac, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.Scheme == "https" ? null : Strings.ImportPacNotHttps;
        var path = uri is { IsFile: true } ? uri.LocalPath : pac;
        return SafePaths.IsLocal(path) && !SafePaths.IsNetworkOrDevice(pac) ? null : Strings.ImportPacNotHttps;
    }

    private static string NetworkSummary(ExportedSettings s) => string.Join(" · ", new[]
    {
        s.ProxyMode switch
        {
            ProxyMode.Manual => string.Format(CultureInfo.CurrentCulture, Strings.ImportNetworkProxy, $"{s.ProxyHost}:{s.ProxyPort}"),
            ProxyMode.Pac => string.Format(CultureInfo.CurrentCulture, Strings.ImportNetworkPac, s.PacUrl),
            ProxyMode.System => Strings.ProxySystem,
            _ => Strings.ProxyNone,
        },
        s.ConfirmBrowserDownloads ? "" : Strings.ImportNetworkNoConfirm,
    }.Where(t => t.Length > 0));

    public static ImportResult Apply(IEnumerable<ImportItem> items, DuplicateMode mode, DownloadService downloads, GrabberService? grabber)
    {
        int added = 0, replaced = 0, skipped = 0;
        items = items.ToList();
        bool Chosen(ImportKind kind) => items.Any(i => i.Kind == kind && i.Include && i.Problem is null);
        var general = Chosen(ImportKind.Settings);
        var network = Chosen(ImportKind.NetworkSettings);
        var settingsDone = false;
        foreach (var item in items)
        {
            if (!item.Include || item.Problem is not null)
            {
                skipped++;
                continue;
            }
            switch (item.Source)
            {
                case ExportedDownload d:
                    if (item.IsDuplicate && mode != DuplicateMode.KeepBoth)
                    {
                        skipped++;
                        break;
                    }
                    downloads.AddImported(d);
                    added++;
                    break;

                case ExportedCategory c:
                {
                    var existing = downloads.Categories.FirstOrDefault(x => (c.Key is not null && x.Key == c.Key) || Same(x.Name, c.Name));
                    if (existing is not null && mode == DuplicateMode.Skip)
                    {
                        skipped++;
                        break;
                    }
                    var category = existing is not null && mode == DuplicateMode.Replace ? existing.Clone() : new Category();
                    category.Name = existing is not null && mode == DuplicateMode.KeepBoth ? NewName(c.Name, downloads.Categories.Select(x => x.Name)) : c.Name.Trim();
                    category.Extensions = Categories.ParseExtensions(string.Join(" ", c.Extensions));
                    category.Folder = c.Folder ?? "";
                    downloads.SaveCategory(category);
                    if (category.Id != 0)
                        replaced++;
                    else
                        added++;
                    break;
                }

                case ExportedQueue q:
                {
                    var existing = downloads.Queues.FirstOrDefault(x => (q.Key is not null && x.Key == q.Key) || Same(x.Name, q.Name));
                    if (existing is not null && mode == DuplicateMode.Skip)
                    {
                        skipped++;
                        break;
                    }
                    var queue = existing is not null && mode == DuplicateMode.Replace ? existing.Clone() : new DownloadQueue();
                    queue.Name = existing is not null && mode == DuplicateMode.KeepBoth ? NewName(q.Name, downloads.Queues.Select(x => x.Name)) : q.Name.Trim();
                    queue.MaxConcurrent = Math.Clamp(q.MaxConcurrent, 1, 20);
                    queue.Schedule = q.Schedule.Clone();
                    queue.RetryCount = Math.Clamp(q.RetryCount, 0, 100);
                    queue.RetryDelaySeconds = Math.Clamp(q.RetryDelaySeconds, 0, 86400);
                    queue.QuotaMBPerHour = Math.Max(0, q.QuotaMBPerHour);
                    // Nothing imported shuts the PC down or starts by itself: end action and schedule are set by the user.
                    queue.EndAction = EndAction.None;
                    queue.Schedule.Enabled = false;
                    downloads.SaveQueue(queue);
                    if (queue.Id != 0)
                        replaced++;
                    else
                        added++;
                    break;
                }

                case GrabberProject p when grabber is not null:
                {
                    var existing = grabber.Projects.FirstOrDefault(x => Same(x.Name, p.Name));
                    if (existing is not null && mode == DuplicateMode.Skip)
                    {
                        skipped++;
                        break;
                    }
                    var project = p.Clone();
                    project.Cookies = [];
                    // Nothing imported runs by itself: the user turns the schedule on after looking at the project.
                    project.Schedule.Enabled = false;
                    project.Id = existing is not null && mode == DuplicateMode.Replace ? existing.Id : 0;
                    if (existing is not null && mode == DuplicateMode.KeepBoth)
                        project.Name = NewName(p.Name, grabber.Projects.Select(x => x.Name));
                    grabber.Save(project);
                    if (project.Id != 0)
                        replaced++;
                    else
                        added++;
                    break;
                }

                case ExportedSettings s when !settingsDone:
                    downloads.UpdateSettings(MergeSettings(downloads.Settings, s, general, network));
                    settingsDone = true;
                    replaced++;
                    break;

                case ExportedSettings:
                    break; // the other half of the same settings, applied above

                default:
                    skipped++;
                    break;
            }
        }
        return new ImportResult(added, replaced, skipped);
    }

    /// <summary>The imported settings over the current ones; program paths, the update address and passwords stay as they are.</summary>
    /// <param name="general">Apply the general part (limits, folder, theme...).</param>
    /// <param name="network">Apply the proxy, the PAC file and the confirmation of browser downloads.</param>
    public static AppSettings MergeSettings(AppSettings current, ExportedSettings s, bool general = true, bool network = false)
    {
        var result = current.Clone();
        if (network)
            MergeNetwork(result, current, s);
        if (!general)
            return result;
        // Numbers within the limits the settings window allows.
        result.MaxConnections = Math.Clamp(s.MaxConnections, 1, 32);
        result.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 50);
        result.SpeedLimitKBps = Math.Max(0, s.SpeedLimitKBps);
        result.DownloadFolder = SafePaths.CheckImportedFolder(s.DownloadFolder ?? "") is null ? s.DownloadFolder! : current.DownloadFolder;
        result.GlobalQuotaMBPerHour = Math.Max(0, s.GlobalQuotaMBPerHour);
        result.CloseToTray = s.CloseToTray;
        result.FtpActiveMode = s.FtpActiveMode;
        result.TimeoutSeconds = Math.Clamp(s.TimeoutSeconds, 5, 600);
        result.MaxConnectionsPerServer = Math.Clamp(s.MaxConnectionsPerServer, 1, 256);
        result.UserAgent = string.IsNullOrWhiteSpace(s.UserAgent) || s.UserAgent.Any(char.IsControl) ? current.UserAgent : s.UserAgent;
        result.Theme = Enum.IsDefined(s.Theme) ? s.Theme : current.Theme;
        result.NotifyOnCompletion = s.NotifyOnCompletion;
        result.ComputeHashOnCompletion = s.ComputeHashOnCompletion;
        result.HashAlgorithm = FileHash.Algorithms.Contains(s.HashAlgorithm) ? s.HashAlgorithm : current.HashAlgorithm;
        return result;
    }

    private static void MergeNetwork(AppSettings result, AppSettings current, ExportedSettings s)
    {
        // The saved proxy password goes only to the proxy it was typed for: any other destination drops it.
        var sameProxy = s.ProxyMode == current.ProxyMode && s.ProxyType == current.ProxyType && s.ProxyPort == current.ProxyPort
                        && string.Equals(s.ProxyHost ?? "", current.ProxyHost, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.PacUrl ?? "", current.PacUrl, StringComparison.Ordinal)
                        && string.Equals(s.ProxyUser ?? "", current.ProxyUser, StringComparison.Ordinal);
        if (!sameProxy)
            result.ProxyPassword = "";
        result.ProxyMode = Enum.IsDefined(s.ProxyMode) ? s.ProxyMode : current.ProxyMode;
        result.ProxyType = Enum.IsDefined(s.ProxyType) ? s.ProxyType : current.ProxyType;
        result.ProxyHost = s.ProxyHost ?? "";
        result.ProxyPort = s.ProxyPort is >= 1 and <= 65535 ? s.ProxyPort : current.ProxyPort;
        result.ProxyUser = s.ProxyUser ?? "";
        result.ProxyBypass = s.ProxyBypass ?? "";
        result.PacUrl = PacProblem(s.PacUrl) is null ? s.PacUrl ?? "" : current.PacUrl;
        result.ConfirmBrowserDownloads = s.ConfirmBrowserDownloads;
    }

    private static string NewName(string name, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 2; ; i++)
        {
            var candidate = $"{name.Trim()} ({i})";
            if (!set.Contains(candidate))
                return candidate;
        }
    }
}
