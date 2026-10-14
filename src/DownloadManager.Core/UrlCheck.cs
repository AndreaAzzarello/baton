using DownloadManager.Core.Resources;

namespace DownloadManager.Core;

public enum UrlMatch
{
    /// <summary>Nothing downloaded yet: the address can simply be replaced.</summary>
    NothingToKeep,

    /// <summary>Same size and same ETag or Last-Modified: the downloaded data can be kept.</summary>
    Same,

    /// <summary>Same size, but the server gives no validator to prove it is the same file.</summary>
    Unverifiable,

    /// <summary>A different file, or a server that cannot resume: the download must start again.</summary>
    Different,
}

/// <summary>What the server at a new address says about the file, compared with the partial download.</summary>
public sealed record UrlCheck(string NewUrl, ProbeResult Probe, UrlMatch Match, string Reason)
{
    public static UrlCheck Compare(DownloadRecord record, string newUrl, ProbeResult probe)
    {
        if (record.Status != DownloadStatus.Completed && record.Segments.All(s => s.Written == 0))
            return new(newUrl, probe, UrlMatch.NothingToKeep, Strings.UrlCheckNothingToKeep);
        if (!probe.SupportsRanges)
            return new(newUrl, probe, UrlMatch.Different, Strings.UrlCheckNoRanges);
        if (record.TotalSize != probe.TotalSize)
            return new(newUrl, probe, UrlMatch.Different, Strings.UrlCheckSizeDiffers);

        if (record.ETag is not null && probe.ETag is not null)
            return record.ETag == probe.ETag
                ? new(newUrl, probe, UrlMatch.Same, Strings.UrlCheckSameETag)
                : new(newUrl, probe, UrlMatch.Different, Strings.UrlCheckETagDiffers);
        if (record.LastModified is not null && probe.LastModified is not null)
            return record.LastModified == probe.LastModified
                ? new(newUrl, probe, UrlMatch.Same, Strings.UrlCheckSameDate)
                : new(newUrl, probe, UrlMatch.Different, Strings.UrlCheckDateDiffers);

        return new(newUrl, probe, UrlMatch.Unverifiable, Strings.UrlCheckUnverifiable);
    }
}
