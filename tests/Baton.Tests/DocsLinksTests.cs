using System.Text.RegularExpressions;

namespace Baton.Tests;

/// <summary>
/// The links of the public documents point somewhere: files of the repository exist, and anchors (in the same document or
/// in another one) match a heading as GitHub turns it into an anchor. Links to the web are checked by hand at release time.
/// </summary>
public sealed partial class DocsLinksTests
{
    private static readonly string[] Documents = ["README.md", "README.it.md", "ROADMAP.md", "PRIVACY.md", "SECURITY.md", "CONTRIBUTING.md"];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Baton.slnx")))
            dir = dir.Parent;
        return dir.FullName;
    }

    /// <summary>GitHub's anchors: lower case, punctuation dropped, spaces as hyphens, "-1", "-2"… for repeated headings.</summary>
    public static HashSet<string> Anchors(string markdown)
    {
        var anchors = new HashSet<string>();
        var inCode = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```"))
                inCode = !inCode;
            if (inCode || HeadingLine().Match(line) is not { Success: true } heading)
                continue;
            var text = LinkText().Replace(heading.Groups[1].Value.Trim(), "$1").Replace("`", "").Replace("*", "");
            var slug = string.Concat(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Replace(' ', '-');
            var unique = slug;
            for (var n = 1; !anchors.Add(unique); n++)
                unique = $"{slug}-{n}";
        }
        return anchors;
    }

    [Fact]
    public void AnchorsAreMadeAsOnGitHub() =>
        Assert.Equal(["limiti-noti-known-issues", "firefox-permanente-firma-non-elencata-su-addonsmozillaorg", "verify-a-release", "italiano", "italiano-1"],
            Anchors("## Limiti noti (known issues)\n### Firefox permanente: firma non elencata su addons.mozilla.org\n## Verify a release\n## Italiano\n## Italiano\n"));

    [Fact]
    public void EveryLinkToTheRepositoryOrToAHeading_Resolves()
    {
        var root = Root();
        var problems = new List<string>();
        var anchored = 0;
        foreach (var document in Documents)
        {
            var text = File.ReadAllText(Path.Combine(root, document));
            foreach (Match link in MarkdownLink().Matches(text).Concat(HtmlLink().Matches(text)))
            {
                var target = link.Groups["url"].Value;
                if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith("mailto:"))
                    continue;
                var (file, anchor) = target.Split('#', 2) is var parts && parts.Length == 2 ? (parts[0], parts[1]) : (target, null);
                var path = file.Length == 0 ? Path.Combine(root, document) : Path.Combine(root, Uri.UnescapeDataString(file).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) && !Directory.Exists(path))
                    problems.Add($"{document}: {target} (missing)");
                else if (anchor is not null && path.EndsWith(".md") && !Anchors(File.ReadAllText(path)).Contains(anchor))
                    problems.Add($"{document}: {target} (no such heading)");
                else if (anchor is not null)
                    anchored++;
            }
        }
        Assert.Empty(problems);
        Assert.True(anchored >= 10, $"only {anchored} links to headings found"); // the patterns do find the links
    }

    [Fact]
    public void PrivacyAndRoadmap_AreLinkedFromTheDocumentsThatIntroduceThem()
    {
        var root = Root();
        foreach (var document in new[] { "README.md", "README.it.md", "SECURITY.md" })
            Assert.Contains("(PRIVACY.md)", File.ReadAllText(Path.Combine(root, document)));
        foreach (var document in new[] { "README.md", "README.it.md" })
            Assert.Contains("(ROADMAP.md)", File.ReadAllText(Path.Combine(root, document)));
        var roadmap = File.ReadAllText(Path.Combine(root, "ROADMAP.md"));
        Assert.Contains("https://github.com/AndreaAzzarello/baton/discussions/categories/ideas", roadmap);
        Assert.EndsWith("This roadmap is a direction, not a promise. Ideas and votes are welcome in\n" +
            "[GitHub Discussions](https://github.com/AndreaAzzarello/baton/discussions/categories/ideas).\n", roadmap.Replace("\r\n", "\n"));
        foreach (var status in new[] { "## In progress", "## Planned", "## Exploring" })
            Assert.Contains(status, roadmap);
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkText();

    // [text](url) and ![alt](url), not inside inline code.
    [GeneratedRegex(@"(?<!`)!?\[[^\]]*\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?:href|src|srcset)=""(?<url>[^""]+)""")]
    private static partial Regex HtmlLink();
}
