using DownloadManager.Core.Integration;

namespace DownloadManager.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Started by the browser extension: act as the native messaging host, without any window.
        if (NativeHost.IsHostInvocation(args))
            return BrowserIntegration.RunHost();

        // One instance per user: a second launch brings the running window forward instead.
        using var instance = new Mutex(true, @"Local\" + AppChannel.DefaultPipeName, out var first);
        if (!first)
        {
            BrowserIntegration.AllowForeground();
            AppChannel.SendAsync(AppChannel.DefaultPipeName, new ChannelMessage(Messages.Activate), TimeSpan.FromSeconds(3))
                .GetAwaiter().GetResult();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
