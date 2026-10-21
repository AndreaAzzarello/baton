using System.Security.Cryptography;
using System.Text;

namespace Baton.Core.Security;

/// <summary>
/// Passwords and other secrets at rest: encrypted with the Windows user's DPAPI key, so another Windows account or
/// a copy of the database on another PC cannot read them. Never written to logs or shown in the interface.
/// </summary>
public static class Secrets
{
    // Kept from before the rename to Baton: another value would make every saved secret unreadable.
    private static readonly byte[] Entropy = "DownloadManager.Secret.v1"u8.ToArray();
    private const string Prefix = "dpapi:";

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Secrets are protected with Windows DPAPI.");
        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Null when the value is empty or cannot be decrypted (for example after a Windows profile change).</summary>
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows())
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
