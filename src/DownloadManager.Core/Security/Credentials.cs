using System.Net;

namespace DownloadManager.Core.Security;

/// <summary>A login for a server (HTTP authentication or FTP). The password is decrypted only in memory.</summary>
public sealed class SiteCredential
{
    public long Id { get; set; }

    /// <summary>Host name as in the URL, compared without regard to case.</summary>
    public string Host { get; set; } = "";

    /// <summary>Null: any port of the host.</summary>
    public int? Port { get; set; }

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>For NTLM/Negotiate on an intranet: the signed-in Windows account answers instead of a password.</summary>
    public bool UseWindowsAccount { get; set; }

    public DateTime AddedUtc { get; set; }

    public bool Matches(string host, int port) =>
        string.Equals(Host, host, StringComparison.OrdinalIgnoreCase) && (Port is null || Port == port);

    public SiteCredential Clone() => (SiteCredential)MemberwiseClone();

    /// <summary>For lists and screen readers: never includes the password.</summary>
    public override string ToString() => $"{Username} @ {Host}{(Port is { } p ? $":{p}" : "")}";
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

    public SiteCredential? Find(string host, int port)
    {
        lock (_lock)
        {
            // The most specific wins: session before saved, a port-specific entry before a host-wide one.
            return _session.Concat(_saved)
                .Where(c => c.Matches(host, port))
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
                : list.FirstOrDefault(c => string.Equals(c.Host, credential.Host, StringComparison.OrdinalIgnoreCase) && c.Port == credential.Port);
            saved = credential.Clone();
            saved.Host = saved.Host.Trim();
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
                _session.RemoveAll(c => string.Equals(c.Host, saved.Host, StringComparison.OrdinalIgnoreCase) && c.Port == saved.Port);
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

/// <summary>Hands saved logins to the HTTP stack (Basic, Digest, NTLM, Negotiate), per host.</summary>
public sealed class StoredCredentials(CredentialStore store) : ICredentials, ICredentialsByHost
{
    private static readonly string[] IntegratedSchemes = ["NTLM", "Negotiate", "Kerberos"];

    public NetworkCredential? GetCredential(Uri uri, string authType) => Lookup(uri.Host, uri.Port, authType);

    public NetworkCredential? GetCredential(string host, int port, string authenticationType) => Lookup(host, port, authenticationType);

    private NetworkCredential? Lookup(string host, int port, string authType)
    {
        if (store.Find(host, port) is not { } credential)
            return null;
        // The Windows account is offered only to integrated schemes, never sent as a Basic password.
        if (credential.UseWindowsAccount)
            return IntegratedSchemes.Contains(authType, StringComparer.OrdinalIgnoreCase) ? CredentialCache.DefaultNetworkCredentials : null;
        return new NetworkCredential(credential.Username, credential.Password);
    }
}
