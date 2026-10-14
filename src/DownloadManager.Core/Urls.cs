using System.Text.RegularExpressions;

namespace DownloadManager.Core;

/// <summary>Recognizes downloadable addresses in text from the clipboard, a drag and drop or the browser.</summary>
public static partial class Urls
{
    /// <summary>Schemes the app downloads: HTTP(S), FTP, FTPS with implicit TLS (ftps) and with explicit TLS (ftpes).</summary>
    public static readonly string[] Schemes = ["http", "https", "ftp", "ftps", "ftpes"];

    public static bool TryParseHttp(string? text, out Uri uri) => TryParse(text, ["http", "https"], out uri);

    public static bool TryParseDownload(string? text, out Uri uri) => TryParse(text, Schemes, out uri);

    private static bool TryParse(string? text, string[] schemes, out Uri uri)
    {
        uri = null!;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
            return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || !schemes.Contains(parsed.Scheme) || parsed.Host.Length == 0)
            return false;
        uri = parsed;
        return true;
    }

    /// <summary>The text itself when it is exactly one downloadable address, otherwise null.</summary>
    public static string? SingleHttpUrl(string? text) => TryParseDownload(text, out _) ? text!.Trim() : null;

    /// <summary>
    /// Every distinct downloadable address in a block of text, in order: one per line from a browser's link drag,
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
            if (TryParseDownload(candidate, out var uri) && !result.Contains(uri.AbsoluteUri))
                result.Add(uri.AbsoluteUri);
        }
        return result;
    }

    /// <summary>
    /// Separates a login written in the address (ftp://user:password@host/file) from it: the address shown and stored
    /// carries no password, the login is kept encrypted with the download.
    /// </summary>
    public static (Uri Address, string? Username, string? Password) SplitLogin(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo))
            return (uri, null, null);
        var parts = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(parts[0]);
        var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        return (clean, user.Length > 0 ? user : null, password);
    }

    /// <summary>The address without any login, for display and logs.</summary>
    public static string Display(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0 ? SplitLogin(uri).Address.AbsoluteUri : url;

    // Stops at spaces, quotes, angle brackets and control characters (dropped links end with a NUL).
    [GeneratedRegex(@"(?:https?|ftps?|ftpes)://[^\s<>""'\u0000-\u001F]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
