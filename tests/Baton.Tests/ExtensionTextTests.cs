using System.Globalization;
using System.Text.Json;
using Baton.Core.Resources;

namespace Baton.Tests;

/// <summary>
/// The extension follows the browser's language, the app its own setting: where one names something of the other (a menu
/// entry, a page, the main queue) both say it the same way, in English and in Italian.
/// </summary>
public sealed class ExtensionTextTests
{
    private static string Extension(string language, string key)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Baton.slnx")))
            dir = dir.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "extension", "_locales", language, "messages.json")));
        return json.RootElement.GetProperty(key).GetProperty("message").GetString()!;
    }

    private static string App(string language, string key) =>
        Strings.ResourceManager.GetString(key, language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language))!;

    [Theory]
    [InlineData("en")]
    [InlineData("it")]
    public void SharedNames_ReadTheSameInTheAppAndInTheExtension(string language)
    {
        // "Capture this site with Baton…" (the page's context menu) is how the app tells to create a project from the browser.
        Assert.Contains(Extension(language, "menuGrab"), App(language, "GrabberEmptyHint"));
        // "Check connection": the extension's page, named by the app's first run and after a registration.
        Assert.Equal(Extension(language, "checkLink"), App(language, "FirstRunVerify"));
        Assert.Contains("“" + Extension(language, "checkLink") + "”", App(language, "BrowserRegistered").Replace("«", "“").Replace("»", "”"));
        Assert.Equal(Extension(language, "mainQueue"), App(language, "QueueMainName"));
        // "Download all with Baton…" opens the app's "Download all" window.
        Assert.StartsWith(App(language, "BatchTitle"), Extension(language, "menuAll"));
        Assert.Equal(Extension(language, "mediaButton"), language == "en" ? "Download this video" : "Scarica questo video");
    }
}
