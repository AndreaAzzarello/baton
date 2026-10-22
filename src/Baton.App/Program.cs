using System.IO;
using System.Windows;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Resources;

namespace Baton.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Started by the browser extension: act as the native messaging host, without any window. It answers fast and does
        // not open the database for the language: its only text (the app did not answer) follows the language of Windows.
        if (NativeHost.IsHostInvocation(args))
            return BrowserIntegration.RunHost();

        // The messages shown before the app starts, and the app itself, in the chosen language.
        Localizer.SetCulture(DownloadStore.LanguageOf(Path.Combine(App.DataDirectory, "downloads.db")));

        // The installer's questions (copies found, data to import, registry clean-up when uninstalling).
        if (SetupCommands.Run(args) is { } setupResult)
            return setupResult;

        // "Register" on the extension's connection page: point the browsers at this executable again.
        if (args.Length == 1 && BrowserIntegration.IsRegisterLink(args[0]))
            return BrowserIntegration.RegisterFromBrowser();

        // A button of a completion notification ("baton:open?..."): handled without starting the app.
        if (args.Length == 1 && NotificationLinks.IsLink(args[0]) && HandleNotificationLink(args[0]) is { } exitCode)
            return exitCode;

        // Run by the previous version after an update: start the new one and put the old one back if it fails.
        if (args.Length == 2 && args[0] == Core.Updates.UpdateInstaller.WatchArgument && int.TryParse(args[1], out var appProcessId))
            return UpdateService.RunWatcher(appProcessId);

        // One instance per user: a second launch brings the running window forward instead.
        using var instance = new Mutex(true, @"Local\" + AppEnvironment.PipeName, out var first);
        // Started after an update or a restore: the previous instance is closing, wait for it instead of activating it.
        if (!first && args.Contains(Core.Updates.UpdateInstaller.UpdatedArgument))
        {
            try
            {
                first = instance.WaitOne(TimeSpan.FromMinutes(2));
            }
            catch (AbandonedMutexException)
            {
                first = true;
            }
        }
        if (!first)
        {
            BrowserIntegration.AllowForeground();
            AppChannel.SendAsync(AppEnvironment.PipeName, new ChannelMessage(Messages.Activate), TimeSpan.FromSeconds(3))
                .GetAwaiter().GetResult();
            return 0;
        }

        // An update from 1.1 put this version in place of DownloadManager.exe: Baton.exe takes over.
        if (LegacyExe.IsLegacyName(Environment.ProcessPath) && LegacyExe.HandOver(Environment.ProcessPath!, args, instance) is { } handedOver)
            return handedOver;

        // DownloadManager (1.1) still open on the data to copy: both apps would run the same downloads. It is closed first.
        if (AppEnvironment.LegacyAppRunning)
        {
            MessageBox.Show(Strings.LegacyAppRunning, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var app = new App { MigrationError = AppEnvironment.MigrateLegacyData() };
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Opens the file or its folder. Links come from notifications but could also come from a web page, so only
    /// links signed with this user's secret are accepted. Returns null for "show", which starts or activates the app.
    /// </summary>
    private static int? HandleNotificationLink(string link)
    {
        var links = NotificationLinks.FromKeyFile(ToastService.KeyPath(App.DataDirectory));
        if (!links.TryParse(link, DateTime.UtcNow, out var action, out var path, out var downloadId))
        {
            MessageBox.Show(Strings.ErrorNotificationExpired, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        if (action == NotificationAction.Show)
            return null;

        // The link opens only the file of its own download, still finished and still at that place.
        string? current;
        try
        {
            current = DownloadStore.CompletedFileOf(Path.Combine(App.DataDirectory, "downloads.db"), downloadId);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            current = null;
        }
        if (current is null || !string.Equals(Path.GetFullPath(current), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(Strings.ErrorNotificationExpired, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        if (!File.Exists(path))
        {
            MessageBox.Show(Strings.ErrorFileMissing, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        var shell = new ShellService();
        if (action == NotificationAction.Open)
            shell.OpenFile(path);
        else
            shell.Reveal(path, Path.GetDirectoryName(path)!);
        return 0;
    }
}
