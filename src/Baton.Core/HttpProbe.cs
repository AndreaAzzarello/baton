using System.Net;
using System.Net.Http.Headers;
using Baton.Core.Resources;
using System.Globalization;

namespace Baton.Core;

public sealed record ProbeResult(long? TotalSize, bool SupportsRanges, string? ETag, string? LastModified, string FileName, string? ContentType = null);

/// <summary>
/// Asks for the first byte only. A 206 with a total length proves range support;
/// a 200 means the server ignores ranges and the file must be fetched in one stream.
/// </summary>
public static class HttpProbe
{
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, string url, CancellationToken ct, RequestContext? context = null,
        TimeSpan? timeout = null, Net.UrlGuard? guard = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (guard is not null)
            await guard.PrepareAsync(request, ct);
        context.ApplyTo(request);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await HttpErrors.SendAsync(http, request, timeout ?? TimeSpan.FromSeconds(30), ct);

        // Even the first byte is out of range only for an empty file ("Content-Range: bytes */0").
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == 0)
        {
            var uri = response.RequestMessage?.RequestUri ?? new Uri(url);
            return new ProbeResult(0, true, response.Headers.ETag is { IsWeak: false } t ? t.Tag : null, null,
                FileNames.FromResponse(response.Content.Headers.ContentDisposition, uri), response.Content.Headers.ContentType?.MediaType);
        }

        if (!response.IsSuccessStatusCode)
            throw HttpErrors.FromResponse(response);

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
        return new ProbeResult(total, ranges, etag, lastModified, name, response.Content.Headers.ContentType?.MediaType);
    }
}

internal static class HttpErrors
{
    /// <summary>
    /// Sends a request and waits for the response headers at most <paramref name="timeout"/>: a server that accepts
    /// the connection but never answers must not hold a download forever. The timeout counts as a transient error.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(CultureInfo.CurrentCulture, Strings.ErrorNoAnswer, timeout.TotalSeconds));
        }
    }

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>The error for a failed response; 401 and 407 ask for a login instead of failing.</summary>
    public static Exception FromResponse(HttpResponseMessage response)
    {
        var target = response.RequestMessage?.RequestUri;
        if (response.StatusCode == HttpStatusCode.Unauthorized && target is not null)
        {
            var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
            return new Net.AuthenticationRequiredException(target, false, challenge?.Scheme ?? "Basic", RealmOf(challenge));
        }
        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired && target is not null)
        {
            var challenge = response.Headers.ProxyAuthenticate.FirstOrDefault();
            return new Net.AuthenticationRequiredException(target, true, challenge?.Scheme ?? "Basic", RealmOf(challenge));
        }
        return FromStatus(response.StatusCode);
    }

    private static string? RealmOf(System.Net.Http.Headers.AuthenticationHeaderValue? challenge)
    {
        var parameter = challenge?.Parameter;
        var at = parameter?.IndexOf("realm=", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0)
            return null;
        var value = parameter![(at + 6)..].TrimStart('"');
        var end = value.IndexOf('"');
        return end >= 0 ? value[..end] : value.Split(',')[0];
    }

    public static Exception FromStatus(HttpStatusCode status)
    {
        var message = $"The server responded {(int)status} {status}.";
        return IsTransient(status)
            ? new HttpRequestException(message, null, status)
            : new DownloadFailedException(message);
    }
}
