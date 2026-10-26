using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Core.Grabber;

namespace Baton.Core.Portable;

/// <summary>A download as exported: where it comes from and where it goes, never its cookies, form data or login.</summary>
public sealed record ExportedDownload(
    string Url,
    string? FileName,
    string? Directory,
    string? Category,
    string? Queue,
    string Status,
    long? Size,
    DateTime AddedUtc,
    DateTime? CompletedUtc,
    string? ExpectedHash,
    ExportedMedia? Media);

/// <summary>A stream download's choices (quality, language, subtitles).</summary>
public sealed record ExportedMedia(string Kind, string VariantId, string? Label, string? AudioId, List<string>? SubtitleIds, string? SubtitleMode);

public sealed record ExportedCategory(string Name, string? Key, List<string> Extensions, string Folder);

public sealed record ExportedQueue(string Name, string? Key, int MaxConcurrent, QueueSchedule Schedule, int RetryCount, int RetryDelaySeconds,
    long QuotaMBPerHour, EndAction EndAction);

/// <summary>The settings that travel: no password, and no program paths or update address (see <see cref="PortableData"/>).</summary>
public sealed class ExportedSettings
{
    public int MaxConnections { get; set; }
    public int MaxConcurrentDownloads { get; set; }
    public long SpeedLimitKBps { get; set; }
    public string DownloadFolder { get; set; } = "";
    public bool? CategorySubfolders { get; set; }
    public int? ClearCompletedAfterDays { get; set; }
    public long GlobalQuotaMBPerHour { get; set; }
    public bool CloseToTray { get; set; }
    public ProxyMode ProxyMode { get; set; }
    public ProxyType ProxyType { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyUser { get; set; } = "";
    public string ProxyBypass { get; set; } = "";
    public string PacUrl { get; set; } = "";
    public bool FtpActiveMode { get; set; }
    public int TimeoutSeconds { get; set; }
    public int MaxConnectionsPerServer { get; set; }
    public string UserAgent { get; set; } = "";
    public bool ConfirmBrowserDownloads { get; set; }
    public AppTheme Theme { get; set; }
    public bool NotifyOnCompletion { get; set; }
    public bool ComputeHashOnCompletion { get; set; }
    public string HashAlgorithm { get; set; } = "SHA256";
}

/// <summary>One exported file: either a list of downloads or the app's configuration.</summary>
public sealed class PortableFile
{
    public const string DownloadsKind = "downloads";
    public const string SettingsKind = "settings";

    public string Format { get; set; } = FormatName;

    public const string FormatName = "Baton";

    /// <summary>Files exported before the rename (up to 1.1) carry the old name and are read the same way.</summary>
    public const string LegacyFormatName = "DownloadManager";
    public int Version { get; set; } = 1;
    public string Kind { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
    public List<ExportedDownload>? Downloads { get; set; }
    public ExportedSettings? Settings { get; set; }
    public List<ExportedCategory>? Categories { get; set; }
    public List<ExportedQueue>? Queues { get; set; }

    /// <summary>Grabber projects, without their cookies.</summary>
    public List<GrabberProject>? Projects { get; set; }
}

/// <summary>
/// Export and import of the download list (JSON, or the addresses as plain text) and of the configuration (settings,
/// categories, queues, grabber projects). Nothing secret ever leaves the app: no cookies, form data, logins or
/// passwords, no saved credentials or trusted certificates. An imported file is untrusted input: sizes are bounded,
/// every address and folder is checked, and settings that would make the app run a program or fetch an update
/// (ffmpeg, antivirus, update address) are never imported.
/// </summary>
public static class PortableData
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int MaxItems = 100_000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static PortableFile ExportDownloads(IEnumerable<DownloadRecord> records, IReadOnlyList<Category> categories, IReadOnlyList<DownloadQueue> queues,
        DateTime utcNow) => new()
    {
        Kind = PortableFile.DownloadsKind,
        ExportedUtc = utcNow,
        Downloads = records.OrderBy(r => r.SortOrder).Select(r => new ExportedDownload(
            Urls.Display(r.Url),
            r.FileName,
            r.Directory,
            categories.FirstOrDefault(c => c.Id == r.CategoryId)?.Name,
            queues.FirstOrDefault(q => q.Id == r.QueueId)?.Name,
            r.Status.ToString(),
            r.TotalSize,
            r.AddedUtc,
            r.CompletedUtc,
            r.Check?.ExpectedHash is { } hash ? $"{r.Check.HashAlgorithm}:{hash}" : null,
            r.Media is { } m ? new ExportedMedia(m.Kind, m.VariantId, m.Label, m.AudioId, m.SubtitleIds, m.SubtitleMode) : null)).ToList(),
    };

    /// <summary>The addresses, one per line, without any login.</summary>
    public static string ExportUrls(IEnumerable<DownloadRecord> records) =>
        string.Join(Environment.NewLine, records.OrderBy(r => r.SortOrder).Select(r => Urls.Display(r.Url))) + Environment.NewLine;

    public static PortableFile ExportSettings(AppSettings settings, IReadOnlyList<Category> categories, IReadOnlyList<DownloadQueue> queues,
        IReadOnlyList<GrabberProject> projects, DateTime utcNow) => new()
    {
        Kind = PortableFile.SettingsKind,
        ExportedUtc = utcNow,
        Settings = new ExportedSettings
        {
            MaxConnections = settings.MaxConnections, MaxConcurrentDownloads = settings.MaxConcurrentDownloads, SpeedLimitKBps = settings.SpeedLimitKBps,
            DownloadFolder = settings.DownloadFolder, CategorySubfolders = settings.CategorySubfolders,
            ClearCompletedAfterDays = settings.ClearCompletedAfterDays, GlobalQuotaMBPerHour = settings.GlobalQuotaMBPerHour, CloseToTray = settings.CloseToTray,
            ProxyMode = settings.ProxyMode, ProxyType = settings.ProxyType, ProxyHost = settings.ProxyHost, ProxyPort = settings.ProxyPort,
            ProxyUser = settings.ProxyUser, ProxyBypass = settings.ProxyBypass, PacUrl = settings.PacUrl, FtpActiveMode = settings.FtpActiveMode,
            TimeoutSeconds = settings.TimeoutSeconds, MaxConnectionsPerServer = settings.MaxConnectionsPerServer, UserAgent = settings.UserAgent,
            ConfirmBrowserDownloads = settings.ConfirmBrowserDownloads, Theme = settings.Theme, NotifyOnCompletion = settings.NotifyOnCompletion,
            ComputeHashOnCompletion = settings.ComputeHashOnCompletion, HashAlgorithm = settings.HashAlgorithm,
        },
        Categories = categories.Select(c => new ExportedCategory(c.Name, c.Key, [.. c.Extensions], c.Folder)).ToList(),
        Queues = queues.Select(q => new ExportedQueue(q.Name, q.Key, q.MaxConcurrent, q.Schedule.Clone(), q.RetryCount, q.RetryDelaySeconds, q.QuotaMBPerHour,
            q.EndAction)).ToList(),
        Projects = projects.Select(p =>
        {
            var copy = p.Clone();
            copy.Id = 0;
            copy.Cookies = []; // never exported (and not serialized anyway)
            copy.LastRunUtc = null;
            copy.LastResult = null;
            return copy;
        }).ToList(),
    };

    public static string Serialize(PortableFile file) => JsonSerializer.Serialize(file, Json);

    /// <summary>
    /// Reads an exported file: JSON from this app, or plain text with addresses (one per line, or scattered in text).
    /// Throws <see cref="FormatException"/> for anything else.
    /// </summary>
    /// <summary>The file is untrusted input: null entries are dropped and missing lists and texts get their defaults.</summary>
    private static PortableFile Clean(PortableFile file)
    {
        file.Downloads = file.Downloads?.Where(d => d is not null).ToList();
        file.Categories = file.Categories?.Where(c => c is not null).ToList();
        file.Queues = file.Queues?.Where(q => q is not null).ToList();
        file.Projects = file.Projects?.Where(p => p is not null).ToList();
        foreach (var p in file.Projects ?? [])
        {
            p.Name ??= "";
            p.StartUrl ??= "";
            p.AllowedDomains = p.AllowedDomains?.Where(d => d is not null).ToList() ?? [];
            p.Extensions = p.Extensions?.Where(e => e is not null).ToList() ?? [];
            p.IncludePattern ??= "";
            p.ExcludePattern ??= "";
            p.UserAgent ??= "";
            p.Folder ??= "";
            if (p.Schedule?.Days is null)
                p.Schedule = new QueueSchedule();
            p.Cookies = [];
        }
        return file;
    }

    public static PortableFile Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FormatException(Resources.Strings.ImportErrorMissing);
        if (info.Length > MaxFileBytes)
            throw new FormatException(Resources.Strings.ImportErrorTooLarge);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.TrimStart().StartsWith('{'))
        {
            PortableFile? file;
            try
            {
                file = JsonSerializer.Deserialize<PortableFile>(text, Json);
            }
            catch (JsonException ex)
            {
                throw new FormatException(Resources.Strings.ImportErrorFormat, ex);
            }
            if (file is null || file.Format is not (PortableFile.FormatName or PortableFile.LegacyFormatName) || file.Kind is not (PortableFile.DownloadsKind or PortableFile.SettingsKind))
                throw new FormatException(Resources.Strings.ImportErrorFormat);
            if ((file.Downloads?.Count ?? 0) + (file.Categories?.Count ?? 0) + (file.Queues?.Count ?? 0) + (file.Projects?.Count ?? 0) > MaxItems)
                throw new FormatException(Resources.Strings.ImportErrorTooLarge);
            return Clean(file);
        }
        var urls = Urls.ExtractHttpUrls(text);
        if (urls.Count == 0)
            throw new FormatException(Resources.Strings.ImportErrorEmpty);
        return new PortableFile
        {
            Kind = PortableFile.DownloadsKind,
            Downloads = urls.Take(MaxItems).Select(u => new ExportedDownload(u, null, null, null, null, nameof(DownloadStatus.Paused), null, DateTime.UtcNow,
                null, null, null)).ToList(),
        };
    }
}
