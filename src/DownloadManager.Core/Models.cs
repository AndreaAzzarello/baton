namespace DownloadManager.Core;

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

    /// <summary>Browser session details needed to fetch the file; null for downloads added by hand.</summary>
    public RequestContext? Context { get; set; }

    public long DownloadedBytes =>
        Status == DownloadStatus.Completed && TotalSize is { } size ? size : Segments.Sum(s => s.Written);

    public string? PartPath => FileName is null ? null : Path.Combine(Directory, $"{FileName}.{Id}.part");

    public string? FinalPath => FileName is null ? null : Path.Combine(Directory, FileName);

    public DownloadRecord Clone()
    {
        var copy = (DownloadRecord)MemberwiseClone();
        copy.Segments = Segments.Select(s => s.Clone()).ToList();
        return copy;
    }
}

/// <summary>A cookie as the browser reported it for the download URL.</summary>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HostOnly);

/// <summary>What the browser would have sent with the request: some servers refuse a download without it.</summary>
public sealed record RequestContext(string? Referrer, string? UserAgent, IReadOnlyList<BrowserCookie> Cookies)
{
    public bool IsEmpty => Referrer is null && UserAgent is null && Cookies.Count == 0;
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
    string? ContentType = null);

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
    public int MaxConcurrentDownloads { get; set; } = 3;
    public long SpeedLimitKBps { get; set; }
    public string DownloadFolder { get; set; } = KnownFolders.Downloads;
    public bool QueueRunning { get; set; } = true;

    /// <summary>Show the add dialog for downloads sent by the browser extension instead of starting them directly.</summary>
    public bool ConfirmBrowserDownloads { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Windows notification when a download completes.</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
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
