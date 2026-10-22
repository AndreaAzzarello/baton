using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.App.Views;
using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Net;
using Baton.Core.Resources;

namespace Baton.App;

/// <summary>Composition root: creates the services, the main view model, the window and the notification area icon.</summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _channelCts = new();
    private readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly WakeTimer _wakeTimer = new();
    private DownloadStore? _store;
    private DownloadService? _service;
    private Core.Grabber.GrabberService? _grabber;
    private MainViewModel? _viewModel;
    private ToastService? _toasts;
    private TrayIcon? _tray;
    private bool _runningEndAction;
    private DispatcherTimer? _trimTimer;

    /// <summary>
    /// In the notification area the app mostly waits: a few seconds after the window hides (or after a start in the tray),
    /// memory used by the window and the startup is given back to Windows. Nothing happens if the window came back.
    /// </summary>
    private void ScheduleTrim(Window window)
    {
        _trimTimer?.Stop();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (window.IsVisible)
                return;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        };
        _trimTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    /// <summary>Database and settings; BATON_DATA points the app at another folder (and sandboxes it), for tests.</summary>
    public static string DataDirectory => AppEnvironment.DataDirectory;

    /// <summary>Why DownloadManager's data could not be copied this time (the app then works on the old folder), if it failed.</summary>
    public string? MigrationError { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Configure(Path.Combine(DataDirectory, "logs"));
        Log.Info("app", $"start {AppVersion} {string.Join(' ', e.Args)}");
        DispatcherUnhandledException += OnUnhandledException;
        // Errors on other threads cannot be shown as they happen: they are logged, and a crash leaves its details there.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("task", "unobserved", args.Exception.GetBaseException());
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("crash", "unhandled", args.ExceptionObject as Exception);

        var databasePath = Path.Combine(DataDirectory, "downloads.db");
        var firstRun = Core.Setup.FirstRun.IsNeeded(databasePath);
        _store = new DownloadStore(databasePath);
        _service = new DownloadService(_store) { RecycleBin = new Core.Platform.WindowsRecycleBin() };
        var service = _service;

        Localizer.Apply(service.Settings.Language);
        var themes = new ThemeService(this);
        themes.Apply(service.Settings.Theme);

        _toasts = new ToastService(DataDirectory);
        _toasts.Register();
        service.DownloadCompleted += snapshot =>
        {
            if (!service.Settings.NotifyOnCompletion)
                return;
            // The portable copy has no notification identity in the registry: the notification area icon tells instead.
            if (AppEnvironment.IsPortable)
                Dispatcher.InvokeAsync(() => _tray?.ShowHint(Strings.ToastCompletedTitle + ": " + snapshot.DisplayName));
            else
                _toasts.ShowCompleted(snapshot);
        };

        var shell = new ShellService();
        var dialogs = new DialogService(() => service, shell);
        _grabber = new Core.Grabber.GrabberService(service, _store);
        service.ResumePendingChecks(); // now that the notifications and the grabber listen
        _viewModel = new MainViewModel(service, _store, dialogs, shell, new StartupService(), new GrabberUi(_grabber, service, dialogs, shell));
        var portableHost = service.Settings.PortableBrowserHost;
        _viewModel.SettingsSaved += settings =>
        {
            themes.Apply(settings.Theme);
            if (Localizer.Apply(settings.Language))
                _viewModel.OnLanguageChanged();
            // Only a change of the check box counts as the user's answer.
            if (AppEnvironment.IsPortable && settings.PortableBrowserHost is { } allowed && allowed != portableHost)
            {
                portableHost = allowed;
                ApplyPortableHost(allowed, chosenNow: true);
            }
        };

        var window = new MainWindow(_viewModel, service.ShutdownAsync, () => service.Settings.CloseToTray && _tray is not null);
        MainWindow = window;
        _tray = new TrayIcon(service, () => window.BringToFront(), window.ExitApp, current => dialogs.AskSpeedLimit(current));
        var hintShown = false;
        window.HiddenToTray += () =>
        {
            ScheduleTrim(window);
            if (hintShown)
                return;
            hintShown = true;
            _tray.ShowHint(Strings.TrayHiddenHint);
        };

        // End actions: a cancellable countdown, then the action (simulated when BATON_SIMULATE_POWER=1).
        IPowerController power = SimulatedPower.IsRequested
            ? new SimulatedPower(Path.Combine(DataDirectory, "simulated-power.log"))
            : new WindowsPower();
        service.QueueFinished += queue => Dispatcher.InvokeAsync(() => RunEndAction(queue, dialogs, power, window));

        // A server or proxy wants a login, or an FTPS server shows a certificate Windows does not trust: ask the user.
        service.LoginRequired += request => Dispatcher.InvokeAsync(() => AskLogin(request, dialogs));
        service.Certificates.Ask = question => Dispatcher.InvokeAsync(() => dialogs.AskCertificate(question)).Task;

        // A project run by its schedule says how it went, from the notification area.
        _grabber.RunFinished += run =>
        {
            if (run.Automatic && run.Result is { } result)
                Dispatcher.InvokeAsync(() => _tray?.ShowHint(string.Format(CultureInfo.CurrentCulture, Strings.GrabberAutoFinished, run.Project.Name,
                    Core.Grabber.GrabberService.Summary(result))));
        };

        _systemTimer.Tick += (_, _) => UpdateSystemState();
        _systemTimer.Start();
        UpdateSystemState();

        if (firstRun)
            RunFirstRun(service, dialogs, shell, themes);
        else
            _ = OfferDesktopRelocationAsync(service, dialogs);

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
        else
        {
            ScheduleTrim(window);
        }

        var updates = new UpdateService(service, dialogs, window.ExitApp);
        _viewModel.Updates = updates;
        if (e.Args.Contains(Core.Updates.UpdateInstaller.UpdatedArgument))
        {
            // Up and running: the previous version, waiting as a watcher, may stop.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, UpdateService.MarkStarted);
            if (e.Args.Contains(UpdateService.RolledBackArgument))
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => dialogs.ShowError(Strings.UpdateRolledBack, Strings.UpdateTitle));
            else // once committed: an update made by 1.3.2 or earlier left the extension next to the program at its old version
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => updates.RepairExtensionAsync(service.Settings.UpdateUrl, interactive: false));
        }
        else if (service.Settings.UpdateCheckEnabled)
        {
            // Not at the very start, which belongs to the downloads; silent unless a newer version exists.
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Dispatcher.InvokeAsync(() => updates.CheckAsync(service.Settings.UpdateUrl, interactive: false)),
                TaskScheduler.Default);
        }

        if (AppEnvironment.IsPortable)
        {
            // Nothing in the registry but the browsers' host, and only once the user agreed.
            if (service.Settings.PortableBrowserHost is null)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => AskPortableHost(service, dialogs));
            else
                ApplyPortableHost(service.Settings.PortableBrowserHost == true);
        }
        else
        {
            BrowserIntegration.Register(DataDirectory);
            RepairStartEntry();
            UpdateInstalledVersion();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, LegacyCleanup.Start); // once up: after the start mark
        }
        if (MigrationError is { } migrationError)
        {
            Log.Error("migration", $"DownloadManager's data not copied, using {DataDirectory}: {migrationError}");
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => dialogs.ShowError(
                string.Format(CultureInfo.CurrentCulture, Strings.MigrationFailed, DataDirectory, migrationError), Strings.AppName));
        }
        _ = AppChannel.ServeAsync(AppEnvironment.PipeName, OnChannelMessage, _channelCts.Token);
    }

    /// <summary>When the extension last said hello (its popup or its connection page), for the first-run check.</summary>
    private static DateTime? LastExtensionContactUtc { get; set; }

    private static void RunFirstRun(DownloadService service, IDialogService dialogs, IShellService shell, ThemeService themes)
    {
        var wizard = new FirstRunViewModel(dialogs, shell, service.Categories, Core.Setup.BrowserDetector.Detect(),
            AppContext.BaseDirectory, () => LastExtensionContactUtc, AppEnvironment.IsPortable ? Environment.ProcessPath : null);
        var settings = service.Settings;
        settings.DesktopRelocationAsked = true; // a new installation never starts on the Desktop
        service.UpdateSettings(settings);
        if (dialogs.ShowFirstRun(wizard) is not { } choices)
            return;
        Core.Setup.FirstRun.Apply(service, choices);
        themes.Apply(choices.Theme);
        if (wizard.StartWithWindows)
            new StartupService().Set(true);
        Log.Info("app", "first run completed");
    }

    /// <summary>
    /// Earlier versions could keep downloads on the Desktop: proposed once, the folders move only on "Yes", and the files
    /// already downloaded only after a second, separate "Yes".
    /// </summary>
    private static async Task OfferDesktopRelocationAsync(DownloadService service, IDialogService dialogs)
    {
        var settings = service.Settings;
        if (settings.DesktopRelocationAsked)
            return;
        var plan = Core.Setup.DesktopRelocation.Plan(settings, service.Categories, service.ExportRecords(), Core.Setup.DesktopRelocation.DesktopFolders(),
            Core.Setup.DesktopRelocation.DefaultTarget);
        if (plan.IsEmpty)
            return;
        var folders = string.Join(Environment.NewLine, plan.Folders.Select(f =>
            $"• {(f.Name.Length > 0 ? f.Name : Strings.RelocationDownloadFolder)}: {f.From} → {f.To}"));
        var answer = dialogs.Ask(string.Format(CultureInfo.CurrentCulture, Strings.RelocationQuestion, folders), Strings.RelocationTitle);
        settings.DesktopRelocationAsked = true;
        service.UpdateSettings(settings);
        if (answer != Answer.Yes)
            return;
        var moveFiles = plan.Files.Count > 0
            && dialogs.Ask(string.Format(CultureInfo.CurrentCulture, Strings.RelocationMoveFiles, plan.Files.Count), Strings.RelocationTitle) == Answer.Yes;
        // In the background: moving many files to another drive takes a while, and the window must stay responsive.
        var result = await Task.Run(() => service.ApplyRelocation(plan, moveFiles));
        var message = string.Format(CultureInfo.CurrentCulture, Strings.RelocationDone, result.FilesMoved);
        if (result.Problems.Count > 0)
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Problems.Take(10));
        dialogs.Inform(message, Strings.RelocationTitle);
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

    /// <summary>The portable copy's one question: may it register itself as the browsers' native messaging host?</summary>
    private static void AskPortableHost(DownloadService service, IDialogService dialogs)
    {
        var answer = dialogs.Ask(string.Format(CultureInfo.CurrentCulture, Strings.PortableHostQuestion, DataDirectory), Strings.AppName);
        if (answer == Answer.Cancel)
            return; // asked again at the next start
        var settings = service.Settings;
        settings.PortableBrowserHost = answer == Answer.Yes;
        service.UpdateSettings(settings);
        ApplyPortableHost(answer == Answer.Yes, chosenNow: true);
    }

    /// <summary>Registers this portable copy as the browsers' host, or removes the registration that points to it.</summary>
    /// <param name="chosenNow">
    /// The user just chose it. At a start, a host that another copy still on the disk holds (an installed Baton) stays with
    /// it: only the user's choice takes it over, so two copies never keep taking it from each other.
    /// </param>
    private static void ApplyPortableHost(bool allowed, bool chosenNow = false)
    {
        // Writes to the host keys are let through for this change only; the rest of the time the copy writes nothing.
        using var _ = Core.Platform.PortableUserRegistry.Allow(AppEnvironment.Registry, browserHost: true);
        try
        {
            if (!allowed)
                AppEnvironment.Registration.Unregister(browserHostOnly: true);
            else if (chosenNow || !AppEnvironment.Registration.BrowserHostHeldByAnother)
                BrowserIntegration.Register(DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("portable", "cannot change the browser host", ex);
        }
    }

    /// <summary>After the program replaced itself with an update, "Installed apps" shows the new version.</summary>
    private static void UpdateInstalledVersion()
    {
        try
        {
            AppEnvironment.Registration.UpdateInstalledVersion(AppVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("setup", "cannot update the installed version", ex);
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
        // Running grabber projects stop; what they found so far is saved.
        _grabber?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
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
                (action, length) => Task.FromResult(dialogs.ShowCountdown(action, queue.DisplayName, length)),
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
    private async Task<ChannelReply> OnChannelMessage(ChannelMessage message)
    {
        // Messages were validated by the channel (known types, sizes, address schemes) before reaching here.
        switch (message.Type)
        {
            case Messages.Ping:
                return new ChannelReply(true);

            case Messages.Hello:
                LastExtensionContactUtc = DateTime.UtcNow;
                return new ChannelReply(true, Protocol: Messages.ProtocolVersion, AppVersion: AppVersion, ExtensionVersion: BundledExtensionVersion);

            case Messages.Activate:
                _ = Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.BringToFront());
                return new ChannelReply(true);

            case Messages.Queues:
                // For the extension's popup: the main queue first, as in the app.
                var names = _service?.Queues.OrderByDescending(q => q.IsMain).Select(q => q.DisplayName).ToList() ?? [];
                return new ChannelReply(true, Queues: names);

            case Messages.Add when message.Download is { } download:
                // The browser gets its answer right away; the confirmation dialog does not keep it waiting.
                OnUi(closing => _viewModel?.ReceiveFromBrowser(download, closing));
                return new ChannelReply(true);

            case Messages.ProbeMedia when message.Media is { } media && _service is not null:
                try
                {
                    return new ChannelReply(true, Protocol: Messages.ProtocolVersion, Media: await _service.ProbeMediaAsync(media));
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TimeoutException or DownloadFailedException
                                               or AuthenticationRequiredException or FormatException or System.Xml.XmlException
                                               or OperationCanceledException or NotSupportedException or ArgumentException)
                {
                    return new ChannelReply(false, Error: string.Format(CultureInfo.CurrentCulture, Strings.MediaProbeFailed, ex.Message));
                }

            case Messages.AddMedia when message.Media is { } media:
                if (Core.Media.MediaPolicy.IsRestricted(media.Url) || Core.Media.MediaPolicy.IsRestricted(media.PageUrl))
                    return new ChannelReply(false, Error: Strings.MediaBlockedTerms);
                OnUi(closing => _viewModel?.ReceiveMedia(media, closing));
                return new ChannelReply(true);

            case Messages.Batch when message.Batch is { } batch:
                OnUi(closing => _viewModel?.ReceiveBatch(batch, closing));
                return new ChannelReply(true);

            case Messages.Grab when message.Grab is { } grab:
                OnUi(closing => _viewModel?.ReceiveGrab(grab, closing));
                return new ChannelReply(true, Protocol: Messages.ProtocolVersion);

            default:
                return new ChannelReply(false, Error: string.Format(Strings.ErrorUnknownMessage, message.Type));
        }
    }

    /// <summary>Runs on the UI thread, telling whether the app is closing (requests then wait instead of opening dialogs).</summary>
    private void OnUi(Action<bool> action) =>
        Dispatcher.InvokeAsync(() => action((MainWindow as MainWindow)?.IsClosing == true));

    /// <summary>The app's version, e.g. "1.0.0", reported to the extension's connection check.</summary>
    /// <summary>The version of the extension folder next to the program, read again at every hello (an update may have just replaced it).</summary>
    public static string? BundledExtensionVersion => Core.Updates.ExtensionPackage.VersionIn(AppContext.BaseDirectory)?.ToString(3);

    public static string AppVersion => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary>
    /// Every unexpected error on the UI thread ends here: logged, and shown with its details folded and a "Copy details"
    /// button, so a problem can be reported. The app keeps running.
    /// </summary>
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", "unexpected error", e.Exception);
        e.Handled = true;
        try
        {
            new MessageWindow(string.Format(CultureInfo.CurrentCulture, Strings.ErrorUnexpectedText, e.Exception.Message), Strings.ErrorUnexpected,
                MessageKind.Error, [(Strings.Ok, Answer.Yes)], Answer.Yes, ErrorDetails(e.Exception))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            }.ShowDialog();
        }
        catch (InvalidOperationException)
        {
            // The error happened while the app was closing: the log has it.
        }
    }

    /// <summary>What a report needs: versions and the error with its stack, with addresses and secrets masked.</summary>
    public static string ErrorDetails(Exception exception)
    {
        var lines = new List<string>
        {
            $"Baton {AppVersion}",
            $"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "",
        };
        lines.AddRange(exception.ToString().Split('\n').Select(l => l.TrimEnd('\r')));
        return string.Join(Environment.NewLine, lines.Select(Log.Redact));
    }
}
