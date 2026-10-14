using System.Text.RegularExpressions;

namespace DownloadManager.Core;

/// <summary>Recognizes downloadable web addresses in text from the clipboard or a drag and drop.</summary>
public static partial class Urls
{
    public static bool TryParseHttp(string? text, out Uri uri)
    {
        uri = null!;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
            return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || parsed.Host.Length == 0)
            return false;
        uri = parsed;
        return true;
    }

    /// <summary>The text itself when it is exactly one http(s) address, otherwise null.</summary>
    public static string? SingleHttpUrl(string? text) => TryParseHttp(text, out _) ? text!.Trim() : null;

    /// <summary>
    /// Every distinct http(s) address in a block of text, in order: one per line from a browser's link drag,
    /// or several scattered in text copied from a page.
    /// </summary>
    public static List<string> ExtractHttpUrls(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;
        foreach (Match match in UrlPattern().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'', '>');
            if (TryParseHttp(candidate, out var uri) && !result.Contains(uri.AbsoluteUri))
                result.Add(uri.AbsoluteUri);
        }
        return result;
    }


    // Stops at spaces, quotes, angle brackets and control characters (dropped links end with a NUL).
    [GeneratedRegex(@"https?://[^\s<>""'\u0000-\u001F]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
