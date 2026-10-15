using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManager.Core.Grabber;

/// <summary>Turns the bytes of a page or style sheet into text, with the encoding a browser would pick.</summary>
public static partial class HtmlText
{
    static HtmlText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-1252, iso-8859-x, shift_jis...

    /// <summary>Byte order mark, then the charset of the Content-Type header, then &lt;meta charset&gt; (or @charset), else UTF-8.</summary>
    public static string Decode(byte[] bytes, int length, string? contentType)
    {
        var span = bytes.AsSpan(0, length);
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return Encoding.UTF8.GetString(span[3..]);
        if (span.StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(span[2..]);
        if (span.StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(span[2..]);

        var encoding = FromLabel(CharsetOf(contentType));
        if (encoding is null)
        {
            // The first 1024 bytes are enough for a <meta charset> (the HTML prescan looks no further).
            var head = Encoding.ASCII.GetString(span[..Math.Min(1024, span.Length)]);
            var match = MetaCharset().Match(head);
            if (match.Success)
                encoding = FromLabel(match.Groups["cs"].Value);
        }
        // A page cannot really be UTF-16 when the label is found in ASCII text: HTML says to use UTF-8 then.
        if (encoding is null || encoding is UnicodeEncoding)
            encoding = Encoding.UTF8;
        return encoding.GetString(span);
    }

    public static string? CharsetOf(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return null;
        var match = HeaderCharset().Match(contentType);
        return match.Success ? match.Groups["cs"].Value : null;
    }

    private static Encoding? FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;
        try
        {
            // Browsers read "iso-8859-1" and "us-ascii" as windows-1252.
            var name = label.Trim().Trim('"', '\'').ToLowerInvariant();
            if (name is "iso-8859-1" or "latin1" or "us-ascii" or "ascii")
                name = "windows-1252";
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("""<meta[^>]+charset\s*=\s*["']?(?<cs>[A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharset();

    [GeneratedRegex("""charset\s*=\s*["']?(?<cs>[A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderCharset();

    /// <summary>A body without a type that still looks like HTML.</summary>
    public static bool LooksLikeHtml(ReadOnlySpan<byte> start)
    {
        var text = Encoding.ASCII.GetString(start[..Math.Min(512, start.Length)]).TrimStart().ToLowerInvariant();
        return text.StartsWith("<!doctype html", StringComparison.Ordinal) || text.StartsWith("<html", StringComparison.Ordinal);
    }

    public static bool IsHtmlType(string? mediaType) => mediaType is "text/html" or "application/xhtml+xml";

    /// <summary>A style sheet as UTF-8 text: its @charset rule, if any, says UTF-8 too.</summary>
    public static string CssAsUtf8(string css) => CssCharset().Replace(css, "@charset \"utf-8\";", 1);

    [GeneratedRegex("""^\s*@charset\s+["'][^"']{0,40}["']\s*;""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssCharset();
}
