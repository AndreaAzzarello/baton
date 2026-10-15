using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.Core.Grabber;

public enum GrabberMode
{
    /// <summary>Only the files that pass the filters (e.g. every PDF): listed, then sent to a queue.</summary>
    Files,

    /// <summary>The whole site for offline reading: pages, style sheets, scripts and images, links pointed at the local copy.</summary>
    Mirror,
}

/// <summary>A saved grabber project: where to start, what to follow and keep, how to treat the site, when to run.</summary>
public sealed class GrabberProject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string StartUrl { get; set; } = "";
    public GrabberMode Mode { get; set; } = GrabberMode.Files;

    /// <summary>Pages followed from the start page: 0 = the start page only.</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Only the start page's site (and its subdomains); otherwise also <see cref="AllowedDomains"/>.</summary>
    public bool SameDomainOnly { get; set; } = true;

    /// <summary>Other sites that may be visited too (each with its subdomains), e.g. the site's file server.</summary>
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>File types kept, without the dot; empty = every file that is not a page.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>0 = no minimum.</summary>
    public long MinSizeBytes { get; set; }

    /// <summary>0 = no maximum.</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>Regular expression a file's address must match (empty = all).</summary>
    public string IncludePattern { get; set; } = "";

    /// <summary>Regular expression for addresses never visited nor kept (pages and files).</summary>
    public string ExcludePattern { get; set; } = "";

    /// <summary>Send the browser's cookies captured with the project (stored encrypted).</summary>
    public bool UseCookies { get; set; } = true;

    /// <summary>Offer the app's saved logins to sites that ask (HTTP authentication).</summary>
    public bool UseSavedCredentials { get; set; } = true;

    /// <summary>Go through the app's proxy settings; otherwise connect directly.</summary>
    public bool UseProxy { get; set; } = true;

    public bool RespectRobots { get; set; } = true;

    /// <summary>Requests started per second to each host.</summary>
    public double RequestsPerSecond { get; set; } = 2;

    /// <summary>Empty = the app's user agent.</summary>
    public string UserAgent { get; set; } = "";

    public int MaxPages { get; set; } = 200;
    public int MaxFiles { get; set; } = 1000;
    public long MaxTotalMB { get; set; } = 1024;

    /// <summary>Pages of the same shape (e.g. "calendar?month=N"): a calendar or an endless list stops there.</summary>
    public int MaxPagesPerPattern { get; set; } = 100;

    /// <summary>Mirror: the folder of the copy. Files: where downloads go (empty = each file's category folder).</summary>
    public string Folder { get; set; } = "";

    public long? QueueId { get; set; }

    /// <summary>Null = each file to the category of its type.</summary>
    public long? CategoryId { get; set; }

    /// <summary>When the project runs by itself; only start, days or date and repetition are used.</summary>
    public QueueSchedule Schedule { get; set; } = new();

    public DateTime AddedUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }

    /// <summary>A short summary of the last run, shown in the project list.</summary>
    public string? LastResult { get; set; }

    /// <summary>Captured from the browser with the project; never serialized with the settings (stored encrypted).</summary>
    [JsonIgnore]
    public List<BrowserCookie> Cookies { get; set; } = [];

    public GrabberProject Clone()
    {
        var copy = (GrabberProject)MemberwiseClone();
        copy.AllowedDomains = [.. AllowedDomains];
        copy.Extensions = [.. Extensions];
        copy.Schedule = Schedule.Clone();
        copy.Cookies = [.. Cookies];
        return copy;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The settings column: everything except the id, the name and the secrets.</summary>
    public string SerializeSettings() => JsonSerializer.Serialize(this, Json);

    public static GrabberProject DeserializeSettings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GrabberProject>(json, Json) ?? new GrabberProject();
        }
        catch (JsonException)
        {
            return new GrabberProject(); // damaged settings: the project survives with defaults
        }
    }

    /// <summary>Why the project cannot be saved as it is, or null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "name";
        if (!Uri.TryCreate(StartUrl?.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https") || start.Host.Length == 0)
            return "url";
        if (GrabberFilters.CheckPattern(IncludePattern) is not null || GrabberFilters.CheckPattern(ExcludePattern) is not null)
            return "pattern";
        if (MaxSizeBytes > 0 && MinSizeBytes > MaxSizeBytes)
            return "size";
        if (Mode == GrabberMode.Mirror && (string.IsNullOrWhiteSpace(Folder) || !Path.IsPathFullyQualified(Folder.Trim())))
            return "folder";
        if (Folder.Trim().Length > 0 && !Path.IsPathFullyQualified(Folder.Trim()))
            return "folder";
        return null;
    }

    /// <summary>Limits kept within sane bounds whatever was typed or stored.</summary>
    public void Clamp()
    {
        Name = Name.Trim();
        StartUrl = StartUrl.Trim();
        Folder = Folder.Trim();
        MaxDepth = Math.Clamp(MaxDepth, 0, 20);
        RequestsPerSecond = Math.Clamp(RequestsPerSecond, 0.1, 20);
        MaxPages = Math.Clamp(MaxPages, 1, 100_000);
        MaxFiles = Math.Clamp(MaxFiles, 1, 200_000);
        MaxTotalMB = Math.Clamp(MaxTotalMB, 1, 10_000_000);
        MaxPagesPerPattern = Math.Clamp(MaxPagesPerPattern, 1, 100_000);
        MinSizeBytes = Math.Max(0, MinSizeBytes);
        MaxSizeBytes = Math.Max(0, MaxSizeBytes);
        Extensions = Extensions.Select(e => e.Trim().TrimStart('*', '.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        AllowedDomains = AllowedDomains.Select(d => d.Trim().ToLowerInvariant().TrimStart('*', '.')).Where(d => d.Length > 0).Distinct().ToList();
        UserAgent = UserAgent.Trim();
        Schedule.RepeatMinutes = Math.Max(0, Schedule.RepeatMinutes);
    }
}

/// <summary>What the grabber remembers about one file between runs, for synchronization.</summary>
public sealed class GrabberFileState
{
    public string Url { get; set; } = "";

    /// <summary>Mirror: the file's path relative to the copy's folder.</summary>
    public string? LocalPath { get; set; }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public long? Size { get; set; }
    public string? ContentType { get; set; }
    public string? SourcePage { get; set; }

    /// <summary>Pages: a hash of the content, to tell a changed page from an unchanged one.</summary>
    public string? ContentHash { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Set when the site no longer has the file; the local copy is never deleted.</summary>
    public DateTime? RemovedUtc { get; set; }

    public GrabberFileState Clone() => (GrabberFileState)MemberwiseClone();
}

public enum GrabberItemStatus
{
    /// <summary>Not seen by an earlier run.</summary>
    New,

    /// <summary>Seen before, and the site's ETag, date or size differ now.</summary>
    Changed,

    Unchanged,

    /// <summary>Gone from the site since the last run (the local copy is kept).</summary>
    Removed,

    /// <summary>Could not be read (the reason is in <see cref="GrabberItem.Error"/>).</summary>
    Failed,
}

/// <summary>A file found by a run, as the preview lists it.</summary>
public sealed record GrabberItem(
    string Url,
    string Name,
    string Extension,
    long? Size,
    string? ContentType,
    string SourcePage,
    GrabberItemStatus Status,
    string? ETag = null,
    string? LastModified = null,
    string? LocalPath = null,
    string? Error = null,
    bool IsPage = false);

/// <summary>How far a run is, for the progress line.</summary>
public sealed record GrabberProgress(
    int PagesVisited,
    int PagesQueued,
    int FilesFound,
    long Bytes,
    int Errors,
    int RobotsBlocked,
    int Skipped,
    string? CurrentUrl,
    string? LimitReached,
    bool Finished);

/// <summary>The outcome of a run.</summary>
public sealed record GrabberRunResult(
    int Pages,
    int Files,
    int New,
    int Changed,
    int Unchanged,
    int Removed,
    int Failed,
    long Bytes,
    string? LimitReached,
    bool Cancelled,
    string? StartPage);
