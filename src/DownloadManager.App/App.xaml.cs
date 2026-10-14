using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DownloadManager.App.Services;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Net;
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

    /// <summary>Database and settings; DOWNLOADMANAGER_DATA points the app at another folder (and sandboxes it), for tests.</summary>
    public static string DataDirectory => AppEnvironment.DataDirectory;

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

        // A server or proxy wants a login, or an FTPS server shows a certificate Windows does not trust: ask the user.
        service.LoginRequired += request => Dispatcher.InvokeAsync(() => AskLogin(request, dialogs));
        service.Certificates.Ask = question => Dispatcher.InvokeAsync(() => dialogs.AskCertificate(question)).Task;

        _systemTimer.Tick += (_, _) => UpdateSystemState();
        _systemTimer.Start();
        UpdateSystemState();

        var startHidden = e.Args.Contains(Core.Platform.SystemRegistration.TrayArgument);
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
        RepairStartEntry();
        _ = AppChannel.ServeAsync(AppEnvironment.PipeName, OnChannelMessage, _channelCts.Token);
    }

    private readonly Dictionary<string, List<LoginRequest>> _waitingForLogin = [];

    /// <summary>
    /// One dialog per server: downloads that ask while it is open wait for the same answer instead of opening
    /// a dialog each.
    /// </summary>
    private void AskLogin(LoginRequest request, IDialogService dialogs)
    {
        var key = $"{request.IsProxy}|{request.Target.Host}:{request.Target.Port}";
        if (_waitingForLogin.TryGetValue(key, out var waiting))
        {
            waiting.Add(request);
            return;
        }
        _waitingForLogin[key] = waiting = [request];
        try
        {
            if (dialogs.AskLogin(request) is not { } answer || _service is null)
                return;
            _service.ProvideLogin(request, answer.Username, answer.Password, answer.Remember);
            foreach (var other in waiting.Skip(1))
                _service.Start(other.DownloadId);
        }
        finally
        {
            _waitingForLogin.Remove(key);
        }
    }

    /// <summary>One start entry at most, pointing at this executable (only if the user turned it on).</summary>
    private static void RepairStartEntry()
    {
        try
        {
            AppEnvironment.Registration.RepairStartEntry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
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

            case Messages.Queues:
                // For the extension's popup: the main queue first, as in the app.
                var names = _service?.Queues.OrderByDescending(q => q.IsMain).Select(q => q.Name).ToList() ?? [];
                return Task.FromResult(new ChannelReply(true, Queues: names));

            case Messages.Add when message.Download is { } download:
                if (!Urls.TryParseDownload(download.Url, out _))
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
