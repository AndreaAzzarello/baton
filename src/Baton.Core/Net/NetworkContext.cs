using System.Net;
using Baton.Core.Security;

namespace Baton.Core.Net;

/// <summary>
/// The network as configured: proxy, saved logins, trusted certificates, timeouts, connections per server and
/// user agent. Builds the HTTP clients and the transport for each download. Rebuilt when the settings change.
/// </summary>
public sealed class NetworkContext
{
    private readonly StoredCredentials _stored;
    private HttpClient? _shared;

    public NetworkContext(AppSettings settings, CredentialStore credentials, CertificateTrust certificates,
        NetworkCredential? sessionProxyLogin = null, PacScript? pac = null)
    {
        Settings = settings.Clone();
        Credentials = credentials;
        Certificates = certificates;
        Proxy = new ProxyResolver(Settings, pac);
        if (sessionProxyLogin is not null)
            Proxy.Credentials = sessionProxyLogin;
        _stored = new StoredCredentials(credentials);
    }

    /// <summary>No proxy, no saved logins, certificates checked by Windows only: for tests and the static helpers.</summary>
    public static NetworkContext Default { get; } =
        new(new AppSettings { ProxyMode = ProxyMode.None }, new CredentialStore(null), new CertificateTrust(null));

    public AppSettings Settings { get; }
    public ProxyResolver Proxy { get; }
    public CredentialStore Credentials { get; }
    public CertificateTrust Certificates { get; }

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(Settings.TimeoutSeconds, 5, 600));

    /// <summary>The client shared by downloads that carry no cookies or login of their own.</summary>
    public HttpClient SharedHttp => _shared ??= CreateHttpClient();

    /// <summary>
    /// A client with this network's proxy and logins. Browser cookies and a login given for one download get a client
    /// of their own, so they reach only their own site (also after a redirect to another host). Without
    /// <paramref name="followRedirects"/> each redirect comes back to the caller, which checks where it leads (the grabber).
    /// </summary>
    public HttpClient CreateHttpClient(CookieContainer? cookies = null, NetworkCredential? ownLogin = null, Uri? ownLoginFor = null,
        bool followRedirects = true)
    {
        ICredentials credentials = ownLogin is not null && ownLoginFor is not null ? new OneHostCredential(ownLoginFor, ownLogin, _stored) : _stored;
        SocketsHttpHandler Handler(bool redirects)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = redirects,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.None, // byte ranges refer to the raw entity
                ConnectTimeout = Timeout < TimeSpan.FromSeconds(20) ? Timeout : TimeSpan.FromSeconds(20),
                MaxConnectionsPerServer = Math.Clamp(Settings.MaxConnectionsPerServer, 1, 256),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseProxy = Settings.ProxyMode != ProxyMode.None,
                Proxy = Settings.ProxyMode != ProxyMode.None ? Proxy : null,
                Credentials = credentials,
                PreAuthenticate = false,
            };
            handler.ConnectCallback = (connection, ct) => UrlGuard.ConnectAsync(connection, IsProxyEndpoint(connection), ct);
            if (cookies is not null)
                handler.CookieContainer = cookies;
            return handler;
        }
        HttpMessageHandler top = followRedirects ? new GuardedRedirectHandler(Handler(true), Handler(false)) : Handler(false);
        var client = new HttpClient(top) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var agent = string.IsNullOrWhiteSpace(Settings.UserAgent) ? AppSettings.DefaultUserAgent : Settings.UserAgent.Trim();
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(agent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppSettings.DefaultUserAgent);
        return client;
    }

    /// <summary>The connection goes to the proxy chosen for the request (an address the user configured), not to the site.</summary>
    private bool IsProxyEndpoint(SocketsHttpConnectionContext connection) =>
        Settings.ProxyMode != ProxyMode.None && connection.InitialRequestMessage.RequestUri is { } target && Proxy.GetProxy(target) is { } proxy
        && string.Equals(proxy.Host, connection.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase) && proxy.Port == connection.DnsEndPoint.Port;

    /// <summary>The transport for a download: HTTP(S) or FTP(S), with the download's own cookies and login when it has them.</summary>
    public ITransport CreateTransport(DownloadRecord record, HttpClient? shared = null)
    {
        var url = new Uri(record.Url);
        var context = record.Context;
        var ownLogin = context is { Username: { Length: > 0 } user } ? new NetworkCredential(user, context.Password ?? "") : null;

        if (FtpTransport.IsFtp(url))
        {
            var saved = Credentials.Find(url.Scheme, url.Host, FtpTransport.PortOf(url));
            // FTP without TLS sends the password readable: only a login the user confirmed for that.
            if (saved is not null && url.Scheme == "ftp" && !saved.PlainConfirmed)
                saved = null;
            var login = ownLogin
                        ?? (saved is not null ? new NetworkCredential(saved.Username, saved.Password) : null)
                        ?? new NetworkCredential("anonymous", "anonymous@");
            return new FtpTransport(url, login, Settings, Certificates, Proxy);
        }

        var cookies = context is { Cookies.Count: > 0 } ? RequestContexts.ToCookieContainer(context.Cookies) : null;
        if (cookies is null && ownLogin is null)
            return new HttpTransport(shared ?? SharedHttp, false, record.Url, context, Timeout);
        return new HttpTransport(CreateHttpClient(cookies, ownLogin, url), true, record.Url, context, Timeout);
    }

    /// <summary>Probes an address the way a download would, with this network (used by "update address").</summary>
    public async Task<ProbeResult> ProbeAsync(DownloadRecord record, CancellationToken ct)
    {
        using var transport = CreateTransport(record);
        return await transport.ProbeAsync(ct);
    }

    /// <summary>A login given for one download: offered to its own scheme and host only, the saved logins elsewhere.</summary>
    private sealed class OneHostCredential(Uri host, NetworkCredential login, ICredentials fallback) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase) && uri.Scheme == host.Scheme ? login : fallback.GetCredential(uri, authType);
    }
}
