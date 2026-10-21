using System.Net;

namespace Baton.Core.Security;

/// <summary>
/// A login for a server (HTTP authentication or FTP), valid for one scheme, host and port: a login saved for https or
/// FTPS is never offered over plain http or ftp. The password is decrypted only in memory.
/// </summary>
public sealed class SiteCredential
{
    public long Id { get; set; }

    /// <summary>"https", "http", "ftps", "ftpes" or "ftp" (lower case).</summary>
    public string Scheme { get; set; } = "https";

    /// <summary>
    /// The user accepted that this password travels readable on the network (Basic over http, FTP without TLS).
    /// Without it the login is not used there and the app asks, with a warning.
    /// </summary>
    public bool PlainConfirmed { get; set; }

    /// <summary>Host name as in the URL, compared without regard to case.</summary>
    public string Host { get; set; } = "";

    /// <summary>Null: any port of the host.</summary>
    public int? Port { get; set; }

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>For NTLM/Negotiate on an intranet: the signed-in Windows account answers instead of a password.</summary>
    public bool UseWindowsAccount { get; set; }

    public DateTime AddedUtc { get; set; }

    public bool Matches(string scheme, string host, int port) =>
        string.Equals(Scheme, scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase)
        && (Port is null || Port == port);

    /// <summary>Schemes on which a password can be sent readable: http (with Basic) and ftp.</summary>
    public static bool IsPlainScheme(string scheme) => scheme.ToLowerInvariant() is "http" or "ftp";

    /// <summary>The schemes a login can be saved for.</summary>
    public static readonly string[] Schemes = ["https", "http", "ftps", "ftpes", "ftp"];

    public SiteCredential Clone() => (SiteCredential)MemberwiseClone();

    /// <summary>For lists and screen readers: never includes the password.</summary>
    public override string ToString() => $"{Username} @ {Scheme}://{Host}{(Port is { } p ? $":{p}" : "")}";
}

/// <summary>
/// Saved logins (encrypted in the database) plus logins entered with "Remember" unchecked, which live only until
/// the app closes. A login is offered only to the host it belongs to.
/// </summary>
public sealed class CredentialStore
{
    private readonly object _lock = new();
    private readonly DownloadStore? _store;
    private readonly List<SiteCredential> _saved;
    private readonly List<SiteCredential> _session = [];

    public CredentialStore(DownloadStore? store)
    {
        _store = store;
        _saved = store?.LoadCredentials() ?? [];
    }

    public event Action? Changed;

    public IReadOnlyList<SiteCredential> Saved
    {
        get { lock (_lock) return _saved.Select(c => c.Clone()).ToList(); }
    }

    public SiteCredential? Find(string scheme, string host, int port)
    {
        lock (_lock)
        {
            // The most specific wins: session before saved, a port-specific entry before a host-wide one.
            return _session.Concat(_saved)
                .Where(c => c.Matches(scheme, host, port))
                .OrderBy(c => c.Port is null)
                .FirstOrDefault()?.Clone();
        }
    }

    /// <summary>Adds or replaces the login for the host (and port); with <paramref name="remember"/> it is saved encrypted.</summary>
    public SiteCredential Save(SiteCredential credential, bool remember)
    {
        SiteCredential saved;
        lock (_lock)
        {
            var list = remember ? _saved : _session;
            var existing = credential.Id != 0
                ? _saved.FirstOrDefault(c => c.Id == credential.Id)
                : list.FirstOrDefault(c => SameServer(c, credential));
            saved = credential.Clone();
            saved.Host = saved.Host.Trim();
            saved.Scheme = saved.Scheme.Trim().ToLowerInvariant();
            saved.Id = existing?.Id ?? 0;
            saved.AddedUtc = existing?.AddedUtc ?? DateTime.UtcNow;
            if (remember)
            {
                _store?.SaveCredential(saved);
                if (existing is not null)
                    _saved[_saved.IndexOf(existing)] = saved;
                else
                    _saved.Add(saved);
                // A remembered login replaces a temporary one for the same server.
                _session.RemoveAll(c => SameServer(c, saved));
            }
            else
            {
                if (existing is not null)
                    _session.Remove(existing);
                _session.Add(saved);
            }
        }
        Changed?.Invoke();
        return saved.Clone();
    }

    private static bool SameServer(SiteCredential a, SiteCredential b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public void Delete(long id)
    {
        lock (_lock)
        {
            if (_saved.FirstOrDefault(c => c.Id == id) is not { } credential)
                return;
            _store?.DeleteCredential(id);
            _saved.Remove(credential);
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Hands saved logins to the HTTP stack (Basic, Digest, NTLM, Negotiate), per scheme, host and port. Over plain http a
/// Basic password (readable on the network) is given only if the user confirmed it for that server; Digest and the
/// Windows schemes never send the password itself.
/// </summary>
public sealed class StoredCredentials(CredentialStore store) : ICredentials
{
    private static readonly string[] IntegratedSchemes = ["NTLM", "Negotiate", "Kerberos"];

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        if (store.Find(uri.Scheme, uri.Host, uri.Port) is not { } credential)
            return null;
        if (uri.Scheme == "http" && authType.Equals("Basic", StringComparison.OrdinalIgnoreCase) && !credential.PlainConfirmed)
            return null;
        // The Windows account is offered only to integrated schemes, never sent as a Basic password.
        if (credential.UseWindowsAccount)
            return IntegratedSchemes.Contains(authType, StringComparer.OrdinalIgnoreCase) ? CredentialCache.DefaultNetworkCredentials : null;
        return new NetworkCredential(credential.Username, credential.Password);
    }
}
