using System.Globalization;
using Baton.App.Mvvm;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>
/// "Start with Windows" as the first-run wizard and the settings show it (StartWithWindowsTemplate): never preselected by
/// the app; for the portable copy, a warning that its entry stops working if the folder moves.
/// </summary>
public sealed class StartWithWindowsViewModel(string? portableProgram, bool enabled) : ObservableObject
{
    private bool _enabled = enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
                OnPropertyChanged(nameof(ShowPortableWarning));
        }
    }

    /// <summary>The portable copy's executable; null for an installed copy.</summary>
    public string? PortableProgram { get; } = portableProgram;

    public bool ShowPortableWarning => PortableProgram is not null && Enabled;

    public string PortableWarning => string.Format(CultureInfo.CurrentCulture, Strings.SettingsPortableStartupWarning, PortableProgram);
}
