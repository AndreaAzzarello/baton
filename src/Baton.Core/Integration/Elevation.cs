using System.Security.Principal;

namespace Baton.Core.Integration;

/// <summary>
/// Whether Baton runs with administrator rights. An elevated Baton must not take orders from agents: any program of the
/// same user could then reach, through the pipe or the local API, a process that has more rights than that user's own.
/// </summary>
public static class Elevation
{
    private static readonly Lazy<bool> Current = new(() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));

    public static bool IsElevated => Current.Value;

    /// <summary>The level an agent origin gets: none at all while elevated.</summary>
    public static AutomationLevel Effective(AutomationLevel configured, bool elevated) => elevated ? AutomationLevel.Off : configured;
}
