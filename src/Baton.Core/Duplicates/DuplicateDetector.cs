using Baton.Core.Grabber;

namespace Baton.Core.Duplicates;

/// <summary>Which rule found the duplicate; the add dialog says it in words.</summary>
public enum DuplicateReason
{
    /// <summary>The same address (without fragment or login) is already in the list or in the history.</summary>
    SameUrl,

    /// <summary>A file with this name is already in the destination folder.</summary>
    SameName,

    /// <summary>Another address, but the same size and the same ETag (or Last-Modified) as a finished download.</summary>
    SameFile,
}

/// <summary>What to do with a duplicate; the remembered choice of the settings. Modelled on <c>PortableImport.DuplicateMode</c>.</summary>
public enum DuplicateAction
{
    /// <summary>Ask each time (the default). Entries that cannot ask (command line, API, MCP) skip the download and say so.</summary>
    Ask,

    /// <summary>Add it with a numbered name ("file (1).zip").</summary>
    AddNumbered,

    /// <summary>The old file goes to the Recycle Bin and the new one takes its place. Interactive, always confirmed, never automatic.</summary>
    Overwrite,

    /// <summary>Do not download: show the existing file.</summary>
    OpenExisting,

    /// <summary>Do not add it.</summary>
    Skip,
}

/// <param name="ExistingId">The download in the list that matched, if any (a match in the history or on the disk has none).</param>
/// <param name="ExistingPath">The file already on the disk.</param>
public sealed record DuplicateMatch(DuplicateReason Reason, long? ExistingId, string ExistingPath);

/// <summary>A download that stopped on a duplicate, for the notification ("Download anyway", "Open the existing file", "Remove").</summary>
public sealed record DuplicateNotice(long DownloadId, string FileName, DuplicateMatch Match);

/// <summary>Raised inside a job when the server's answer shows a file already downloaded: nothing was written yet.</summary>
public sealed class DuplicateFoundException(DuplicateMatch match) : Exception("Possible duplicate")
{
    public DuplicateMatch Match { get; } = match;
}

/// <summary>A removed download remembered to recognise it again. Never holds the address, only its keyed hash.</summary>
public sealed record HistoryEntry(string UrlHash, string FileName, string Path, long Size, string? ETag, string? LastModified, DateTime AddedUtc);

/// <summary>The history of removed downloads, as the detector reads it (the store implements it).</summary>
public interface IDuplicateHistory
{
    IReadOnlyList<HistoryEntry> FindByUrl(string normalizedUrl);

    IReadOnlyList<HistoryEntry> FindBySize(long size);
}

/// <summary>
/// The three duplicate rules, shared by every way of adding a download (dialog, browser, batch, and later command line,
/// API and clipboard). Pure logic: the list and the history are passed in.
/// </summary>
public static class DuplicateDetector
{
    /// <summary>The address as compared: no fragment, no login, host in lower case, session parameters out. Null for an invalid one.</summary>
    public static string? NormalizeUrl(string url)
    {
        if (!Urls.TryParseDownload(url, out var parsed))
            return null;
        var (uri, _, _) = Urls.SplitLogin(parsed);
        return CrawlUrls.Key(uri);
    }

    /// <summary>
    /// Rules 1 and 2, before anything is asked of the server: the same address already in the list (or in the history, with
    /// its file still there), or a file with the same name in the destination folder.
    /// </summary>
    /// <param name="fileName">Null when the name is only known after the server answers: rule 2 is then skipped.</param>
    public static DuplicateMatch? CheckBeforeAdd(string url, string directory, string? fileName, IEnumerable<DownloadRecord> items,
        IDuplicateHistory? history, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (NormalizeUrl(url) is not { } key)
            return null;
        foreach (var record in items)
        {
            if (NormalizeUrl(record.Url) == key)
                return new DuplicateMatch(DuplicateReason.SameUrl, record.Id, record.FinalPath ?? "");
        }
        if (history?.FindByUrl(key).FirstOrDefault(e => fileExists(e.Path)) is { } remembered)
            return new DuplicateMatch(DuplicateReason.SameUrl, null, remembered.Path);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var path = Path.Combine(directory, fileName);
            if (fileExists(path))
            {
                var listed = items.FirstOrDefault(r => string.Equals(r.FinalPath, path, StringComparison.OrdinalIgnoreCase));
                return new DuplicateMatch(DuplicateReason.SameName, listed?.Id, path);
            }
        }
        return null;
    }

    /// <summary>
    /// Rule 3, once the server has answered: the same size <b>and</b> the same ETag (without one, the same Last-Modified) as a
    /// finished download or a remembered one whose file is still on the disk. The size alone is never enough.
    /// </summary>
    public static DuplicateMatch? CheckProbe(ProbeResult probe, long excludeId, IEnumerable<DownloadRecord> items,
        IDuplicateHistory? history, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (probe.TotalSize is not > 0)
            return null;
        var size = probe.TotalSize.Value;
        foreach (var record in items)
        {
            if (record.Id != excludeId && record.Status == DownloadStatus.Completed && record.TotalSize == size
                && Same(record.ETag, record.LastModified, probe) && record.FinalPath is { } path && fileExists(path))
                return new DuplicateMatch(DuplicateReason.SameFile, record.Id, path);
        }
        if (history?.FindBySize(size).FirstOrDefault(e => Same(e.ETag, e.LastModified, probe) && fileExists(e.Path)) is { } remembered)
            return new DuplicateMatch(DuplicateReason.SameFile, null, remembered.Path);
        return null;
    }

    private static bool Same(string? etag, string? lastModified, ProbeResult probe)
    {
        if (etag is not null && probe.ETag is not null)
            return etag == probe.ETag;
        return lastModified is not null && probe.LastModified is not null && lastModified == probe.LastModified;
    }
}
