using System.Globalization;
using System.Xml.Linq;

namespace Baton.Tests;

/// <summary>The text colors of both themes against every background they are drawn on: at least 4.5:1 (WCAG AA).</summary>
public sealed class ThemeContrastTests
{
    public static readonly TheoryData<string, string, string> Pairs = Build();

    private static TheoryData<string, string, string> Build()
    {
        var data = new TheoryData<string, string, string>();
        string[] backgrounds = ["WindowBrush", "SurfaceBrush", "SurfaceAltBrush", "HoverBrush", "PressedBrush", "AccentSoftBrush", "SelectionBrush"];
        foreach (var theme in new[] { "Light", "Dark" })
        {
            foreach (var background in backgrounds)
            {
                data.Add(theme, "TextBrush", background);
                data.Add(theme, "MutedBrush", background);
            }
            foreach (var background in new[] { "WindowBrush", "SurfaceBrush", "SurfaceAltBrush" })
            {
                data.Add(theme, "AccentBrush", background); // accent glyphs and links
                data.Add(theme, "DangerBrush", background);
                data.Add(theme, "SuccessBrush", background);
            }
            data.Add(theme, "DangerBrush", "DangerSoftBrush");
            data.Add(theme, "AccentTextBrush", "AccentBrush"); // the primary button
            data.Add(theme, "SelectionTextBrush", "SelectionBrush"); // selected rows in the lists
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TextColors_HaveEnoughContrast(string theme, string foreground, string background)
    {
        var colors = Palette(theme);
        var ratio = Contrast(colors[foreground], colors[background]);
        Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {background} is {ratio:0.00}:1");
    }

    private static Dictionary<string, string> Palette(string theme)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "src", "Baton.App", "Themes", theme + ".xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(file).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => (string)e.Attribute("Color")!);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var rgb = hex.TrimStart('#');
        if (rgb.Length == 8)
            rgb = rgb[2..]; // #AARRGGBB: only opaque colors are used for text
        double Channel(int i)
        {
            var c = int.Parse(rgb.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }
}
