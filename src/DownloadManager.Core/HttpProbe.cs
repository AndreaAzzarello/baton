using System.Net;
using System.Net.Http.Headers;

namespace DownloadManager.Core;

public sealed record ProbeResult(long? TotalSize, bool SupportsRanges, string? ETag, string? LastModified, string FileName);

/// <summary>
/// Asks for the first byte only. A 206 with a total length proves range support;
/// a 200 means the server ignores ranges and the file must be fetched in one stream.
/// </summary>
public static class HttpProbe
{
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, string url, CancellationToken ct, RequestContext? context = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        context.ApplyTo(request);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // Even the first byte is out of range only for an empty file ("Content-Range: bytes */0").
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == 0)
        {
            var uri = response.RequestMessage?.RequestUri ?? new Uri(url);
            return new ProbeResult(0, true, response.Headers.ETag is { IsWeak: false } t ? t.Tag : null, null,
                FileNames.FromResponse(response.Content.Headers.ContentDisposition, uri));
        }

        if (!response.IsSuccessStatusCode)
            throw HttpErrors.FromStatus(response.StatusCode);

        long? total;
        bool ranges;
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is { } length)
        {
            total = length;
            ranges = true;
        }
        else
        {
            total = response.Content.Headers.ContentLength;
            ranges = false;
        }

        // A weak validator cannot be used with If-Range, so it is not kept.
        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.Tag : null;
        var lastModified = response.Content.Headers.LastModified?.ToString("R");
        var finalUri = response.RequestMessage?.RequestUri ?? new Uri(url);
        var name = FileNames.FromResponse(response.Content.Headers.ContentDisposition, finalUri);
        return new ProbeResult(total, ranges, etag, lastModified, name);
    }
}

internal static class HttpErrors
{
    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    public static Exception FromStatus(HttpStatusCode status)
    {
        var message = $"The server responded {(int)status} {status}.";
        return IsTransient(status)
            ? new HttpRequestException(message, null, status)
            : new DownloadFailedException(message);
    }
}
