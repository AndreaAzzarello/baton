using System.Diagnostics;
using System.IO;
using Baton.Core;
using Baton.Core.Updates;

namespace Baton.App.Services;

/// <summary>
/// The app was called DownloadManager up to 1.1, and an update from 1.1 puts this version in place of DownloadManager.exe.
/// Started under that name, it copies itself to Baton.exe next to it and hands over to the copy. When a 1.1 watcher is
/// waiting (an update), this process stays until Baton.exe reports that it started, because the watcher judges the process
/// it started: if Baton.exe fails, this one closes without the mark and the watcher puts 1.1 back. Baton.exe then tidies
/// the folder (<see cref="LegacyCleanup"/>).
/// </summary>
internal static class LegacyExe
{
    public static bool IsLegacyName(string? path) =>
        path is not null && Path.GetFileName(path).Equals(UpdateChecker.LegacyExeName, StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.ChangeExtension(path, ".dll")); // a development build keeps its name

    /// <summary>The exit code once Baton.exe has taken over, or null when it could not be created (the app then runs as is).</summary>
    public static int? HandOver(string legacyExe, string[] args, Mutex instance)
    {
        var baton = UpdateInstaller.BatonNextTo(legacyExe);
        try
        {
            // A Baton.exe as new as this copy (or newer, updated since) is kept: an old copy never takes its place.
            if (!File.Exists(baton) || UpdateInstaller.VersionOf(baton) < UpdateInstaller.VersionOf(legacyExe))
                File.Copy(legacyExe, baton, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Configure(Path.Combine(AppEnvironment.DataDirectory, "logs"));
            Log.Error("rename", "cannot create Baton.exe, running as DownloadManager.exe", ex);
            return null;
        }
        // Baton.exe takes over the single instance.
        instance.ReleaseMutex();
        instance.Dispose();
        var info = new ProcessStartInfo(baton) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(baton)! };
        foreach (var argument in args)
            info.ArgumentList.Add(argument);
        using var started = Process.Start(info);
        if (started is null || !args.Contains(UpdateInstaller.UpdatedArgument) || UpdateService.LegacyMarkerPath is not { } marker)
            return 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(85); // the 1.1 watcher allows 90 s
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(marker))
                return 0;
            if (started.HasExited)
                return 1;
            Thread.Sleep(200);
        }
        return File.Exists(marker) ? 0 : 1;
    }
}
