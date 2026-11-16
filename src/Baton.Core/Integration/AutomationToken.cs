using System.Security.Cryptography;

namespace Baton.Core.Integration;

/// <summary>The secret a program must send to the local API (Bearer). Random, URL-safe, 256 bits.</summary>
public static class AutomationToken
{
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Constant-time comparison; an empty expected token never matches.</summary>
    public static bool Matches(string expected, string? presented)
    {
        if (expected.Length == 0 || presented is null)
            return false;
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
