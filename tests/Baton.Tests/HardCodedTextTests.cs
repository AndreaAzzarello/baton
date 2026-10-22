using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Baton.Tests;

/// <summary>
/// Every text the user reads comes from Strings (English and Italian): no caption written in the XAML or in the app's
/// code, where it would stay in one language.
/// </summary>
public sealed partial class HardCodedTextTests
{
    // Attributes that show text to the user or to a screen reader.
    private static readonly string[] TextAttributes =
        ["Content", "Text", "Header", "ToolTip", "Title", "AutomationProperties.Name", "AutomationProperties.HelpText", "InputGestureText"];

    /// <summary>Words, not glyphs (Segoe Fluent Icons live in the private use area), symbols or numbers.</summary>
    private static bool IsWords(string value) => value.Count(char.IsLetter) >= 2 && !value.StartsWith('{');

    // Product and format names that read the same in every language.
    private static readonly HashSet<string> Allowed = ["Baton", "HTTP", "HTTPS", "SOCKS5", "MD5", "SHA-1", "SHA-256", "SHA-512", "ETag", "English", "Italiano"];

    [Fact]
    public void Xaml_HasNoTextOfItsOwn()
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Baton.App"), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => TextAttributes.Contains(a.Name.LocalName)))
                {
                    if (IsWords(attribute.Value) && !Allowed.Contains(attribute.Value))
                        found.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} {attribute.Name.LocalName}=\"{attribute.Value}\"");
                }
                // Text written as content: <TextBlock>Hello</TextBlock>, <Run>…</Run>
                if (element.Name.LocalName is "TextBlock" or "Run" or "Label" or "Button" or "Hyperlink"
                    && element.Nodes().OfType<XText>().Any(t => IsWords(t.Value.Trim())))
                    found.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} <{element.Name.LocalName}> text");
            }
        }
        Assert.True(found.Count == 0, "text not in Strings:\n" + string.Join("\n", found));
    }

    // A literal given to a property or a dialog that shows it: Header = "…", Show("…"), new Choice(x, "…")…
    [GeneratedRegex(@"(?:\b(?:Header|Content|Text|Title|ToolTip|InputGestureText|szInfoTitle|szInfo|szTip)\s*=\s*|\b(?:MessageBox\.Show|ShowError|Inform|Confirm|Ask|ShowMessage|ShowHint|SetStatus)\(\s*)\$?@?""(?<text>[^""]*)""")]
    private static partial Regex ShownLiteral();

    [Theory]
    [InlineData("item.Header = \"Apri cartella\";", "Apri cartella")]
    [InlineData("_dialogs.ShowError($\"Errore {x}\", title);", "Errore {x}")]
    [InlineData("MessageBox.Show(\"Hello there\");", "Hello there")]
    public void TheCodeScan_FindsLiterals(string line, string text) =>
        Assert.Equal(text, ShownLiteral().Match(line).Groups["text"].Value);

    [Fact]
    public void AppCode_HasNoTextOfItsOwn()
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Baton.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                foreach (Match match in ShownLiteral().Matches(lines[i]))
                {
                    var text = match.Groups["text"].Value;
                    if (IsWords(text) && !Allowed.Contains(text))
                        found.Add($"{Path.GetFileName(file)}:{i + 1} \"{text}\"");
                }
            }
        }
        Assert.True(found.Count == 0, "text not in Strings:\n" + string.Join("\n", found));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }
}
