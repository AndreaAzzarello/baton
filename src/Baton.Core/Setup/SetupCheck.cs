using Baton.Core.Platform;

namespace Baton.Core.Setup;

/// <summary>What the installer finds before installing: data to keep, other copies of the app, a copy still running.</summary>
/// <param name="Running">Baton (any copy, also 1.2) is open: it must be closed first.</param>
/// <param name="HasData">%LOCALAPPDATA%\Baton already holds downloads: the installed app uses them as they are.</param>
/// <param name="LegacyData">DownloadManager's data (up to 1.1), copied at the first start of the installed app.</param>
/// <param name="Copies">Other copies the browsers, the link scheme or start-with-Windows point to; the installed app takes their place.</param>
/// <param name="PortableData">The data folder of a portable copy, which can be copied into the installed app (when it has none).</param>
public sealed record SetupReport(bool Running, bool HasData, string? LegacyData, IReadOnlyList<string> Copies, string? PortableData)
{
    /// <summary>One "name=value" per line, read by the installer's script.</summary>
    public string Format()
    {
        var lines = new List<string> { $"running={(Running ? 1 : 0)}", $"data={(HasData ? 1 : 0)}" };
        if (LegacyData is not null)
            lines.Add("legacy=" + LegacyData);
        lines.AddRange(Copies.Select(c => "copy=" + c));
        if (PortableData is not null)
            lines.Add("portable=" + PortableData);
        return string.Join("\r\n", lines) + "\r\n";
    }
}

public static class SetupCheck
{
    /// <summary>Next to Baton.exe: this copy is portable, its data lives in the <see cref="PortableDataFolder"/> folder beside it.</summary>
    public const string PortableFlag = "portable.flag";

    public const string PortableDataFolder = "Data";

    /// <param name="installedData">The installed app's data folder (%LOCALAPPDATA%\Baton).</param>
    /// <param name="legacy">DownloadManager's data folder (%LOCALAPPDATA%\DownloadManager), if there can be one.</param>
    /// <param name="installedExe">Where the installer puts Baton.exe: not "another copy" even when registered already.</param>
    public static SetupReport Find(IUserRegistry registry, string installedData, string? legacy, string installedExe, bool running)
    {
        var hasData = File.Exists(Path.Combine(installedData, LegacyData.DatabaseName));
        var installed = FullPath(installedExe);
        // Paths come from the registry and from manifest files: a malformed one is simply not a copy.
        var copies = new SystemRegistration(registry, installedExe).RegisteredPrograms()
            .Select(FullPath)
            .OfType<string>()
            .Where(p => !string.Equals(p, installed, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .ToList();
        var portable = hasData ? null : copies
            .Select(p => Path.GetDirectoryName(p)!)
            .Where(dir => File.Exists(Path.Combine(dir, PortableFlag)))
            .Select(dir => Path.Combine(dir, PortableDataFolder))
            .FirstOrDefault(data => File.Exists(Path.Combine(data, LegacyData.DatabaseName)));
        // DownloadManager's data is copied at the first start only into an empty folder: not after a portable import.
        var legacyData = !hasData && portable is null && legacy is not null && File.Exists(Path.Combine(legacy, LegacyData.DatabaseName))
            ? legacy
            : null;
        return new SetupReport(running, hasData, legacyData, copies, portable);
    }

    private static string? FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
