using System.Collections;
using System.Windows;
using System.Windows.Media;
using Baton.Core;
using Microsoft.Win32;

namespace Baton.App.Services;

/// <summary>
/// Light, dark or "follow Windows". The app's palette and the Fluent theme of the standard controls are switched
/// together; in system mode they follow the Windows setting while the app runs.
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri LightPalette = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkPalette = new("pack://application:,,,/Themes/Dark.xaml");

    // The app's blue in the same steps as the Windows accent palette, so Fluent controls match the rest of the UI.
    private static readonly (Func<Color> System, Color App)[] AccentSteps =
    [
        (() => SystemColors.AccentColor, Color.FromRgb(0x2F, 0x6F, 0xEB)),
        (() => SystemColors.AccentColorLight1, Color.FromRgb(0x5A, 0x8F, 0xF0)),
        (() => SystemColors.AccentColorLight2, Color.FromRgb(0x82, 0xAA, 0xFF)),
        (() => SystemColors.AccentColorLight3, Color.FromRgb(0xA9, 0xC4, 0xFF)),
        (() => SystemColors.AccentColorDark1, Color.FromRgb(0x24, 0x57, 0xD1)),
        (() => SystemColors.AccentColorDark2, Color.FromRgb(0x1C, 0x47, 0xAE)),
        (() => SystemColors.AccentColorDark3, Color.FromRgb(0x14, 0x35, 0x8A)),
    ];

    private readonly Application _app;
    private readonly List<object> _accentOverrides = [];
    private AppTheme _theme;
    private bool? _appliedDark;
    private ResourceDictionary? _palette;

    public ThemeService(Application app)
    {
        _app = app;
        // App.xaml starts with the light palette.
        _palette = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString == "Themes/Light.xaml");
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                _app.Dispatcher.InvokeAsync(() => Apply(_theme, force: true));
        };
    }

    public void Apply(AppTheme theme, bool force = false)
    {
        _theme = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemUsesDarkTheme(),
        };
        if (dark == _appliedDark && !force)
            return;
        _appliedDark = dark;

        _app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        // The palette is swapped by reference: the Fluent theme adds dictionaries with similar names.
        var dictionaries = _app.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };
        var index = _palette is null ? -1 : dictionaries.IndexOf(_palette);
        if (index < 0)
            dictionaries.Add(replacement);
        else
            dictionaries[index] = replacement;
        _palette = replacement;
        ReplaceAccent();
    }

    /// <summary>
    /// Fluent brushes are built from the Windows accent colour. Every brush that uses one of its steps is
    /// overridden with the same step of the app's blue; entries in Application.Resources take precedence
    /// over the theme dictionaries.
    /// </summary>
    private void ReplaceAccent()
    {
        foreach (var key in _accentOverrides)
            _app.Resources.Remove(key);
        _accentOverrides.Clear();

        var map = new Dictionary<Color, Color>();
        foreach (var (system, color) in AccentSteps)
        {
            var current = system();
            map.TryAdd(Color.FromRgb(current.R, current.G, current.B), color);
        }
        if (map.Count == 0)
            return;

        var overrides = new Dictionary<object, object>();
        foreach (var dictionary in _app.Resources.MergedDictionaries.Where(d => d != _palette))
            Collect(dictionary, map, overrides);
        foreach (var (key, value) in overrides)
        {
            _app.Resources[key] = value;
            _accentOverrides.Add(key);
        }
    }

    private static void Collect(ResourceDictionary dictionary, Dictionary<Color, Color> map, Dictionary<object, object> overrides)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            var replaced = entry.Value switch
            {
                SolidColorBrush brush when Map(brush.Color, map) is { } color => new SolidColorBrush(color) { Opacity = brush.Opacity },
                GradientBrush gradient when gradient.GradientStops.Any(s => Map(s.Color, map) is not null) => Recolor(gradient, map),
                _ => null,
            };
            if (replaced is not null)
            {
                replaced.Freeze();
                overrides.TryAdd(entry.Key, replaced);
            }
        }
        foreach (var merged in dictionary.MergedDictionaries)
            Collect(merged, map, overrides);
    }

    private static Brush Recolor(GradientBrush gradient, Dictionary<Color, Color> map)
    {
        var copy = gradient.CloneCurrentValue();
        foreach (var stop in copy.GradientStops)
            stop.Color = Map(stop.Color, map) ?? stop.Color;
        return copy;
    }

    /// <summary>The app's colour for an accent step, keeping the original transparency.</summary>
    private static Color? Map(Color color, Dictionary<Color, Color> map) =>
        map.TryGetValue(Color.FromRgb(color.R, color.G, color.B), out var mapped) ? Color.FromArgb(color.A, mapped.R, mapped.G, mapped.B) : null;

    /// <summary>The "app mode" chosen in Windows Settings › Personalization › Colors.</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
