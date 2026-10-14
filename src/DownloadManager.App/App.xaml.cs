using System.Globalization;
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

/// <summary>Composition root: creates the services, the main view model, the window and the notification area icon.</summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _channelCts = new();
    private readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly WakeTimer _wakeTimer = new();
    private DownloadStore? _store;
    private DownloadService? _service;
    private MainViewModel? _viewModel;
    private ToastService? _toasts;
    private TrayIcon? _tray;
    private bool _runningEndAction;

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
        _viewModel = new MainViewModel(service, _store, dialogs, shell, new StartupService());
        _viewModel.SettingsSaved += settings => themes.Apply(settings.Theme);

        var window = new MainWindow(_viewModel, service.ShutdownAsync, () => service.Settings.CloseToTray && _tray is not null);
        MainWindow = window;
        _tray = new TrayIcon(service, () => window.BringToFront(), window.ExitApp, current => dialogs.AskSpeedLimit(current));
        var hintShown = false;
        window.HiddenToTray += () =>
        {
            if (hintShown)
                return;
            hintShown = true;
            _tray.ShowHint(Strings.TrayHiddenHint);
        };

        // End actions: a cancellable countdown, then the action (simulated when DOWNLOADMANAGER_SIMULATE_POWER=1).
        IPowerController power = SimulatedPower.IsRequested
            ? new SimulatedPower(Path.Combine(DataDirectory, "simulated-power.log"))
            : new WindowsPower();
        service.QueueFinished += queue => Dispatcher.InvokeAsync(() => RunEndAction(queue, dialogs, power, window));

        _systemTimer.Tick += (_, _) => UpdateSystemState();
        _systemTimer.Start();
        UpdateSystemState();

        var startHidden = e.Args.Contains(StartupRegistration.TrayArgument);
        var fromBrowser = e.Args.Contains(BrowserIntegration.BackgroundArgument);
        if (!startHidden)
        {
            if (fromBrowser)
            {
                window.ShowActivated = false;
                window.WindowState = WindowState.Minimized;
            }
            window.Show();
        }

        BrowserIntegration.Register(DataDirectory);
        _ = AppChannel.ServeAsync(AppChannel.DefaultPipeName, OnChannelMessage, _channelCts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _channelCts.Cancel();
        _systemTimer.Stop();
        _tray?.Dispose();
        _wakeTimer.Dispose();
        _store?.Dispose();
        base.OnExit(e);
    }

    private async void RunEndAction(DownloadQueue queue, IDialogService dialogs, IPowerController power, MainWindow window)
    {
        if (queue.EndAction == EndAction.None || _runningEndAction || _service is null)
            return;
        _runningEndAction = true;
        try
        {
            var runner = new EndActionRunner(
                power,
                (action, length) => Task.FromResult(dialogs.ShowCountdown(action, queue.Name, length)),
                _service.ShutdownAsync,
                window.ExitApp);
            if (await runner.RunAsync(queue.EndAction) && queue.EndAction == EndAction.Shutdown)
            {
                // Still running a minute later: the shutdown was cancelled (or simulated), so the queues carry on.
                await Task.Delay(TimeSpan.FromMinutes(1));
                _service.ResumeAfterShutdown();
            }
        }
        finally
        {
            _runningEndAction = false;
        }
    }

    /// <summary>Tooltip of the notification area icon, and the wake timer for the next scheduled start.</summary>
    private void UpdateSystemState()
    {
        if (_service is null || _viewModel is null)
            return;
        var snapshots = _service.GetSnapshots();
        var statuses = _service.GetQueueStatuses();
        _tray?.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.TrayStatus,
            snapshots.Count(s => s.Status == DownloadStatus.Downloading),
            statuses.Count(s => s.State == QueueState.Running),
            TrayIcon.LimitText(_service.Settings.SpeedLimitKBps)));

        var wakeFor = _service.Queues
            .Where(q => q.Schedule.Enabled && q.Schedule.WakeToRun)
            .Select(q => statuses.FirstOrDefault(s => s.QueueId == q.Id)?.NextStartUtc)
            .Where(t => t is not null)
            .Min();
        _wakeTimer.Set(wakeFor);
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
        new MessageWindow(e.Exception.Message, Strings.ErrorUnexpected, MessageKind.Error, [(Strings.Ok, Answer.Yes)], Answer.Yes)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        }.ShowDialog();
        e.Handled = true;
    }
}
