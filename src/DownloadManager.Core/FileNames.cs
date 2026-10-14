using System.Net.Http.Headers;

namespace DownloadManager.Core;

public static class FileNames
{
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

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(Path.GetFileName(name).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return cleaned.Length == 0 ? "download" : cleaned;
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
