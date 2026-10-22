using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Baton.App.ViewModels;
using Baton.Core;
using Baton.Core.Resources;
using Baton.Core.Setup;
using Baton.Core.Updates;

namespace Baton.App.Services;

/// <summary>
/// The app's side of updates: checks the configured address, shows the notes, and after the user confirms downloads,
/// verifies and installs the new version (see <see cref="UpdateInstaller"/>) together with the browser extension next to
/// it (see <see cref="ExtensionPackage"/>); also "restore previous version". Only the published single executable updates
/// itself: a development build has its program in separate files.
/// </summary>
public sealed class UpdateService(DownloadService service, IDialogService dialogs, Action exit)
{
    public const string RolledBackArgument = "--update-failed";

    private static string Folder => Path.Combine(App.DataDirectory, "updates");

    /// <summary>The new version writes it once started; the watcher waits for it.</summary>
    public static string MarkerPath => Path.Combine(Folder, "started");

    /// <summary>Where a 1.1 watcher (the app's previous name) waits for the mark, when its data folder exists.</summary>
    public static string? LegacyMarkerPath =>
        AppEnvironment.LegacyDataDirectory is { } legacy && Directory.Exists(legacy) ? Path.Combine(legacy, "updates", "started") : null;

    /// <summary>The running executable when it can be replaced: the published single file, else null.</summary>
    public static string? InstalledExe =>
        Environment.ProcessPath is { } path && Path.GetFileName(path).Equals(UpdateChecker.ExeName, StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.ChangeExtension(path, ".dll"))
            ? path
            : null;

    /// <summary>The database schema of the version an update replaced, written before the swap.</summary>
    private static string PreviousSchemaPath => Path.Combine(Folder, "previous-schema");

    private static string DatabasePath => Path.Combine(App.DataDirectory, "downloads.db");

    public static bool CanRestore => InstalledExe is { } exe && File.Exists(UpdateInstaller.BackupOf(exe));

    /// <summary>Looks for a newer version; <paramref name="interactive"/> also reports "none" and errors.</summary>
    public async Task CheckAsync(string address, bool interactive)
    {
        if (address.Trim().Length == 0)
        {
            if (interactive)
                dialogs.Inform(Strings.ErrorUpdateUrlMissing, Strings.UpdateTitle);
            return;
        }
        UpdateInfo? update;
        try
        {
            using var http = service.Network.CreateHttpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            update = await UpdateChecker.CheckAsync(http, address.Trim(), Version.Parse(App.AppVersion), timeout.Token);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or OperationCanceledException or IOException)
        {
            Log.Warning("update", $"check failed: {ex.Message}");
            if (ex is UpdateException { Unverifiable: true })
            {
                // A newer version that cannot be verified: the check worked, the release is what is wrong. The automatic
                // check says it once per release, not at every start.
                if (interactive || FirstReport(ex.Message))
                    dialogs.ShowError(ex.Message, Strings.UpdateTitle);
            }
            else if (interactive)
            {
                dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.UpdateCheckFailed, ex.Message), Strings.UpdateTitle);
            }
            return;
        }
        if (update is null)
        {
            await RepairExtensionAsync(address, interactive);
            if (interactive)
                dialogs.Inform(string.Format(CultureInfo.CurrentCulture, Strings.UpdateNone, App.AppVersion), Strings.UpdateTitle);
            return;
        }
        Log.Info("update", $"version {update.Version} available");
        dialogs.ShowUpdate(new UpdateViewModel(update, App.AppVersion, (progress, ct) => InstallAsync(update, progress, ct)));
    }

    /// <summary>True the first time this message is reported (remembered in the update folder).</summary>
    private static bool FirstReport(string message)
    {
        var file = Path.Combine(Folder, "reported");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file) == message)
                return false;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(file, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered: reported again next time, which is better than never.
        }
        return true;
    }

    private async Task InstallAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct)
    {
        var exe = InstalledExe ?? throw new UpdateException(Strings.UpdateNotInstalled);
        string verified;
        using (var http = service.Network.CreateHttpClient())
        {
            http.Timeout = Timeout.InfiniteTimeSpan; // a large file on a slow line; closing the window cancels it
            verified = await UpdateInstaller.DownloadAsync(http, update, Folder, progress, ct);
        }
        // The extension is made ready before anything changes; a problem with it never stops the program's update.
        // One at a time: a repair still running shares the staging folder.
        await ExtensionLock.WaitAsync(ct);
        try
        {
            var extensionReady = false;
            if (BundledExtension.Folder(ProgramFolder(exe)) is not null)
            {
                using var http = service.Network.CreateHttpClient();
                extensionReady = await PrepareExtensionAsync(http, update, exe, ct);
            }
            File.WriteAllText(PreviousSchemaPath, DownloadStore.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            UpdateInstaller.Swap(verified, exe);
            if (extensionReady)
            {
                try
                {
                    ExtensionPackage.Swap(ProgramFolder(exe));
                    Log.Info("update", "extension updated next to the program");
                }
                catch (UpdateException ex)
                {
                    Log.Warning("update", $"extension not swapped: {ex.Message}"); // the new version tries again once started
                }
            }
            try
            {
                // The previous version watches the new one and puts itself back if the new one does not start.
                StartDetached(UpdateInstaller.BackupOf(exe), UpdateInstaller.WatchArgument, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                UpdateInstaller.Restore(exe);
                RestoreExtension(exe, Version.Parse(App.AppVersion)); // this version keeps running: its extension comes back too
                throw;
            }
        }
        finally
        {
            ExtensionLock.Release();
        }
        Log.Info("update", $"installing {update.Version}");
        exit();
    }

    private static string ProgramFolder(string exe) => Path.GetDirectoryName(exe)!;

    /// <summary>The extension next to the program is changed by one update or repair at a time.</summary>
    private static readonly SemaphoreSlim ExtensionLock = new(1, 1);

    /// <summary>The extension's package staged next to the program; a problem is logged and leaves it as it is.</summary>
    private static async Task<bool> PrepareExtensionAsync(HttpClient http, UpdateInfo update, string exe, CancellationToken ct)
    {
        try
        {
            await ExtensionPackage.PrepareAsync(http, update.Extension, update.Version, ProgramFolder(exe), Folder, ct);
            return true;
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // The new version tries again once started, and says why if it cannot (RepairExtensionAsync).
            Log.Warning("update", $"extension {update.Version.ToString(3)} not prepared: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// An update made by 1.3.2 or earlier replaced only Baton.exe: an older extension folder next to this version gets the
    /// signed package of this very version. Run after an update and with the update checks (the only times the app
    /// contacts the update address); outside <paramref name="interactive"/> a failure is reported once.
    /// </summary>
    public async Task RepairExtensionAsync(string address, bool interactive)
    {
        if (InstalledExe is not { } exe || address.Trim().Length == 0 || !await ExtensionLock.WaitAsync(0))
            return; // a repair or an update is already at work on it
        try
        {
            using var http = service.Network.CreateHttpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (await ExtensionPackage.RepairAsync(http, address.Trim(), Version.Parse(App.AppVersion), ProgramFolder(exe), Folder, timeout.Token))
                Log.Info("update", $"extension {App.AppVersion} installed next to the program");
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Log.Warning("update", $"extension not updated: {ex.Message}");
            var message = string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateFailed, App.AppVersion, ex.Message);
            if (interactive || FirstReport(message))
                dialogs.ShowError(message, Strings.UpdateTitle);
        }
        finally
        {
            ExtensionLock.Release();
        }
    }

    /// <summary>The ".old" extension comes back with the program it belongs to; a problem is only logged.</summary>
    private static void RestoreExtension(string exe, Version restoredVersion)
    {
        try
        {
            if (ExtensionPackage.Restore(ProgramFolder(exe), restoredVersion))
                Log.Info("update", $"extension {restoredVersion.ToString(3)} restored");
        }
        catch (UpdateException ex)
        {
            Log.Warning("update", $"extension not restored: {ex.Message}");
        }
    }

    public void RestorePrevious()
    {
        if (InstalledExe is not { } exe || !File.Exists(UpdateInstaller.BackupOf(exe)))
        {
            dialogs.Inform(Strings.UpdateNoPrevious, Strings.UpdateTitle);
            return;
        }
        // The previous version cannot open a database this one has migrated: going back would lose the list.
        if (File.Exists(PreviousSchemaPath) && int.TryParse(File.ReadAllText(PreviousSchemaPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var previous)
            && previous < DownloadStore.SchemaVersion)
        {
            dialogs.ShowError(Strings.UpdateRestoreSchema, Strings.UpdateTitle);
            return;
        }
        // A version from before the rename goes back as DownloadManager.exe, with the data it had (never changed by Baton).
        var restored = UpdateInstaller.VersionOf(UpdateInstaller.BackupOf(exe)) < UpdateInstaller.FirstBatonVersion
            ? Path.Combine(Path.GetDirectoryName(exe)!, UpdateChecker.LegacyExeName)
            : exe;
        if (!dialogs.Confirm(restored == exe ? Strings.UpdateConfirmRestore : Strings.UpdateConfirmRestoreLegacy, Strings.UpdateTitle))
            return;
        var restoredVersion = UpdateInstaller.VersionOf(UpdateInstaller.BackupOf(exe));
        UpdateInstaller.Restore(exe, restored == exe ? null : restored);
        RestoreExtension(exe, restoredVersion);
        if (restored != exe)
            AppEnvironment.Registration.RestoreLegacyStartEntry(restored);
        Log.Info("update", "previous version restored");
        StartDetached(restored, UpdateInstaller.UpdatedArgument);
        exit();
    }

    /// <summary>
    /// The watcher (run by the previous version, as "Baton.exe.old --update-watch PID"): waits for the app to
    /// close, starts the new version and waits for its mark; otherwise restores the previous version and starts it.
    /// </summary>
    public static int RunWatcher(int appProcessId)
    {
        var self = Environment.ProcessPath;
        if (self is null || !self.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            return 1;
        Log.Configure(Path.Combine(App.DataDirectory, "logs"));
        var exe = self[..^4];
        var ok = UpdateInstaller.WatchAsync(exe, MarkerPath,
            async () =>
            {
                try
                {
                    using var app = Process.GetProcessById(appProcessId);
                    using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    await app.WaitForExitAsync(wait.Token);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
                {
                    // Already closed, or still closing after five minutes: the new version waits for it in turn.
                }
            },
            path => new ProcessHandle(StartDetached(path, UpdateInstaller.UpdatedArgument)),
            TimeSpan.FromSeconds(90),
            path =>
            {
                RestoreDatabase();
                RestoreExtension(path, UpdateInstaller.VersionOf(path));
                StartDetached(path, UpdateInstaller.UpdatedArgument, RolledBackArgument).Dispose();
            }).GetAwaiter().GetResult();
        Log.Info("update", ok ? "new version started" : "new version did not start: previous version restored");
        return ok ? 0 : 1;
    }

    /// <summary>If the version that did not start had already migrated the database, its copy from before is put back.</summary>
    private static void RestoreDatabase()
    {
        try
        {
            if (DownloadStore.RestoreBackupForThisVersion(DatabasePath))
                Log.Info("update", "database restored from the copy taken before the update");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("update", "cannot restore the database", ex);
        }
    }

    /// <summary>Called once the new version is up: the watcher stops waiting (a 1.1 watcher looks in its own folder).</summary>
    public static void MarkStarted()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(MarkerPath, App.AppVersion);
            if (LegacyMarkerPath is { } legacy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
                File.WriteAllText(legacy, App.AppVersion);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("update", "cannot write the start mark", ex);
        }
    }

    private static Process StartDetached(string exe, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new UpdateException(Strings.UpdateNotInstalled);
    }
}
