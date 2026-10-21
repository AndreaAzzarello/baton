using System.Diagnostics;
using System.IO;
using Baton.Core;
using Baton.Core.Platform;
using Baton.Core.Updates;

namespace Baton.App.Services;

/// <summary>
/// Tidies, in the background after the start, what the app left under its name up to 1.1 (DownloadManager): the program
/// file and its backup next to Baton.exe (see <see cref="UpdateInstaller.FinishRename"/>) and the shortcuts that started it.
/// Also DownloadManager's registry entries (<see cref="SystemRegistration.RemoveLegacy"/>). Started once the app is up.
/// </summary>
internal static class LegacyCleanup
{
    public static void Start() => Task.Run(() =>
    {
        try
        {
            AppEnvironment.Registration.RemoveLegacy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("rename", "cannot remove DownloadManager's registrations", ex);
        }
        if (UpdateService.InstalledExe is not { } exe)
            return; // a development build
        var legacy = Path.Combine(Path.GetDirectoryName(exe)!, UpdateChecker.LegacyExeName);
        var shortcutsDone = Path.Combine(AppEnvironment.DataDirectory, "shortcuts-renamed");
        try
        {
            var renamed = File.Exists(legacy) || File.Exists(UpdateInstaller.BackupOf(legacy));
            if (renamed)
            {
                // The copy this version started from, and a 1.1 watcher, close once Baton.exe has reported that it started.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (RunningFrom(legacy) && DateTime.UtcNow < deadline)
                    Thread.Sleep(500);
                if (RunningFrom(legacy))
                    return; // DownloadManager.exe still in use (for example 1.1 started by hand): next time
                UpdateInstaller.FinishRename(exe, UpdateInstaller.VersionOf);
            }
            // Once, not at every start: reading the shortcuts loads parts of the shell that would stay in memory in the tray.
            if (!AppEnvironment.IsSandboxed && (renamed || !File.Exists(shortcutsDone)))
            {
                Log.Info("rename", $"{LegacyShortcuts.Retarget(LegacyShortcuts.UserFolders, legacy, exe)} shortcut(s) now start Baton.exe");
                File.WriteAllText(shortcutsDone, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            // Background and best effort: whatever fails is logged (an unobserved task would say nothing).
            Log.Error("rename", "cannot tidy DownloadManager.exe or its shortcuts", ex);
        }
    });

    private static bool RunningFrom(string legacy) =>
        Process.GetProcesses().Any(p =>
        {
            using (p)
            {
                try
                {
                    return p.ProcessName.StartsWith("DownloadManager", StringComparison.OrdinalIgnoreCase)
                        && p.MainModule?.FileName is { } path && path.StartsWith(legacy, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }
        });
}
