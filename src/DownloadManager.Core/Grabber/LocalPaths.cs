using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Core.Grabber;

/// <summary>
/// Where each address of an offline copy is saved: &lt;root&gt;\&lt;host&gt;\&lt;path&gt;, the site's folders kept. Every
/// name is made valid for Windows (reserved characters and device names, trailing dots and spaces), ".." can never
/// leave the root, long names and paths are shortened with a hash, and two addresses never share a file (Windows
/// names ignore case).
/// </summary>
public sealed class LocalPaths
{
    /// <summary>Longest full path written: below MAX_PATH, so Explorer and every program can open the copy.</summary>
    public const int MaxFullPath = 240;

    public const int MaxSegment = 80;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private readonly string _root;
    private readonly Dictionary<string, string> _byUrl = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase); // relative file path -> url
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

    public LocalPaths(string root, IEnumerable<KeyValuePair<string, string>>? previous = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // Files saved by an earlier run keep their names, so a synchronization rewrites the same copy.
        foreach (var (url, relative) in previous ?? [])
        {
            if (IsInside(relative) && !_owners.ContainsKey(relative))
                Register(url, relative);
        }
    }

    public string Root => _root;

    /// <summary>The relative path already given to an address in this copy, if any.</summary>
    public string? Existing(string url) => _byUrl.TryGetValue(url, out var relative) ? relative : null;

    public string FullPath(string relative) => Path.Combine(_root, relative);

    /// <summary>
    /// The relative path (with "\") for an address. <paramref name="contentType"/> adds the extension a local file needs
    /// to open correctly: ".html" for pages (also "x.php"), ".css" for style sheets served without one.
    /// </summary>
    public string Map(Uri url, string? contentType)
    {
        var key = CrawlUrls.Key(url);
        lock (_owners)
        {
            if (_byUrl.TryGetValue(key, out var known))
            {
                // A page keeps its name only if it is one a browser opens as a page: an address saved earlier as a file
                // ("x.hta") that now serves HTML gets a new ".html" name, never HTML in a file Windows would run.
                if (ExtensionFor(contentType, "") != ".html" || IsPageFile(known))
                    return known;
                _byUrl.Remove(key);
                _owners.Remove(known);
            }

            var segments = new List<string> { HostFolder(url) };
            var path = url.AbsolutePath;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
            string file;
            if (path.EndsWith('/') || parts.Count == 0)
            {
                file = "index.html";
            }
            else
            {
                file = parts[^1];
                parts.RemoveAt(parts.Count - 1);
            }
            segments.AddRange(parts.Select(p => Segment(p, isFile: false)));

            var stem = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            if (url.Query.Length > 1)
                stem += "_" + Hash(url.Query, 8); // a different query is a different file
            var wanted = ExtensionFor(contentType, extension);
            if (wanted is not null && !string.Equals(extension, wanted, StringComparison.OrdinalIgnoreCase)
                && !(wanted == ".html" && extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)))
                extension += wanted; // "index.php" served as HTML becomes "index.php.html"
            segments.Add(Segment(stem + extension, isFile: true));

            var relative = Path.Combine([.. segments]);
            if (_root.Length + 1 + relative.Length > MaxFullPath)
            {
                // Too long for Windows: a short name in a folder of the host.
                var ext = Path.GetExtension(segments[^1]);
                relative = Path.Combine(segments[0], "_long", Hash(key, 16) + (ext.Length <= 8 ? ext : ""));
            }
            if (_owners.ContainsKey(relative) || _folders.Contains(relative) || CollidesWithFile(relative))
                relative = Path.Combine(Path.GetDirectoryName(relative)!, $"{Path.GetFileNameWithoutExtension(relative)}~{Hash(key, 6)}{Path.GetExtension(relative)}");
            if (!IsInside(relative))
                throw new InvalidOperationException("Unsafe local path.");
            Register(key, relative);
            return relative;
        }
    }

    private void Register(string url, string relative)
    {
        _byUrl[url] = relative;
        _owners[relative] = url;
        for (var folder = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            _folders.Add(folder);
    }

    /// <summary>A folder of the new path is already a file of the copy.</summary>
    private bool CollidesWithFile(string relative)
    {
        for (var folder = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (_owners.ContainsKey(folder))
                return true;
        }
        return false;
    }

    /// <summary>The path stays inside the root once resolved (no "..", no drive or UNC prefix, no alternate streams).</summary>
    public bool IsInside(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            return false;
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The host as a folder name; a port other than the default is kept ("example.com_8080").</summary>
    public static string HostFolder(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        return Segment(url.IsDefaultPort ? host : $"{host}_{url.Port.ToString(CultureInfo.InvariantCulture)}", isFile: false);
    }

    /// <summary>One file or folder name made valid and safe for Windows.</summary>
    public static string Segment(string name, bool isFile)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '\u007f' ? '_' : c);
        var cleaned = builder.ToString().TrimEnd('.', ' ').TrimStart(' ');
        if (cleaned.Length == 0 || cleaned is "." or "..")
            cleaned = "_";
        // "CON", "con.txt", "COM1.tar.gz"... are devices whatever follows the first dot.
        var stemEnd = cleaned.IndexOf('.');
        var device = stemEnd < 0 ? cleaned : cleaned[..stemEnd];
        if (DeviceNames.Contains(device.TrimEnd(' ')))
            cleaned = "_" + cleaned;
        if (cleaned.Length > MaxSegment)
        {
            var ext = isFile ? Path.GetExtension(cleaned) : "";
            if (ext.Length > 12)
                ext = "";
            cleaned = cleaned[..(MaxSegment - ext.Length - 7)].TrimEnd('.', ' ') + "~" + Hash(cleaned, 6) + ext;
        }
        return cleaned;
    }

    /// <summary>A file a browser opens as a page (".html", ".htm").</summary>
    public static bool IsPageFile(string path) =>
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>The extension a local copy of this type must have, or null when the address's own is fine.</summary>
    public static string? ExtensionFor(string? contentType, string current)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        switch (type)
        {
            case "text/html" or "application/xhtml+xml":
                return ".html";
            case "text/css":
                return ".css";
        }
        if (current.Length > 0)
            return null;
        return type switch
        {
            "application/javascript" or "text/javascript" => ".js",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/avif" => ".avif",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "font/woff2" => ".woff2",
            "font/woff" => ".woff",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "text/plain" => ".txt",
            _ => null,
        };
    }

    /// <summary>
    /// The address of <paramref name="target"/> as seen from the page saved at <paramref name="from"/>: a relative URL
    /// with "/" and each name escaped, so names with spaces, "#" or "%" still open.
    /// </summary>
    public static string Relative(string from, string target)
    {
        var fromParts = (Path.GetDirectoryName(from) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var toParts = target.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length - 1
               && string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
            common++;
        var up = Enumerable.Repeat("..", fromParts.Length - common);
        var down = toParts.Skip(common).Select(EscapeSegment);
        return string.Join('/', up.Concat(down));
    }

    private static string EscapeSegment(string segment) => Uri.EscapeDataString(segment).Replace("%7E", "~");

    public static string Hash(string value, int length) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length];
}
