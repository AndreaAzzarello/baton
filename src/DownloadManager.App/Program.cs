using System.IO;
using System.Windows;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Resources;

namespace DownloadManager.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Started by the browser extension: act as the native messaging host, without any window.
        if (NativeHost.IsHostInvocation(args))
            return BrowserIntegration.RunHost();

        // "Register" on the extension's connection page: point the browsers at this executable again.
        if (args.Length == 1 && BrowserIntegration.IsRegisterLink(args[0]))
            return BrowserIntegration.RegisterFromBrowser();

        // A button of a completion notification ("downloadmanager:open?..."): handled without starting the app.
        if (args.Length == 1 && NotificationLinks.IsLink(args[0]) && HandleNotificationLink(args[0]) is { } exitCode)
            return exitCode;

        // One instance per user: a second launch brings the running window forward instead.
        using var instance = new Mutex(true, @"Local\" + AppEnvironment.PipeName, out var first);
        if (!first)
        {
            BrowserIntegration.AllowForeground();
            AppChannel.SendAsync(AppEnvironment.PipeName, new ChannelMessage(Messages.Activate), TimeSpan.FromSeconds(3))
                .GetAwaiter().GetResult();
            return 0;
        }

        var app = new App();
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
        if (!links.TryParse(link, out var action, out var path))
            return 1;
        if (action == NotificationAction.Show)
            return null;

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
