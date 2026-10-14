namespace DownloadManager.Core;

/// <summary>A group of file types with its own destination folder.</summary>
public sealed class Category
{
    public long Id { get; set; }

    /// <summary>Stable identifier of a built-in category, kept when the user renames it; null for user categories.</summary>
    public string? Key { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Lower-case extensions without the dot.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>
    /// Destination folder. A relative path (the default for built-in categories) is resolved against the
    /// default download folder, so it follows that setting; an empty value means the default folder itself.
    /// </summary>
    public string Folder { get; set; } = "";

    public int SortOrder { get; set; }

    public bool IsGeneral => Key == Categories.GeneralKey;

    public string ResolveFolder(string defaultFolder) =>
        string.IsNullOrWhiteSpace(Folder) ? defaultFolder
        : Path.IsPathFullyQualified(Folder) ? Folder
        : Path.Combine(defaultFolder, Folder);

    public override string ToString() => Name;

    public Category Clone()
    {
        var copy = (Category)MemberwiseClone();
        copy.Extensions = [.. Extensions];
        return copy;
    }
}

/// <summary>Built-in categories and the rules that assign a download to one.</summary>
public static class Categories
{
    public const string CompressedKey = "compressed";
    public const string DocumentsKey = "documents";
    public const string MusicKey = "music";
    public const string VideoKey = "video";
    public const string ProgramsKey = "programs";
    public const string GeneralKey = "general";

    public static List<Category> Defaults() =>
    [
        Builtin(CompressedKey, Resources.Strings.CategoryCompressed, "zip rar 7z gz tgz tar bz2 xz zst cab arj lzh iso img", 0),
        Builtin(DocumentsKey, Resources.Strings.CategoryDocuments, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt csv epub md xps", 1),
        Builtin(MusicKey, Resources.Strings.CategoryMusic, "mp3 flac wav aac m4a ogg oga opus wma aif aiff mid midi", 2),
        Builtin(VideoKey, Resources.Strings.CategoryVideo, "mp4 mkv avi mov wmv webm flv m4v mpg mpeg 3gp ts", 3),
        Builtin(ProgramsKey, Resources.Strings.CategoryPrograms, "exe msi msix msixbundle appx appxbundle apk dmg pkg deb rpm", 4),
        // The fallback: no extensions of its own, files land in the default folder.
        new Category { Key = GeneralKey, Name = Resources.Strings.CategoryGeneral, Folder = "", SortOrder = 5 },
    ];

    private static Category Builtin(string key, string name, string extensions, int order) =>
        new() { Key = key, Name = name, Extensions = ParseExtensions(extensions), Folder = name, SortOrder = order };

    /// <summary>Reads a user-entered list such as ".zip, RAR 7z" into distinct lower-case extensions.</summary>
    public static List<string> ParseExtensions(string? text) =>
        (text ?? "")
            .Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0 && e.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            .Distinct()
            .ToList();

    public static string FormatExtensions(IEnumerable<string> extensions) => string.Join(" ", extensions);

    // Media types whose extension is not visible in the URL, e.g. "download.php?id=42".
    private static readonly Dictionary<string, string> MediaTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = "zip",
        ["application/x-zip-compressed"] = "zip",
        ["application/vnd.rar"] = "rar",
        ["application/x-rar-compressed"] = "rar",
        ["application/x-7z-compressed"] = "7z",
        ["application/gzip"] = "gz",
        ["application/x-gzip"] = "gz",
        ["application/x-tar"] = "tar",
        ["application/x-bzip2"] = "bz2",
        ["application/x-xz"] = "xz",
        ["application/zstd"] = "zst",
        ["application/x-iso9660-image"] = "iso",
        ["application/pdf"] = "pdf",
        ["application/msword"] = "doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.ms-excel"] = "xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/vnd.oasis.opendocument.text"] = "odt",
        ["application/rtf"] = "rtf",
        ["application/epub+zip"] = "epub",
        ["text/plain"] = "txt",
        ["text/csv"] = "csv",
        ["application/x-msdownload"] = "exe",
        ["application/x-msdos-program"] = "exe",
        ["application/vnd.microsoft.portable-executable"] = "exe",
        ["application/x-msi"] = "msi",
        ["application/x-ms-installer"] = "msi",
        ["application/vnd.android.package-archive"] = "apk",
        ["application/x-apple-diskimage"] = "dmg",
        ["audio/mpeg"] = "mp3",
        ["audio/flac"] = "flac",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/aac"] = "aac",
        ["audio/ogg"] = "ogg",
        ["video/mp4"] = "mp4",
        ["video/x-matroska"] = "mkv",
        ["video/webm"] = "webm",
        ["video/quicktime"] = "mov",
        ["video/x-msvideo"] = "avi",
        ["video/mpeg"] = "mpg",
    };

    /// <summary>
    /// The category for a file: by its extension first, then by the server's media type.
    /// Falls back to the general category (or the last one if that was removed).
    /// </summary>
    public static Category Match(IReadOnlyList<Category> categories, string? fileName, string? contentType = null)
    {
        var general = categories.FirstOrDefault(c => c.IsGeneral) ?? categories[^1];

        var extension = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (extension.Length > 0 && ByExtension(categories, extension) is { } byName)
            return byName;

        var mediaType = contentType?.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(mediaType))
            return general;
        if (MediaTypeExtensions.TryGetValue(mediaType, out var mapped) && ByExtension(categories, mapped) is { } byType)
            return byType;

        // A type with no known extension still says whether it is audio or video.
        var family = mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? VideoKey
            : mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? MusicKey
            : null;
        return categories.FirstOrDefault(c => family is not null && c.Key == family) ?? general;
    }

    /// <summary>The category for a URL whose file name is not known yet.</summary>
    public static Category MatchUrl(IReadOnlyList<Category> categories, string url, string? fileName = null) =>
        Match(categories, fileName ?? (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? FileNames.FromUrl(uri) : null));

    private static Category? ByExtension(IReadOnlyList<Category> categories, string extension) =>
        categories.OrderBy(c => c.SortOrder).FirstOrDefault(c => c.Extensions.Contains(extension));
}
