using System.Globalization;
using System.Security.Cryptography;

namespace DownloadManager.Core.PostDownload;

/// <summary>What the antivirus said about a finished file.</summary>
public enum ScanOutcome
{
    /// <summary>No threat found.</summary>
    Clean,

    /// <summary>The scanner reported a threat (its own action, e.g. quarantine, is up to it).</summary>
    Threat,

    /// <summary>The scanner could not be run or ended with an unexpected code.</summary>
    Failed,

    /// <summary>The scanner did not finish within the time allowed and was stopped.</summary>
    TimedOut,
}

/// <summary>
/// The checks run on a finished download: its hash (expected one given when it was added, or computed afterwards)
/// and the antivirus scan. Stored as JSON with the download.
/// </summary>
public sealed class PostCheck
{
    /// <summary>"MD5", "SHA1" or "SHA256".</summary>
    public string? HashAlgorithm { get; set; }

    /// <summary>The hash the user expects, lower-case hexadecimal.</summary>
    public string? ExpectedHash { get; set; }

    /// <summary>The hash of the file on disk, lower-case hexadecimal.</summary>
    public string? ComputedHash { get; set; }

    public ScanOutcome? Scan { get; set; }

    /// <summary>What the scanner said (its last line), or why it could not run.</summary>
    public string? ScanDetail { get; set; }

    /// <summary>Checks still to run (the app was closed before they finished, or they are running now).</summary>
    public bool Pending { get; set; }

    /// <summary>True or false once both an expected and a computed hash of the same algorithm exist.</summary>
    public bool? HashMatches => ExpectedHash is null || ComputedHash is null ? null : string.Equals(ExpectedHash, ComputedHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>Something the user must look at: a hash that differs, a threat, or a scan that could not give a verdict.</summary>
    public bool HasProblem => HashMatches == false || Scan is ScanOutcome.Threat or ScanOutcome.Failed or ScanOutcome.TimedOut;

    public PostCheck Clone() => (PostCheck)MemberwiseClone();

    /// <summary>The outcome in words, one line per check that ran, for tooltips and notifications; null when nothing ran.</summary>
    public static string? Describe(PostCheck? check)
    {
        if (check is null)
            return null;
        if (check.Pending)
            return Resources.Strings.CheckRunning;
        var lines = new List<string>();
        if (check.HashMatches is { } matches)
            lines.Add(matches ? Resources.Strings.CheckHashMatchesShort : Resources.Strings.CheckHashDiffersShort);
        else if (check.ComputedHash is { } hash)
            lines.Add($"{FileHash.DisplayName(check.HashAlgorithm)}: {hash}");
        switch (check.Scan)
        {
            case ScanOutcome.Clean:
                lines.Add(Resources.Strings.ScanClean);
                break;
            case ScanOutcome.Threat:
                lines.Add(Resources.Strings.ScanThreatShort);
                break;
            case ScanOutcome.TimedOut:
                lines.Add(Resources.Strings.ScanTimedOut);
                break;
            case ScanOutcome.Failed:
                lines.Add(string.Format(CultureInfo.CurrentCulture, Resources.Strings.ScanFailed, check.ScanDetail ?? ""));
                break;
        }
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Hashes: parsing what the user pastes, and computing them without loading the file in memory.</summary>
public static class FileHash
{
    public static readonly string[] Algorithms = ["MD5", "SHA1", "SHA256"];

    /// <summary>
    /// "sha256:ab12…", "SHA-1 ab12…", or bare hexadecimal whose length tells the algorithm (32 MD5, 40 SHA-1, 64 SHA-256).
    /// Spaces and colons between bytes are ignored.
    /// </summary>
    public static bool TryParse(string? text, out string algorithm, out string hex)
    {
        algorithm = hex = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var value = text.Trim();
        string? named = null;
        foreach (var (prefix, name) in new[] { ("sha-256", "SHA256"), ("sha256", "SHA256"), ("sha-1", "SHA1"), ("sha1", "SHA1"), ("md5", "MD5") })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                named = name;
                value = value[prefix.Length..].TrimStart(':', '=', ' ', '\t');
                break;
            }
        }
        var digits = new string(value.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray()).ToLowerInvariant();
        if (digits.Length == 0 || !digits.All(Uri.IsHexDigit))
            return false;
        var byLength = digits.Length switch { 32 => "MD5", 40 => "SHA1", 64 => "SHA256", _ => null };
        if (byLength is null || (named is not null && named != byLength))
            return false;
        algorithm = byLength;
        hex = digits;
        return true;
    }

    public static async Task<string> ComputeAsync(string path, string algorithm, CancellationToken ct)
    {
        using HashAlgorithm hash = algorithm switch
        {
            "MD5" => MD5.Create(),
            "SHA1" => SHA1.Create(),
            "SHA256" => SHA256.Create(),
            _ => throw new ArgumentException("Unknown hash algorithm.", nameof(algorithm)),
        };
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await hash.ComputeHashAsync(file, ct);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>"SHA-256" for display.</summary>
    public static string DisplayName(string? algorithm) => algorithm switch
    {
        "SHA256" => "SHA-256",
        "SHA1" => "SHA-1",
        null => "",
        _ => algorithm.ToUpper(CultureInfo.InvariantCulture),
    };
}
