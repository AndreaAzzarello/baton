using System.Security.Cryptography;
using System.Text;
using Baton.Core.Duplicates;

namespace Baton.Core.Integration;

/// <summary>
/// Decides which copied text is worth offering as a download ("Download this?"). Only a lone http, https or ftp link whose file
/// extension belongs to a category; a link offered once is not offered again for a while. Nothing here is written to disk or to
/// the log: the text is judged and forgotten (the links seen are kept in memory as hashes, only to avoid repeating a notification).
/// </summary>
public sealed class ClipboardLinks(TimeSpan? memory = null)
{
    /// <summary>Longer text is never a link worth offering (and is not even parsed).</summary>
    public const int MaxLength = 2048;

    private const int MaxSeen = 64;

    private readonly TimeSpan _memory = memory ?? TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTime> _seen = [];

    /// <summary>True when <paramref name="host"/> (or a site it belongs to) is in the space-separated list of ignored sites.</summary>
    public static bool IsIgnored(string host, string ignoredHosts) =>
        ignoredHosts.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(h =>
            host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>The list with <paramref name="host"/> added (lower case, once).</summary>
    public static string WithIgnored(string ignoredHosts, string host) =>
        IsIgnored(host, ignoredHosts) ? ignoredHosts : string.Join(' ', ignoredHosts.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(host.ToLowerInvariant()));

    /// <summary>The link to offer, or null for anything else (a page address, text, a link seen a moment ago).</summary>
    public Uri? Accept(string? text, IEnumerable<Category> categories, DateTime nowUtc)
    {
        if (text is null || text.Length > MaxLength || !Urls.TryParseDownload(text, out var uri))
            return null;
        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.');
        if (extension.Length == 0 || !categories.Any(c => c.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
            return null;

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DuplicateDetector.NormalizeUrl(uri.AbsoluteUri) ?? uri.AbsoluteUri)));
        foreach (var old in _seen.Where(s => nowUtc - s.Value >= _memory).Select(s => s.Key).ToList())
            _seen.Remove(old);
        if (_seen.ContainsKey(key))
            return null;
        if (_seen.Count >= MaxSeen)
            _seen.Remove(_seen.MinBy(s => s.Value).Key);
        _seen[key] = nowUtc;
        return uri;
    }
}
