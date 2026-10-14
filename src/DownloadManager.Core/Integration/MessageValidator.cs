namespace DownloadManager.Core.Integration;

/// <summary>
/// Checks every message from the browser before the app acts on it. The extension runs in a browser that visits
/// any site, so its messages are treated as untrusted input: known types only, supported address schemes only,
/// bounded sizes everywhere, no control characters in names and headers.
/// </summary>
public static class MessageValidator
{
    public const int MaxUrlLength = 8192;
    public const int MaxTextLength = 2048;
    public const int MaxCookies = 300;
    public const int MaxCookieValue = 8192;
    public const int MaxBodyLength = 1024 * 1024;
    public const int MaxBatchItems = 10_000;
    public const int MaxCookieSitesInBatch = 50;

    private static readonly string[] KnownTypes =
        [Messages.Ping, Messages.Add, Messages.Activate, Messages.Queues, Messages.Hello, Messages.ProbeMedia, Messages.AddMedia, Messages.Batch];

    private static readonly string[] BodyTypes = [Net.FormBody.UrlEncoded, Net.FormBody.Multipart];

    /// <summary>Null when the message may be handled; otherwise why it is refused.</summary>
    public static string? Validate(ChannelMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Type))
            return "Missing message type.";
        if (!KnownTypes.Contains(message.Type))
            return $"Unknown message type: {Clip(message.Type)}";
        if (message.Version is < 1 or > 1000)
            return "Invalid protocol version.";

        switch (message.Type)
        {
            case Messages.Add:
                return message.Download is { } download ? ValidateDownload(download) : "Missing download.";
            case Messages.ProbeMedia:
            case Messages.AddMedia:
                if (message.Media is not { } media)
                    return "Missing media.";
                return ValidateMedia(media, needsVariant: message.Type == Messages.AddMedia);
            case Messages.Batch:
                return message.Batch is { } batch ? ValidateBatch(batch) : "Missing page.";
            default:
                return null;
        }
    }

    private static string? ValidateDownload(BrowserDownload d)
    {
        if (DownloadUrl(d.Url) is { } urlError)
            return urlError;
        if ((Text(d.FileName, 255) ?? Text(d.Referrer, MaxUrlLength) ?? Text(d.UserAgent, 1024) ?? Text(d.Queue, 200)) is { } textError)
            return textError;
        if (d.Referrer is { Length: > 0 } referrer && !IsWeb(referrer))
            return "The referrer must be an http or https address.";
        if (Cookies(d.Cookies) is { } cookieError)
            return cookieError;
        if (d.Method is { } method && method is not ("GET" or "POST"))
            return "Only GET and POST requests are supported.";
        if (d.Body is not null)
        {
            if (d.Method != "POST")
                return "A body is only allowed with POST.";
            if (d.Body.Length > MaxBodyLength)
                return "The form data is too large.";
            if (d.BodyType is not { } type || !BodyTypes.Contains(type))
                return "Unsupported form encoding.";
            if (!Uri.TryCreate(d.Url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
                return "Forms can only be sent to http or https addresses.";
        }
        return null;
    }

    private static string? ValidateMedia(MediaRequest m, bool needsVariant)
    {
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || m.Url.Length > MaxUrlLength)
            return "Media must come from an http or https address.";
        if (m.PageUrl is { Length: > 0 } page && (!IsWeb(page) || page.Length > MaxUrlLength))
            return "Invalid page address.";
        if (m.Referrer is { Length: > 0 } referrer && !IsWeb(referrer))
            return "The referrer must be an http or https address.";
        if ((Text(m.Title, 512) ?? Text(m.Referrer, MaxUrlLength) ?? Text(m.UserAgent, 1024) ?? Text(m.Queue, 200) ?? Text(m.VariantId, 200) ?? Text(m.Label, 200)) is { } textError)
            return textError;
        if (needsVariant && string.IsNullOrEmpty(m.VariantId))
            return "Missing quality.";
        return Cookies(m.Cookies);
    }

    private static string? ValidateBatch(BatchRequest b)
    {
        if (!IsWeb(b.PageUrl) || b.PageUrl.Length > MaxUrlLength)
            return "Invalid page address.";
        if (b.Items is null || b.Items.Count == 0)
            return "No links.";
        if (b.Items.Count > MaxBatchItems)
            return $"Too many links (at most {MaxBatchItems}).";
        if ((Text(b.PageTitle, 512) ?? Text(b.UserAgent, 1024) ?? Text(b.Queue, 200)) is { } textError)
            return textError;
        foreach (var item in b.Items)
        {
            // Links the app cannot download are dropped by the window, not an error: a page has many kinds of links.
            if (item.Url is null || item.Url.Length > MaxUrlLength || HasControl(item.Url) || Text(item.Text, MaxTextLength) is not null)
                return "Invalid link.";
            if (item.Kind is { } kind && kind is not ("link" or "image"))
                return "Invalid link kind.";
        }
        if (b.Cookies is { } cookies && cookies.Select(c => c.Domain).Distinct().Count() > MaxCookieSitesInBatch)
            return "Too many sites in the cookies.";
        return Cookies(b.Cookies, MaxCookies * 4);
    }

    private static string? DownloadUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength)
            return "Invalid address.";
        return Urls.TryParseDownload(url, out _) ? null : "Only http, https, ftp, ftps and ftpes addresses are supported.";
    }

    private static string? Cookies(List<BrowserCookie>? cookies, int max = MaxCookies)
    {
        if (cookies is null)
            return null;
        if (cookies.Count > max)
            return "Too many cookies.";
        foreach (var c in cookies)
        {
            if (c is null || string.IsNullOrEmpty(c.Name) || c.Name.Length > 1024 || (c.Value?.Length ?? 0) > MaxCookieValue
                || HasControl(c.Name) || HasControl(c.Value) || string.IsNullOrEmpty(c.Domain) || c.Domain.Length > 255 || HasControl(c.Domain)
                || (c.Path?.Length ?? 0) > 2048 || HasControl(c.Path))
                return "Invalid cookie.";
        }
        return null;
    }

    private static string? Text(string? value, int max) =>
        value is null ? null : value.Length > max ? "A text field is too long." : HasControl(value) ? "A text field contains control characters." : null;

    private static bool HasControl(string? value) => value is not null && value.Any(c => c < 0x20 || c == 0x7F);

    private static bool IsWeb(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string Clip(string value) => value.Length > 40 ? value[..40] + "…" : value;
}
