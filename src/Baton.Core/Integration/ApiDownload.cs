namespace Baton.Core.Integration;

/// <summary>
/// A download as the local API shows it: the cleaned fields of <see cref="DownloadInfo"/> plus <see cref="DisplayUrl"/>, the address
/// reduced to scheme, host and file name (no user name, password, query or fragment), for a script or an agent that only needs to say
/// which download it means. <c>url</c> stays the full address, as it has been since 1.4.1.
/// </summary>
public sealed record ApiDownload(long Id, string Name, string Url, string DisplayUrl, string Folder, string? Path, string Status, long Downloaded,
    long? TotalSize, string? Error, string? Queue, string? Category, DateTime AddedUtc, DateTime? CompletedUtc)
{
    public static ApiDownload From(DownloadInfo info)
    {
        var d = AgentText.Safe(info);
        return new ApiDownload(d.Id, d.Name, d.Url, AgentText.Address(d.Url), d.Folder, d.Path, d.Status, d.Downloaded, d.TotalSize, d.Error, d.Queue,
            d.Category, d.AddedUtc, d.CompletedUtc);
    }
}
