namespace Baton.Core;

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
