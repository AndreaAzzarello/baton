using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.Services;

/// <summary>
/// Applies the interface language: texts (<see cref="Strings"/>), dates, numbers and sizes. A change made in the settings
/// applies at once: XAML texts are bound through <see cref="LocExtension"/>, and view models refresh their bindings.
/// </summary>
public static class Localizer
{
    /// <summary>The language and regional format of Windows, read before the app changes the current culture.</summary>
    public static CultureInfo WindowsLanguage { get; } = CultureInfo.CurrentUICulture;

    public static CultureInfo WindowsFormat { get; } = CultureInfo.CurrentCulture;

    /// <summary>BATON_PSEUDO=1 shows every text stretched by 40% (the qps-ploc texts from make_resx.py --pseudo), to find truncated labels.</summary>
    private static readonly bool Pseudo = Environment.GetEnvironmentVariable("BATON_PSEUDO") == "1";

    private static bool _windowsHooked;

    public static CultureInfo Current { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>
    /// Sets the culture of every thread, for texts and formats; returns false when nothing changed. Enough for the browser
    /// host, which shows no window (and so does not load WPF for it).
    /// </summary>
    public static bool SetCulture(AppLanguage choice)
    {
        var formats = UiLanguage.Resolve(choice, WindowsLanguage, WindowsFormat);
        var texts = Pseudo ? CultureInfo.GetCultureInfo("qps-ploc") : formats;
        if (Strings.Culture is { } previous && previous.Name == texts.Name && Current.Name == formats.Name)
            return false;
        CultureInfo.DefaultThreadCurrentCulture = formats;
        CultureInfo.DefaultThreadCurrentUICulture = texts;
        CultureInfo.CurrentCulture = formats;
        CultureInfo.CurrentUICulture = texts;
        Strings.Culture = texts;
        Current = formats;
        return true;
    }

    /// <summary>
    /// <see cref="SetCulture"/>, then the windows: at the first call the default of every new window, later the texts and
    /// formats of the open ones. Returns false when nothing changed.
    /// </summary>
    public static bool Apply(AppLanguage choice)
    {
        var changed = SetCulture(choice);
        if (!_windowsHooked)
        {
            _windowsHooked = true;
            HookWindows();
        }
        else if (changed)
        {
            RefreshWindows();
        }
        return changed;
    }

    private static void HookWindows()
    {
        // Converters and StringFormat in bindings use the element's Language (en-US by default), not CurrentCulture.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Current.IetfLanguageTag)));
        // Windows opened after a change in this session: the default above is the language at startup.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window && window.Language.IetfLanguageTag != Current.IetfLanguageTag)
                window.Language = XmlLanguage.GetLanguage(Current.IetfLanguageTag);
        }));
    }

    private static void RefreshWindows()
    {
        // The app's windows belong to its thread (where the settings are saved); never touched from another one.
        if (Application.Current is { } app && app.Dispatcher.CheckAccess())
        {
            foreach (Window window in app.Windows)
                window.Language = XmlLanguage.GetLanguage(Current.IetfLanguageTag);
        }
        LocalizedStrings.Instance.Refresh();
        Mvvm.ObservableObject.RefreshAll();
    }
}

public static class LocalizedText
{
    /// <summary>
    /// Binds a text of an element built in code (a menu entry, by default its header) to <see cref="Strings"/>, so it follows
    /// a language change like {l:Loc} in XAML. Pass the key with nameof.
    /// </summary>
    public static T Text<T>(this T target, string key, DependencyProperty? property = null) where T : HeaderedItemsControl
    {
        BindingOperations.SetBinding(target, property ?? HeaderedItemsControl.HeaderProperty,
            new Binding($"[{key}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay });
        return target;
    }
}

/// <summary>The texts as a bindable source: a language change refreshes every <see cref="LocExtension"/> binding.</summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    public static LocalizedStrings Instance { get; } = new();

    public string this[string key] => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary>{l:Loc …}: a text of <see cref="Strings"/> that follows the interface language.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
