using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace Baton.Core.Net;

/// <summary>
/// Chooses the proxy for every request: none, the one configured in Windows, a manual one (HTTP, HTTPS or SOCKS5)
/// or the one a PAC file picks. Local addresses (localhost, 127.0.0.0/8, ::1) never go through a proxy, and the
/// user's exceptions are honoured. Used by the HTTP stack as its IWebProxy, and by FTP to pick a proxy it can use.
/// </summary>
public sealed class ProxyResolver : IWebProxy
{
    private readonly AppSettings _settings;
    private readonly Regex[] _bypass;
    private readonly bool _bypassPlainNames;
    private readonly ConcurrentDictionary<string, (Uri? Proxy, DateTime Until)> _pacCache = new();
    private PacScript? _pac;

    private ICredentials? _credentials;
    private bool _savedLogin;

    public ProxyResolver(AppSettings settings, PacScript? pac = null)
    {
        _settings = settings;
        _pac = pac;
        PacTrusted = pac is not null;
        var patterns = settings.ProxyBypass.Split([';', ',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        _bypassPlainNames = patterns.Any(p => p.Equals("<local>", StringComparison.OrdinalIgnoreCase));
        _bypass = patterns.Where(p => !p.StartsWith('<'))
            .Select(p => new Regex("^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase))
            .ToArray();
        if (settings.ProxyMode != ProxyMode.None && (settings.ProxyUser.Length > 0 || settings.ProxyPassword.Length > 0))
        {
            _credentials = new NetworkCredential(settings.ProxyUser, settings.ProxyPassword);
            _savedLogin = true;
        }
    }

    /// <summary>
    /// Login for the proxy (407): from the settings, or entered when the proxy asked. The saved one is offered only to a
    /// proxy the user chose: written in the settings, Windows' own, or picked by a PAC file read over https or from this
    /// computer. A PAC file fetched over plain http could have been changed on the way to name another proxy.
    /// </summary>
    public ICredentials? Credentials
    {
        get => _savedLogin && _settings.ProxyMode == ProxyMode.Pac && !PacTrusted ? null : _credentials;
        set
        {
            _credentials = value;
            _savedLogin = false; // entered by the user for the proxy that asked
        }
    }

    /// <summary>The PAC file in use came over https or from a file on this computer.</summary>
    public bool PacTrusted { get; private set; }

    public ProxyMode Mode => _settings.ProxyMode;

    /// <summary>A PAC file that could not be loaded is reported here; connections are then direct.</summary>
    public string? PacError { get; private set; }

    public bool IsBypassed(Uri host) => Resolve(host) is null;

    public Uri? GetProxy(Uri destination) => Resolve(destination);

    /// <summary>The proxy for a destination, or null for a direct connection.</summary>
    public Uri? Resolve(Uri destination)
    {
        if (_settings.ProxyMode == ProxyMode.None || IsLocal(destination) || IsException(destination.Host))
            return null;
        switch (_settings.ProxyMode)
        {
            case ProxyMode.Manual:
                if (_settings.ProxyHost.Length == 0)
                    return null;
                var scheme = _settings.ProxyType switch
                {
                    ProxyType.Https => "https",
                    ProxyType.Socks5 => "socks5",
                    _ => "http",
                };
                return new Uri($"{scheme}://{_settings.ProxyHost}:{_settings.ProxyPort}");

            case ProxyMode.Pac:
                return ResolvePac(destination);

            default:
                // The proxy set in Windows (including its automatic configuration), as read by .NET.
                var system = SystemProxy;
                if (system.IsBypassed(destination))
                    return null;
                var proxy = system.GetProxy(destination);
                return proxy is null || proxy == destination ? null : proxy;
        }
    }

    public static IWebProxy SystemProxy { get; } = HttpClient.DefaultProxy;

    /// <summary>Loads the PAC file of the settings (http, https or a local file); errors leave connections direct.</summary>
    public async Task LoadPacAsync(CancellationToken ct = default)
    {
        if (_settings.ProxyMode != ProxyMode.Pac)
            return;
        try
        {
            _pac = new PacScript(await DownloadPacAsync(_settings.PacUrl, ct));
            PacTrusted = !Uri.TryCreate(_settings.PacUrl, UriKind.Absolute, out var location) || location.Scheme is "https" or "file";
            PacError = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Network, file, syntax or script errors alike: the downloads must not stop because of the PAC file.
            _pac = null;
            PacTrusted = false;
            PacError = ex.Message;
        }
        _pacCache.Clear();
    }

    public static async Task<string> DownloadPacAsync(string location, CancellationToken ct)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            // The PAC file itself is always fetched directly.
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
            return await http.GetStringAsync(uri, ct);
        }
        var path = uri is { IsFile: true } ? uri.LocalPath : location;
        // Never from a network share: reading it would send the user's Windows credentials (NTLM) to that server.
        if (!SafePaths.IsLocal(path))
            throw new IOException(Resources.Strings.PacNotLocal);
        return await File.ReadAllTextAsync(path, ct);
    }

    private Uri? ResolvePac(Uri destination)
    {
        if (_pac is null)
            return null;
        var key = $"{destination.Scheme}://{destination.Host}:{destination.Port}";
        if (_pacCache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow)
            return cached.Proxy;
        Uri? proxy;
        try
        {
            proxy = PacScript.FirstChoice(_pac.FindProxyForUrl(destination));
        }
        catch (Exception)
        {
            proxy = null; // a broken script falls back to a direct connection
        }
        _pacCache[key] = (proxy, DateTime.UtcNow.AddMinutes(5));
        return proxy;
    }

    public static bool IsLocal(Uri destination)
    {
        var host = destination.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    private bool IsException(string host) =>
        (_bypassPlainNames && !host.Contains('.') && !host.Contains(':')) || _bypass.Any(r => r.IsMatch(host));
}
