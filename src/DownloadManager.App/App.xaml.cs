using System.IO;
using System.Windows;
using System.Windows.Threading;
using DownloadManager.Core;
using DownloadManager.Core.Integration;

namespace DownloadManager.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _channelCts = new();
    private DownloadStore? _store;

    public static DownloadService Service { get; private set; } = null!;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _store = new DownloadStore(Path.Combine(DataDirectory, "downloads.db"));
        Service = new DownloadService(_store);

        MainWindow = new MainWindow();
        if (e.Args.Contains(BrowserIntegration.BackgroundArgument))
        {
            MainWindow.ShowActivated = false;
            MainWindow.WindowState = WindowState.Minimized;
        }
        MainWindow.Show();

        BrowserIntegration.Register(DataDirectory);
        _ = AppChannel.ServeAsync(AppChannel.DefaultPipeName, OnChannelMessage, _channelCts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _channelCts.Cancel();
        _store?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Messages from the browser extension (via the native host) or from a second app launch.</summary>
    private Task<ChannelReply> OnChannelMessage(ChannelMessage message)
    {
        switch (message.Type)
        {
            case Messages.Ping:
                return Task.FromResult(new ChannelReply(true));

            case Messages.Activate:
                Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.BringToFront());
                return Task.FromResult(new ChannelReply(true));

            case Messages.Add when message.Download is { } download:
                if (!Uri.TryCreate(download.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return Task.FromResult(new ChannelReply(false, Error: "Sono supportati solo indirizzi http e https."));
                // The browser gets its answer right away; the confirmation dialog does not keep it waiting.
                Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ReceiveFromBrowser(download));
                return Task.FromResult(new ChannelReply(true));

            default:
                return Task.FromResult(new ChannelReply(false, Error: $"Messaggio sconosciuto: {message.Type}"));
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Errore imprevisto", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
