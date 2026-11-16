using System.Globalization;

namespace Baton.Core.Rules;

/// <summary>
/// The rename action: {name} (the name without its extension), {ext} (the extension with its dot), {host}, {date} (yyyy-MM-dd),
/// {time} (HH-mm-ss), {category} and {n} (the first number that makes the name new in its folder). The real extension is
/// always kept: it is added when the pattern has no {ext}, and a result can never end in a program or script extension
/// the file did not have.
/// </summary>
public static class RenameTemplate
{
    public static readonly string[] Placeholders = ["{name}", "{ext}", "{host}", "{date}", "{time}", "{category}", "{n}"];

    private const int MaxNumber = 9999;

    public static string Apply(string pattern, string originalName, string host, string? category, DateTime localNow, Func<string, bool>? isTaken = null)
    {
        var original = FileNames.Sanitize(originalName);
        var extension = Path.GetExtension(original);
        var stem = extension.Length > 0 ? original[..^extension.Length] : original;
        var usesNumber = pattern.Contains("{n}", StringComparison.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var result = Finish(Expand(pattern, stem, extension, host, category, localNow, n), extension);
            if (!usesNumber || isTaken is null || !isTaken(result) || n >= MaxNumber)
                return result;
        }
    }

    private static string Expand(string pattern, string stem, string extension, string host, string? category, DateTime now, int n)
    {
        var text = pattern;
        string[][] values =
        [
            ["{name}", stem], ["{ext}", extension], ["{host}", FileNames.CleanSegment(host)], ["{category}", FileNames.CleanSegment(category ?? "")],
            ["{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], ["{time}", now.ToString("HH-mm-ss", CultureInfo.InvariantCulture)],
            ["{n}", n.ToString(CultureInfo.InvariantCulture)],
        ];
        foreach (var pair in values)
            text = text.Replace(pair[0], pair[1], StringComparison.OrdinalIgnoreCase);
        if (!pattern.Contains("{ext}", StringComparison.OrdinalIgnoreCase))
            text += extension;
        return text;
    }

    private static string Finish(string expanded, string extension)
    {
        var name = FileNames.Sanitize(expanded);
        var current = Path.GetExtension(name);
        if (current.Equals(extension, StringComparison.OrdinalIgnoreCase))
            return name;
        // The file's own extension stays the last one. Without one, a program or script extension a pattern left at the end is dropped.
        if (extension.Length > 0)
            return FileNames.Sanitize(name + extension);
        return ExecutableTypes.IsRunnable(current) ? FileNames.Sanitize(name[..^current.Length]) : name;
    }
}
