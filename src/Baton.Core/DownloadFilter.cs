namespace Baton.Core;

public enum StatusFilter
{
    All,
    Unfinished,
    Finished,
}

/// <summary>The side panel selection plus the search box: which downloads the list shows.</summary>
public sealed record DownloadFilter(StatusFilter Status = StatusFilter.All, long? CategoryId = null, string? Search = null, long? QueueId = null)
{
    public bool Matches(DownloadStatus status, long? categoryId, string name, string url, long? queueId = null)
    {
        if (QueueId is { } queue && queueId != queue)
            return false;
        if (Status == StatusFilter.Finished && status != DownloadStatus.Completed)
            return false;
        if (Status == StatusFilter.Unfinished && status == DownloadStatus.Completed)
            return false;
        if (CategoryId is { } category && categoryId != category)
            return false;
        if (string.IsNullOrWhiteSpace(Search))
            return true;

        // Every word must appear in the name or the address, in any order.
        return Search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => name.Contains(word, StringComparison.CurrentCultureIgnoreCase) || url.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public bool Matches(DownloadSnapshot s) => Matches(s.Status, s.CategoryId, s.DisplayName, s.Url, s.QueueId);
}
