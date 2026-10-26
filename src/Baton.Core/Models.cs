using Baton.Core.PostDownload;

namespace Baton.Core;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
}

/// <summary>A byte range of the target file and how much of it is already on disk.</summary>
public sealed class SegmentState
{
    public long Start { get; set; }

    /// <summary>Inclusive end offset, or -1 when the total size is unknown.</summary>
    public long End { get; set; }

    public long Written { get; set; }

    public long Position => Start + Written;

    public bool HasKnownEnd => End >= 0;

    /// <summary>Set when an open-ended stream reached its end.</summary>
    public bool Closed { get; set; }

    public long Remaining => HasKnownEnd ? Math.Max(0, End - Position + 1) : long.MaxValue;

    public bool IsComplete => Closed || (HasKnownEnd && Position > End);

    public SegmentState Clone() => new() { Start = Start, End = End, Written = Written, Closed = Closed };
}

public sealed class DownloadRecord
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? FileName { get; set; }
    public long? TotalSize { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public bool SupportsRanges { get; set; }
    public DownloadStatus Status { get; set; }
    public string? Error { get; set; }
    public int SortOrder { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<SegmentState> Segments { get; set; } = [];

    public long? CategoryId { get; set; }

    /// <summary>
    /// The category was guessed from the URL when the download was added: once the server reveals the real
    /// file name and type it is chosen again, and so is the folder if it was the category's folder.
    /// </summary>
    public bool AutoCategory { get; set; }

    /// <summary>Media type reported by the server, without parameters.</summary>
    public string? ContentType { get; set; }

    public long? QueueId { get; set; }

    /// <summary>Failed attempts since the last success; compared with the queue's retry count.</summary>
    public int Failures { get; set; }

    /// <summary>A failed download waiting in its queue is not started again before this time.</summary>
    public DateTime? RetryAtUtc { get; set; }

    /// <summary>Browser session details needed to fetch the file; null for downloads added by hand.</summary>
    public RequestContext? Context { get; set; }

    /// <summary>
    /// Set for a video or audio stream (HLS or DASH): <see cref="Url"/> is then its manifest, and the file is put
    /// together from many small segments instead of byte ranges of one file.
    /// </summary>
    public MediaSource? Media { get; set; }

    /// <summary>Hash and antivirus checks of the finished file; null when none were asked for.</summary>
    public PostCheck? Check { get; set; }

    public long DownloadedBytes =>
        Status == DownloadStatus.Completed && TotalSize is { } size ? size : Segments.Sum(s => s.Written);

    public string? PartPath => FileName is null ? null : Path.Combine(Directory, $"{FileName}.{Id}.part");

    public string? FinalPath => FileName is null ? null : Path.Combine(Directory, FileName);

    public DownloadRecord Clone()
    {
        var copy = (DownloadRecord)MemberwiseClone();
        copy.Segments = Segments.Select(s => s.Clone()).ToList();
        copy.Media = Media?.Clone();
        copy.Check = Check?.Clone();
        return copy;
    }
}

/// <summary>A cookie as the browser reported it for the download URL.</summary>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HostOnly);

/// <summary>
/// What the browser would have sent with the request (some servers refuse a download without it), and the login
/// given in the address itself (ftp://user:password@host/...). Stored encrypted and cleared when the download completes.
/// </summary>
public sealed record RequestContext(string? Referrer, string? UserAgent, IReadOnlyList<BrowserCookie> Cookies,
    string? Username = null, string? Password = null)
{
    /// <summary>"POST" for a download started by submitting a form; null for GET.</summary>
    public string? Method { get; init; }

    /// <summary>The form fields, URL-encoded (name=value&amp;...), sent again with each request.</summary>
    public string? Body { get; init; }

    /// <summary>How the form encoded its fields: application/x-www-form-urlencoded or multipart/form-data.</summary>
    public string? BodyType { get; init; }

    public bool IsPost => Method == "POST";

    public bool IsEmpty => Referrer is null && UserAgent is null && Cookies.Count == 0 && Username is null && Method is null;
}

/// <summary>What a video stream download needs besides its manifest address.</summary>
public sealed class MediaSource
{
    /// <summary>"hls" or "dash".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The quality the user picked, as listed by the media probe.</summary>
    public string VariantId { get; set; } = "";

    /// <summary>The quality as shown to the user, e.g. "1080p".</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Set when the download completed without the single MP4 the user may expect (ffmpeg missing or failed): what
    /// was saved instead. Shown with the finished download.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>The audio language chosen (as listed by the probe); null = the stream's default.</summary>
    public string? AudioId { get; set; }

    /// <summary>The subtitle tracks chosen (WebVTT), saved next to the video or put inside it.</summary>
    public List<string>? SubtitleIds { get; set; }

    /// <summary>"vtt" or "srt" (files next to the video) or "embed" (inside the video, with ffmpeg).</summary>
    public string? SubtitleMode { get; set; }

    public MediaSource Clone()
    {
        var copy = (MediaSource)MemberwiseClone();
        copy.SubtitleIds = SubtitleIds is null ? null : [.. SubtitleIds];
        return copy;
    }
}

/// <summary>Immutable view of a download for the UI.</summary>
public sealed record DownloadSnapshot(
    long Id,
    string Url,
    string DisplayName,
    string Directory,
    string? FinalPath,
    long? TotalSize,
    long Downloaded,
    DownloadStatus Status,
    string? Error,
    int Connections,
    int SortOrder,
    bool SupportsRanges,
    DateTime AddedUtc,
    long? CategoryId = null,
    DateTime? CompletedUtc = null,
    string? ETag = null,
    string? LastModified = null,
    string? ContentType = null,
    long? QueueId = null,
    DateTime? RetryAtUtc = null,
    PostCheck? Check = null);

/// <summary>A byte range for the progress view; <see cref="Active"/> when a connection is downloading it right now.</summary>
public sealed record SegmentView(long Start, long End, long Written, bool Active);

public sealed class EngineOptions
{
    public int MaxConnections { get; set; } = 8;
    public long MinSegmentSize { get; set; } = 1024 * 1024;
    public int BufferSize { get; set; } = 64 * 1024;
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 6;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class AppSettings
{
    public int MaxConnections { get; set; } = 8;

    /// <summary>Downloads that all queues together may run at once; each queue also has its own limit.</summary>
    public int MaxConcurrentDownloads { get; set; } = 10;
    public long SpeedLimitKBps { get; set; }
    public string DownloadFolder { get; set; } = KnownFolders.Downloads;

    /// <summary>Categories with a relative folder ("Video") get a subfolder of the download folder; off, everything lands in it.</summary>
    public bool CategorySubfolders { get; set; } = true;

    /// <summary>Completed downloads older than this many days leave the list by themselves (files stay); 0 = never.</summary>
    public int ClearCompletedAfterDays { get; set; }

    /// <summary>Until version 0.3 the state of the only queue; now the main queue keeps it (read during the upgrade).</summary>
    public bool QueueRunning { get; set; } = true;

    /// <summary>MB that all downloads together may transfer in any 60 minutes; 0 = no quota.</summary>
    public long GlobalQuotaMBPerHour { get; set; }

    /// <summary>Closing the main window keeps the app running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    // Network

    /// <summary>System by default: the proxy configured in Windows, as the app always used.</summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    public ProxyType ProxyType { get; set; } = ProxyType.Http;
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUser { get; set; } = "";

    /// <summary>Kept in memory only; stored encrypted with DPAPI.</summary>
    public string ProxyPassword { get; set; } = "";

    /// <summary>Hosts reached without the proxy, separated by ";" or spaces; "*" is a wildcard, "&lt;local&gt;" means names without a dot.</summary>
    public string ProxyBypass { get; set; } = "";

    /// <summary>Address (http, https or file) of the proxy auto-configuration script.</summary>
    public string PacUrl { get; set; } = "";

    /// <summary>FTP data connections opened by the server (PORT/EPRT) instead of the default passive mode.</summary>
    public bool FtpActiveMode { get; set; }

    /// <summary>How long to wait for a connection, a response or more data.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public int MaxConnectionsPerServer { get; set; } = 16;
    public string UserAgent { get; set; } = DefaultUserAgent;

    public const string DefaultUserAgent = "Baton/1.3";

    /// <summary>
    /// The default of an earlier version ("Baton/1.2", or "DownloadManager/1.0" from before the rename), saved as is: it
    /// follows the version, a custom value does not.
    /// </summary>
    public static bool IsEarlierDefaultUserAgent(string value) =>
        value != DefaultUserAgent && (VersionAfter(value, "Baton/") || VersionAfter(value, "DownloadManager/"));

    private static bool VersionAfter(string value, string product) =>
        value.StartsWith(product, StringComparison.Ordinal)
        && value[product.Length..] is { Length: > 0 } number && number.All(c => char.IsAsciiDigit(c) || c == '.');

    /// <summary>ffmpeg.exe used to put the video and audio of a stream into one MP4; empty = look for it in PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Show the add dialog for downloads sent by the browser extension instead of starting them directly.</summary>
    public bool ConfirmBrowserDownloads { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Language of the interface; automatic follows the language of Windows.</summary>
    public AppLanguage Language { get; set; }

    /// <summary>
    /// Portable copy only: the user agreed (true) or refused (false) to register it as the browsers' native messaging host,
    /// the one registry entry it may write; null until asked.
    /// </summary>
    public bool? PortableBrowserHost { get; set; }

    /// <summary>Windows notification when a download completes.</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    // After the download

    /// <summary>Compute the hash of every finished file (also without an expected one), with <see cref="HashAlgorithm"/>.</summary>
    public bool ComputeHashOnCompletion { get; set; }

    public string HashAlgorithm { get; set; } = "SHA256";

    /// <summary>Scan every finished file with the antivirus below. Off by default.</summary>
    public bool ScanOnCompletion { get; set; }

    /// <summary>Empty = Windows Defender (MpCmdRun.exe).</summary>
    public string ScannerPath { get; set; } = "";

    public string ScannerArguments { get; set; } = VirusScanner.DefaultArguments;

    /// <summary>Exit codes that mean "threat found" (0 always means clean).</summary>
    public string ScanThreatCodes { get; set; } = VirusScanner.DefaultThreatCodes;

    public int ScanTimeoutSeconds { get; set; } = 300;

    // Updates

    /// <summary>Look for a newer version at <see cref="UpdateUrl"/> when the app starts. Off by default.</summary>
    public bool UpdateCheckEnabled { get; set; }

    /// <summary>A GitHub "latest release" API address, or a JSON manifest (version, notes, url, sha256).</summary>
    // Empty (the default up to 1.1.0) and the never published 1.1.1 default mean the project's own releases.
    public string UpdateUrl
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) || value.Trim() == LegacyDefaultUpdateUrl ? DefaultUpdateUrl : value;
    } = DefaultUpdateUrl;

    /// <summary>The releases of the project's own repository; checking stays off until the user turns it on.</summary>
    public const string DefaultUpdateUrl = "https://api.github.com/repos/AndreaAzzarello/baton/releases/latest";

    private const string LegacyDefaultUpdateUrl = "https://api.github.com/repos/AndreaAzzarello/DownloadManager/releases/latest";

    /// <summary>The move of folders off the Desktop was proposed once (whatever the answer): it is not proposed again.</summary>
    public bool DesktopRelocationAsked { get; set; }

    /// <summary>The toolbar's buttons in order, by id (see the app's toolbar); empty = the default set.</summary>
    public string ToolbarItems { get; set; } = "";

    public bool ToolbarSmallIcons { get; set; }

    public bool ToolbarShowText { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public enum ProxyMode
{
    None,
    System,
    Manual,
    Pac,
}

public enum ProxyType
{
    Http,
    Https,
    Socks5,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class DownloadFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The remote file no longer matches the partial data on disk.</summary>
public sealed class ResourceChangedException(string message) : Exception(message);
