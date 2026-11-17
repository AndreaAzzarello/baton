using System.Globalization;
using System.Text;

namespace Baton.Core.Integration;

/// <summary>
/// Makes text that came from outside (a file name the server chose, a title, an error from a web server) safe to hand to an AI agent
/// or a script: no control, bidirectional, zero-width, tag or variant-selector characters, no line breaks, a bounded length.
/// Such text always travels as a field of structured data, never inside a sentence the agent could read as an instruction;
/// this only removes what hides or reorders characters and cuts what is too long. It lives in the protocol project so the app,
/// the API and the command line apply the same rules.
/// </summary>
public static class AgentText
{
    public const int MaxName = 255;
    public const int MaxUrl = 2048;
    public const int MaxMessage = 500;
    public const string Ellipsis = "…";

    /// <summary>The text without hidden or reordering characters, at most <paramref name="max"/> UTF-16 units (a cut ends with "…").</summary>
    public static string Clean(string? text, int max = MaxName)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var builder = new StringBuilder(Math.Min(text.Length, max + 2));
        var lastWasSpace = true; // leading spaces are dropped
        var overflow = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var keep = Normalize(rune);
            if (keep is null)
                continue;
            if (keep.Value.Value == ' ')
            {
                if (lastWasSpace)
                    continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            builder.Append(keep.Value.ToString());
            if (builder.Length > max)
            {
                overflow = true; // enough read: nothing past the limit is kept, so a 100 KB address costs no more
                break;
            }
        }
        if (overflow)
        {
            // Leave room for the sign and do not leave half of a surrogate pair.
            var length = Math.Max(0, max - Ellipsis.Length);
            if (length > 0 && length < builder.Length && char.IsLowSurrogate(builder[length]))
                length--;
            builder.Length = Math.Min(length, builder.Length);
            while (builder.Length > 0 && builder[^1] == ' ')
                builder.Length--;
            return builder.Append(Ellipsis).ToString();
        }
        while (builder.Length > 0 && builder[^1] == ' ')
            builder.Length--;
        return builder.ToString();
    }

    private static Rune? Normalize(Rune rune)
    {
        var v = rune.Value;
        if (v is '\t' or '\n' or '\r' or '\v' or '\f' or 0x85 or 0x2028 or 0x2029)
            return new Rune(' ');
        // Variation selectors (they change how the previous character looks) and the Mongolian free ones.
        if (v is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or >= 0x180B and <= 0x180F)
            return null;
        // U+E0000-E007F (tag characters, used to smuggle text) is Format, so it falls in the category test below.
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.Format: // bidi controls, zero-width characters, soft hyphen, tag characters
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
                return null;
            case UnicodeCategory.SpaceSeparator:
                return new Rune(' '); // no-break and wide spaces become an ordinary space
            default:
                return v == 0xFFFD ? null : rune;
        }
    }

    /// <summary>An address reduced to what an agent needs: scheme, host and the name of the file ("https://example.com/…/file.zip").</summary>
    public static string Address(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Clean(url, 80);
        var name = uri.Segments.Length > 1 ? Uri.UnescapeDataString(uri.Segments[^1]).Trim('/') : "";
        var middle = uri.Segments.Length > 2 ? "/…" : "";
        var host = uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port;
        return Clean($"{uri.Scheme}://{host}{middle}/{name}", MaxUrl);
    }
}
