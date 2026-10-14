using System.IO;
using System.Windows;
using System.Windows.Threading;
using DownloadManager.App.Services;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Resources;

namespace DownloadManager.App;

/// <summary>Composition root: creates the services, the main view model and the window.</summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _channelCts = new();
    private DownloadStore? _store;
    private DownloadService? _service;
    private MainViewModel? _viewModel;
    private ToastService? _toasts;

    /// <summary>Database and settings; DOWNLOADMANAGER_DATA points the app at another folder, for example to test.</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _store = new DownloadStore(Path.Combine(DataDirectory, "downloads.db"));
        _service = new DownloadService(_store);
        var service = _service;

        var themes = new ThemeService(this);
        themes.Apply(service.Settings.Theme);

        _toasts = new ToastService(DataDirectory);
        _toasts.Register();
        service.DownloadCompleted += snapshot =>
        {
            if (service.Settings.NotifyOnCompletion)
                _toasts.ShowCompleted(snapshot);
        };

        var shell = new ShellService();
        var dialogs = new DialogService(() => service, shell);
        _viewModel = new MainViewModel(service, _store, dialogs, shell);
        _viewModel.SettingsSaved += settings => themes.Apply(settings.Theme);

        var window = new MainWindow(_viewModel, service.ShutdownAsync);
        MainWindow = window;
        if (e.Args.Contains(BrowserIntegration.BackgroundArgument))
        {
            window.ShowActivated = false;
            window.WindowState = WindowState.Minimized;
        }
        window.Show();

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
                if (!Urls.TryParseHttp(download.Url, out _))
                    return Task.FromResult(new ChannelReply(false, Error: Strings.ErrorOnlyHttp));
                // The browser gets its answer right away; the confirmation dialog does not keep it waiting.
                Dispatcher.InvokeAsync(() => _viewModel?.ReceiveFromBrowser(download, (MainWindow as MainWindow)?.IsClosing == true));
                return Task.FromResult(new ChannelReply(true));

            default:
                return Task.FromResult(new ChannelReply(false, Error: string.Format(Strings.ErrorUnknownMessage, message.Type)));
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, Strings.ErrorUnexpected, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
