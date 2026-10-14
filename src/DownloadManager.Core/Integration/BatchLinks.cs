namespace DownloadManager.Core.Integration;

/// <summary>A link or image of a page, as "download all" lists it.</summary>
/// <param name="Extension">Lower case without the dot; empty when the address has none.</param>
/// <param name="Preselected">Ticked at first: it looks like a file, not like another web page.</param>
public sealed record BatchLink(string Url, string Name, string Extension, string Text, bool IsImage, bool Preselected);

/// <summary>
/// Turns what the browser found in a page into the list "download all" shows: only addresses the app can download,
/// each once ("#section" links point to the same resource), named after their file. Pages link to many things that
/// are not files, so only addresses that look like files are ticked at first.
/// </summary>
public static class BatchLinks
{
    /// <summary>Extensions of web pages and scripts: listed, but not ticked.</summary>
    public static readonly HashSet<string> PageExtensions =
        ["", "htm", "html", "xhtml", "php", "asp", "aspx", "jsp", "cgi", "pl", "shtml", "cfm"];

    public static List<BatchLink> Build(IEnumerable<BatchItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BatchLink>();
        foreach (var item in items)
        {
            if (item?.Url is null || !Urls.TryParseDownload(item.Url, out var uri))
                continue;
            var address = uri.GetLeftPart(UriPartial.Query);
            if (!seen.Add(address))
                continue;
            var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
            if (extension.Length > 8 || !extension.All(char.IsLetterOrDigit))
                extension = "";
            var text = (item.Text ?? "").Trim();
            result.Add(new BatchLink(address, FileNames.Sanitize(FileNames.FromUrl(uri)), extension, text.Length > 200 ? text[..200] : text,
                item.Kind == "image", !PageExtensions.Contains(extension)));
        }
        return result;
    }

    /// <summary>The type filter of "download all": the extensions present, most frequent first, with their count.</summary>
    public static List<(string Extension, int Count)> Extensions(IEnumerable<BatchLink> links) =>
        links.Where(l => l.Extension.Length > 0)
            .GroupBy(l => l.Extension)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .ToList();

    /// <summary>Whether a link passes the filters: kind (0 all, 1 links, 2 images), extension (null = any) and text.</summary>
    public static bool Matches(BatchLink link, int kind, string? extension, string? text)
    {
        if (kind == 1 && link.IsImage || kind == 2 && !link.IsImage)
            return false;
        if (extension is not null && link.Extension != extension)
            return false;
        var wanted = text?.Trim() ?? "";
        return wanted.Length == 0
               || link.Name.Contains(wanted, StringComparison.CurrentCultureIgnoreCase)
               || link.Text.Contains(wanted, StringComparison.CurrentCultureIgnoreCase)
               || link.Url.Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }
}
