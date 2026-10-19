namespace DownloadManager.Core.Net;

/// <summary>
/// The server (401, or an FTP login refused) or the proxy (407) wants a login the app does not have.
/// The download pauses and the app asks the user; it is never retried with a guess.
/// </summary>
public sealed class AuthenticationRequiredException(Uri target, bool proxy, string scheme, string? realm, string? ftpCode = null)
    : Exception(proxy ? $"The proxy {target.Host} requires a login." : $"{target.Host} requires a login.")
{
    /// <summary>The server or the proxy that asked (for the proxy: the proxy's address).</summary>
    public Uri Target { get; } = target;

    public bool IsProxy { get; } = proxy;

    /// <summary>"Basic", "Digest", "NTLM", "Negotiate" or "FTP".</summary>
    public string Scheme { get; } = scheme;

    public string? Realm { get; } = realm;

    /// <summary>The FTP reply code (530 for a refused login).</summary>
    public string? FtpCode { get; } = ftpCode;
}

/// <summary>The server refuses another connection right now (HTTP 429/503, FTP 421/530 "too many users").</summary>
public sealed class ServerBusyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A TLS certificate that Windows does not trust and that the user did not accept.</summary>
public sealed class UntrustedCertificateException(string host, Exception? inner = null)
    : Exception($"The certificate of {host} is not trusted and was not accepted.", inner)
{
    public string Host { get; } = host;
}

/// <summary>A paused download waiting for a login; shown to the user, answered with DownloadService.ProvideLogin.</summary>
/// <param name="Target">The server, or for <paramref name="IsProxy"/> the proxy, that asked.</param>
/// <param name="DisplayName">The file, so the user knows which download asks.</param>
public sealed record LoginRequest(long DownloadId, Uri Target, bool IsProxy, string Scheme, string? Realm, string DisplayName)
{
    /// <summary>
    /// The password would travel readable (Basic over http, FTP without TLS): the login window asks the user to accept
    /// that explicitly, and only then is the login marked as confirmed for plain text.
    /// </summary>
    public bool SendsPasswordInClear =>
        !IsProxy && Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) && Target.Scheme == "http"
        || Scheme == "FTP" && Target.Scheme == "ftp";
}
