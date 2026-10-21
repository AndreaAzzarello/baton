using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Baton.Core.Security;

public enum CertificateDecision
{
    Reject,

    /// <summary>Until the app closes.</summary>
    AcceptOnce,

    /// <summary>Saved: this exact certificate (by SHA-256 fingerprint) is accepted for this server from now on.</summary>
    AcceptAlways,
}

/// <summary>What the user sees before deciding about a certificate that Windows does not trust.</summary>
public sealed record CertificateQuestion(
    string Host,
    int Port,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Fingerprint,
    SslPolicyErrors Errors);

public sealed record TrustedCertificate(string Host, int Port, string Fingerprint, string Subject, DateTime AddedUtc);

/// <summary>
/// Certificates of FTPS servers. A certificate Windows trusts for that host passes; any other one is never accepted
/// silently: it passes only if the user accepted that exact certificate for that server, otherwise the user is asked.
/// Without anyone to ask (tests, unattended runs) it is refused.
/// </summary>
public sealed class CertificateTrust(DownloadStore? store)
{
    private readonly ConcurrentDictionary<string, Task<CertificateDecision>> _pending = new();
    private readonly ConcurrentDictionary<string, bool> _acceptedOnce = new();
    private readonly List<TrustedCertificate> _trusted = store?.LoadTrustedCertificates() ?? [];
    private readonly object _lock = new();

    /// <summary>Asks the user; set by the app. Several connections to the same server share one question.</summary>
    public Func<CertificateQuestion, Task<CertificateDecision>>? Ask { get; set; }

    public IReadOnlyList<TrustedCertificate> Trusted
    {
        get { lock (_lock) return _trusted.ToList(); }
    }

    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>Called by the TLS handshake on a worker thread; may wait for the user's answer.</summary>
    public bool Validate(string host, int port, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null)
            return false;
        if (errors == SslPolicyErrors.None)
            return true;

        var fingerprint = FingerprintOf(certificate);
        var key = $"{host.ToLowerInvariant()}:{port}:{fingerprint}";
        lock (_lock)
        {
            if (_trusted.Any(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase) && t.Port == port && t.Fingerprint == fingerprint))
                return true;
        }
        if (_acceptedOnce.ContainsKey(key))
            return true;
        if (Ask is not { } ask)
            return false;

        var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var question = new CertificateQuestion(host, port, cert.Subject, cert.Issuer, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(),
            fingerprint, errors);
        var decision = _pending.GetOrAdd(key, _ => ask(question)).GetAwaiter().GetResult();
        _pending.TryRemove(key, out _);
        switch (decision)
        {
            case CertificateDecision.AcceptAlways:
                Remember(new TrustedCertificate(host, port, fingerprint, cert.Subject, DateTime.UtcNow));
                return true;
            case CertificateDecision.AcceptOnce:
                _acceptedOnce[key] = true;
                return true;
            default:
                return false;
        }
    }

    public void Remember(TrustedCertificate certificate)
    {
        lock (_lock)
        {
            if (_trusted.Any(t => t.Host == certificate.Host && t.Port == certificate.Port && t.Fingerprint == certificate.Fingerprint))
                return;
            _trusted.Add(certificate);
            store?.SaveTrustedCertificate(certificate);
        }
    }

    public void Forget(TrustedCertificate certificate)
    {
        lock (_lock)
        {
            _trusted.RemoveAll(t => t.Host == certificate.Host && t.Port == certificate.Port && t.Fingerprint == certificate.Fingerprint);
            store?.DeleteTrustedCertificate(certificate);
        }
    }
}
