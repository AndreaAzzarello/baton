using System.Net.Http.Headers;

namespace Baton.Core;

public static class FileNames
{
    public const int MaxNameLength = 200;

    public static string FromResponse(ContentDispositionHeaderValue? disposition, Uri uri)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = FromUrl(uri);
        return Sanitize(name);
    }

    public static string FromUrl(Uri uri)
    {
        var last = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1]).Trim('/') : "";
        return string.IsNullOrWhiteSpace(last) ? uri.Host : last;
    }

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Characters that change how a name is displayed without being visible: bidirectional overrides and isolates
    /// ("invoice‮fdp.exe" shows as "invoiceexe.pdf"), zero-width characters, marks and the byte order mark.
    /// </summary>
    public static bool IsInvisible(char c) => c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or (>= '​' and <= '‏')
        or '﻿' or '؜' or '⁠';

    /// <summary>
    /// One file or folder name made valid and safe for Windows: reserved and control characters replaced, invisible and
    /// bidirectional characters removed, trailing dots and spaces dropped, device names ("CON", "nul.txt", "COM1.tar.gz")
    /// prefixed. Shared by downloads and the grabber's copies; may return "" (the caller picks a default).
    /// </summary>
    public static string CleanSegment(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (IsInvisible(c))
                continue;
            builder.Append(c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '\u007f' ? '_' : c);
        }
        var cleaned = builder.ToString().TrimEnd('.', ' ').TrimStart(' ');
        if (cleaned is "." or "..")
            return "";
        // Devices whatever follows the first dot, and also with spaces before it ("CON .txt").
        var stemEnd = cleaned.IndexOf('.');
        var device = (stemEnd < 0 ? cleaned : cleaned[..stemEnd]).TrimEnd(' ');
        return DeviceNames.Contains(device) ? "_" + cleaned : cleaned;
    }

    public static string Sanitize(string name)
    {
        // Leading dots go too (no hidden ".name"), and the device check runs again on what is left (".con" -> "con").
        var cleaned = CleanSegment(CleanSegment(Path.GetFileName(name.Replace('/', '\\'))).TrimStart('.', ' '));
        if (cleaned.Length == 0)
            return "download";
        // Windows limits a name to 255 characters; a shorter limit leaves room for " (2)" and ".part".
        if (cleaned.Length > MaxNameLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 16)
                extension = "";
            cleaned = cleaned[..(MaxNameLength - extension.Length)].TrimEnd(' ', '.') + extension;
        }
        return cleaned;
    }

    /// <summary>Returns "name (n).ext" for the first n that does not exist yet.</summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
