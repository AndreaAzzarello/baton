using System.Xml.Linq;

namespace Baton.Tests;

/// <summary>
/// Every interactive control in the app's windows has a name for screen readers: its own text (a button or check box
/// with a plain caption), an AutomationProperties.Name, or a Label that targets it.
/// </summary>
public sealed class XamlAccessibilityTests
{
    private static readonly string[] Interactive =
        ["Button", "TextBox", "PasswordBox", "ComboBox", "CheckBox", "RadioButton", "ListBox", "ListView", "DataGrid", "Slider", "TreeView", "DatePicker", "Expander"];

    public static TheoryData<string> Views()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Baton.App", "Views"), "*.xaml"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void InteractiveControls_HaveAnAccessibleName(string view)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var path = Path.Combine(Root(), "src", "Baton.App", "Views", view);
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
        var codeBehind = File.Exists(path + ".cs") ? File.ReadAllText(path + ".cs") : "";
        var targeted = doc.Descendants().Where(e => e.Name.LocalName == "Label")
            .Select(e => (string?)e.Attribute("Target"))
            .Where(t => t is not null && t.Contains("ElementName=", StringComparison.Ordinal))
            .Select(t => t!.Split("ElementName=")[1].TrimEnd('}').Trim())
            .ToHashSet();
        var missing = new List<string>();
        foreach (var element in doc.Descendants().Where(e => Interactive.Contains(e.Name.LocalName)))
        {
            var named = element.Attribute("AutomationProperties.Name") is not null
                        || (element.Attribute(x + "Name") is { } name && targeted.Contains(name.Value))
                        || (element.Name.LocalName is "Button" or "CheckBox" or "RadioButton" or "Expander"
                            && (element.Attribute("Content") ?? element.Attribute("Header")) is not null)
                        // A caption set by the code behind (e.g. "Shut down now" with the action's name).
                        || (element.Attribute(x + "Name") is { } named2 && codeBehind.Contains(named2.Value + ".Content =", StringComparison.Ordinal));
            if (!named)
            {
                var info = (System.Xml.IXmlLineInfo)element;
                missing.Add($"{element.Name.LocalName} at line {info.LineNumber}");
            }
        }
        Assert.True(missing.Count == 0, $"{view}: no accessible name for {string.Join(", ", missing)}");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }
}
