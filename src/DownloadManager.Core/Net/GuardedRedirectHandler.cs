using System.Net;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core.Net;

/// <summary>
/// Redirects of requests marked <see cref="UrlGuard.PublicOnly"/> (a web page's video) are followed here, each target
/// checked before it is requested: the connection-time check cannot see the site behind a proxy, and the handler's own
/// redirects would let a public address lead to a private one there. Every other request goes to the ordinary
/// handler, with its usual redirects.
/// </summary>
public sealed class GuardedRedirectHandler : DelegatingHandler
{
    public const int MaxRedirects = 10;

    private readonly HttpMessageInvoker _withoutRedirects;

    /// <param name="followsRedirects">The ordinary handler.</param>
    /// <param name="withoutRedirects">The same network settings (proxy, logins, cookies) with automatic redirects off.</param>
    public GuardedRedirectHandler(HttpMessageHandler followsRedirects, HttpMessageHandler withoutRedirects) : base(followsRedirects) =>
        _withoutRedirects = new HttpMessageInvoker(withoutRedirects, disposeHandler: true);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.Options.TryGetValue(UrlGuard.PublicOnly, out var publicOnly) || !publicOnly)
            return await base.SendAsync(request, ct);
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await _withoutRedirects.SendAsync(current, ct);
            if (hop == MaxRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location
                || current.Method != HttpMethod.Get && current.Method != HttpMethod.Head)
                return response;
            var next = new Uri(current.RequestUri!, location);
            // As the handler itself: no other schemes, and never from https down to http.
            if (next.Scheme is not ("http" or "https") || current.RequestUri!.Scheme == "https" && next.Scheme == "http")
                return response;
            response.Dispose();
            if (await UrlGuard.IsPrivateHostAsync(next.IdnHost, ct))
                throw new HttpRequestException(Strings.MediaErrorPrivate);
            var follow = new HttpRequestMessage(current.Method, next) { Version = current.Version, VersionPolicy = current.VersionPolicy };
            // Referer, User-Agent, Range...; a login or cookies are added by the handler for each host, never carried over.
            foreach (var header in current.Headers)
            {
                if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    follow.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            follow.Options.Set(UrlGuard.PublicOnly, true);
            if (!ReferenceEquals(current, request))
                current.Dispose();
            current = follow;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _withoutRedirects.Dispose();
        base.Dispose(disposing);
    }
}
