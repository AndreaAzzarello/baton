using System.Globalization;

namespace Baton.Core;

/// <summary>The language of the interface chosen in the settings.</summary>
public enum AppLanguage
{
    /// <summary>Italian when Windows is in Italian, English otherwise.</summary>
    Automatic,
    English,
    Italian,
}

/// <summary>Turns the chosen language into the culture used for texts, dates, numbers and sizes.</summary>
public static class UiLanguage
{
    /// <summary>
    /// The culture of the interface. Dates and numbers follow the chosen language: the Windows regional format when it is in
    /// that language (en-GB, it-CH…), otherwise the language's usual one (en-US, it-IT).
    /// </summary>
    public static CultureInfo Resolve(AppLanguage choice, CultureInfo windowsLanguage, CultureInfo windowsFormat)
    {
        var language = choice switch
        {
            AppLanguage.English => "en",
            AppLanguage.Italian => "it",
            _ => windowsLanguage.TwoLetterISOLanguageName == "it" ? "it" : "en",
        };
        if (windowsFormat.TwoLetterISOLanguageName == language && !windowsFormat.IsNeutralCulture)
            return windowsFormat;
        return CultureInfo.GetCultureInfo(language == "it" ? "it-IT" : "en-US");
    }

    /// <summary>
    /// A name the app gave (a built-in category, the main queue) in the language of the moment: while the user has not
    /// renamed it, which shows as either language's default, it follows the current language.
    /// </summary>
    public static string Follow(string resourceKey, string name)
    {
        var resources = Resources.Strings.ResourceManager;
        return name == resources.GetString(resourceKey, CultureInfo.InvariantCulture)
               || name == resources.GetString(resourceKey, CultureInfo.GetCultureInfo("it"))
            ? resources.GetString(resourceKey, Resources.Strings.Culture) ?? name
            : name;
    }

    /// <summary>Reads a stored value; anything unknown means automatic.</summary>
    public static AppLanguage Parse(string? value) =>
        Enum.TryParse<AppLanguage>(value, out var language) && Enum.IsDefined(language) ? language : AppLanguage.Automatic;
}
