using System.IO;
using System.Text;
using Baton.Core;
using Baton.Core.Setup;

namespace Baton.App.Services;

/// <summary>
/// What the installer asks of Baton.exe, so that its script keeps no logic of its own: the copies and data it finds before
/// installing, the import of a portable copy's data, and the clean-up of the registry when uninstalling.
/// </summary>
internal static class SetupCommands
{
    public const string Check = "--setup-check";
    public const string Import = "--import-data";
    public const string Uninstall = "--uninstall";

    public static int? Run(string[] args) => args switch
    {
        [Check, var report, var installedExe] => WriteReport(report, installedExe),
        [Import, var folder] => ImportData(folder),
        [Uninstall] => Unregister(),
        _ => null,
    };

    /// <summary>The report of <see cref="SetupCheck"/> in a file the installer reads (it cannot read a program's output).</summary>
    private static int WriteReport(string report, string installedExe)
    {
        // The single instance of the installed app (and of 1.2); in a sandboxed test run, of that run only.
        var running = Mutex.TryOpenExisting(@"Local\" + AppEnvironment.PipeName, out var mutex);
        mutex?.Dispose();
        var found = SetupCheck.Find(AppEnvironment.Registry, AppEnvironment.InstalledDataDirectory, AppEnvironment.LegacyDataDirectory,
            installedExe, running);
        File.WriteAllText(report, found.Format(), new UTF8Encoding(true)); // with a BOM: Inno Setup then reads it as UTF-8
        return 0;
    }

    /// <summary>
    /// Copies a portable copy's data folder into the installed app's, with the copy that moved DownloadManager's data to
    /// Baton (consistent database, checked, the source never changed). Only when the installed app has no data yet.
    /// </summary>
    private static int ImportData(string folder)
    {
        var (outcome, error) = LegacyData.Migrate(Path.GetFullPath(folder), AppEnvironment.InstalledDataDirectory, leaveNote: false);
        if (outcome == MigrationOutcome.Failed)
            Log.Error("setup", $"portable data not imported from {folder}: {error}");
        return outcome == MigrationOutcome.Failed ? 1 : 0;
    }

    /// <summary>Removes the browser host, link scheme, notification identity and start entry that point to this program.</summary>
    private static int Unregister()
    {
        try
        {
            AppEnvironment.Registration.Unregister();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 1;
        }
    }
}
