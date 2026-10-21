using System.Security.Cryptography;
using System.Text;

namespace Baton.Core.Updates;

/// <summary>A public key whose signatures an update may carry: the curve point X||Y (P-256, 64 bytes).</summary>
public sealed record ReleaseKey(string Id, byte[] Point)
{
    /// <summary>The key's short name in a ".sig" file: the first 8 bytes of SHA-256(X||Y), in hex.</summary>
    public static string IdOf(byte[] point) => Convert.ToHexStringLower(SHA256.HashData(point))[..16];

    /// <summary>A key from its base64 point, or null when it is not a point of P-256.</summary>
    public static ReleaseKey? FromBase64(string text)
    {
        byte[] point;
        try
        {
            point = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        if (point.Length != 64)
            return null;
        try
        {
            using var check = Create(point); // throws for a point that is not on the curve
            return new ReleaseKey(IdOf(point), point);
        }
        // Windows (CNG) reports a point off the curve as "curve not supported"; P-256 itself always is.
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    internal static ECDsa Create(byte[] point) => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = point[..32], Y = point[32..] },
    });
}

/// <summary>
/// Signed updates (from 1.1.0). The author signs, with ECDSA P-256 / SHA-256, a short statement that ties together the
/// version, the program's file name and its SHA-256, so an old signed release cannot be passed off as a newer one and
/// one program cannot stand in for another. The signatures travel in "&lt;exe&gt;.sig" (a GitHub release asset) or in the
/// manifest's "signature" field: one line "&lt;key id&gt; &lt;base64 signature&gt;" per key, so a release can be signed with
/// both the old and the new key while keys are rotated. The accepted public keys are built into the app
/// (Updates\release-keys.txt, one base64 X||Y point per line); a key is retired by leaving it out of a later version.
/// </summary>
public static class UpdateSignature
{
    /// <summary>The keys built into this version of the app.</summary>
    public static IReadOnlyList<ReleaseKey> Trusted { get; } = Load();

    // "DownloadManager update 1" is the statement's format name from before the rename: versions up to 1.1 check it.
    public static string Statement(Version version, string file, string sha256) =>
        $"DownloadManager update 1\nversion={version.ToString(3)}\nfile={file}\nsha256={sha256.ToLowerInvariant()}\n";

    /// <summary>True when one line of <paramref name="signatures"/> is a valid signature of the statement by one of the keys.</summary>
    public static bool Verify(string signatures, Version version, string file, string sha256, IReadOnlyList<ReleaseKey> keys)
    {
        var data = Encoding.UTF8.GetBytes(Statement(version, file, sha256));
        foreach (var line in signatures.Split('\n'))
        {
            var parts = line.Split((char[])[' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || keys.FirstOrDefault(k => k.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) is not { } key)
                continue;
            try
            {
                using var ecdsa = ReleaseKey.Create(key.Point);
                if (ecdsa.VerifyData(data, Convert.FromBase64String(parts[1]), HashAlgorithmName.SHA256))
                    return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // A malformed line proves nothing: the other lines may still hold a valid signature.
            }
        }
        return false;
    }

    /// <summary>The keys in a list as release-keys.txt holds them: a base64 point per line; "#" starts a comment.</summary>
    public static List<ReleaseKey> Parse(string text) =>
        text.Split('\n')
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0)
            .Select(line => ReleaseKey.FromBase64(line) ?? throw new InvalidDataException("Invalid release key: " + line))
            .ToList();

    private static List<ReleaseKey> Load()
    {
        using var stream = typeof(UpdateSignature).Assembly.GetManifestResourceStream("Baton.Core.Updates.release-keys.txt");
        return stream is null ? [] : Parse(new StreamReader(stream).ReadToEnd());
    }
}
