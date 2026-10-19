using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Net;
using DownloadManager.Core.Resources;
using DownloadManager.Core.Security;

namespace DownloadManager.App.ViewModels;

/// <summary>A trusted certificate as a list entry (never more than host, subject and fingerprint).</summary>
public sealed record CertificateEntry(TrustedCertificate Certificate, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// The settings window: general options, network (proxy, timeouts, connections per server, user agent, FTP mode)
/// and the saved logins and certificates. General and network options are applied on "Save"; logins and
/// certificates are saved or removed right away with their own buttons.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _original;
    private readonly CredentialStore _credentials;
    private readonly CertificateTrust _certificates;
    private string? _error;
    private ProxyMode _proxyMode;
    private string _testResult = "";
    private bool _testing;
    private SiteCredential? _selectedLogin;
    private string _loginHost = "";
    private string _loginUser = "";
    private bool _loginWindows;
    private string? _loginMessage;
    private CertificateEntry? _selectedCertificate;

    public SettingsViewModel(IDialogService dialogs, AppSettings current, bool runAtStartup,
        CredentialStore? credentials = null, CertificateTrust? certificates = null,
        Func<string, Task>? checkUpdates = null, Action? restorePreviousVersion = null)
    {
        _dialogs = dialogs;
        _original = current.Clone();
        _credentials = credentials ?? new CredentialStore(null);
        _certificates = certificates ?? new CertificateTrust(null);
        RunAtStartup = runAtStartup;
        CloseToTray = current.CloseToTray;
        GlobalQuota = current.GlobalQuotaMBPerHour.ToString(CultureInfo.CurrentCulture);
        Connections = current.MaxConnections.ToString(CultureInfo.CurrentCulture);
        Concurrent = current.MaxConcurrentDownloads.ToString(CultureInfo.CurrentCulture);
        SpeedLimit = current.SpeedLimitKBps.ToString(CultureInfo.CurrentCulture);
        Folder = current.DownloadFolder;
        ConfirmBrowserDownloads = current.ConfirmBrowserDownloads;
        NotifyOnCompletion = current.NotifyOnCompletion;
        Theme = current.Theme;

        _proxyMode = current.ProxyMode;
        ProxyTypeChoice = ProxyTypes.First(t => t.Value == current.ProxyType);
        ProxyHost = current.ProxyHost;
        ProxyPort = current.ProxyPort.ToString(CultureInfo.CurrentCulture);
        ProxyUser = current.ProxyUser;
        ProxyPassword = current.ProxyPassword;
        ProxyBypass = current.ProxyBypass;
        PacUrl = current.PacUrl;
        TimeoutSeconds = current.TimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        MaxPerServer = current.MaxConnectionsPerServer.ToString(CultureInfo.CurrentCulture);
        UserAgent = current.UserAgent;
        FtpActiveMode = current.FtpActiveMode;
        _ffmpegPath = current.FfmpegPath;

        ComputeHashOnCompletion = current.ComputeHashOnCompletion;
        HashAlgorithmChoice = HashAlgorithms.FirstOrDefault(a => a.Value == current.HashAlgorithm) ?? HashAlgorithms[^1];
        ScanOnCompletion = current.ScanOnCompletion;
        _scannerPath = current.ScannerPath;
        ScannerArguments = current.ScannerArguments;
        ScanThreatCodes = current.ScanThreatCodes;
        ScanTimeout = current.ScanTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        UpdateCheckEnabled = current.UpdateCheckEnabled;
        UpdateUrl = current.UpdateUrl;

        BrowseCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFolder(Strings.SettingsFolder, Directory.Exists(Folder) ? Folder : null) is { } picked)
            {
                Folder = picked;
                OnPropertyChanged(nameof(Folder));
            }
        });
        BrowseFfmpegCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFile(Strings.SettingsFfmpegPick, "ffmpeg|ffmpeg.exe|*.exe|*.exe", FfmpegPath.Length > 0 ? FfmpegPath : null) is { } picked)
                FfmpegPath = picked;
        });
        BrowseScannerCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFile(Strings.SettingsScannerPick, Strings.SettingsScannerFilter, ScannerPath.Length > 0 ? ScannerPath : null) is { } picked)
                ScannerPath = picked;
        });
        ResetScannerCommand = new RelayCommand(() =>
        {
            ScannerPath = "";
            ScannerArguments = Core.PostDownload.VirusScanner.DefaultArguments;
            ScanThreatCodes = Core.PostDownload.VirusScanner.DefaultThreatCodes;
            OnPropertyChanged(nameof(ScannerArguments));
            OnPropertyChanged(nameof(ScanThreatCodes));
        });
        // The address as typed is checked, so it can be tried before saving.
        CheckUpdatesCommand = new RelayCommand(async () =>
        {
            if (checkUpdates is null || _checkingUpdates)
                return;
            _checkingUpdates = true;
            try
            {
                await checkUpdates(UpdateUrl.Trim());
            }
            finally
            {
                _checkingUpdates = false;
            }
        }, () => checkUpdates is not null && !_checkingUpdates);
        RestorePreviousCommand = new RelayCommand(() => restorePreviousVersion?.Invoke(), () => restorePreviousVersion is not null);
        TestProxyCommand = new RelayCommand(TestProxy, () => !_testing);
        NewLoginCommand = new RelayCommand(() => SelectedLogin = null);
        SaveLoginCommand = new RelayCommand(SaveLogin, () => LoginHost.Trim().Length > 0 && (LoginUser.Trim().Length > 0 || LoginWindowsAccount));
        DeleteLoginCommand = new RelayCommand(DeleteLogin, () => SelectedLogin is not null);
        ForgetCertificateCommand = new RelayCommand(ForgetCertificate, () => SelectedCertificate is not null);
        ReloadLogins();
        ReloadCertificates();
    }

    // General

    public string Connections { get; set; }
    public string Concurrent { get; set; }
    public string SpeedLimit { get; set; }
    public string Folder { get; set; }
    public bool ConfirmBrowserDownloads { get; set; }

    private string _ffmpegPath;

    /// <summary>ffmpeg.exe for joining the video and audio of streams; empty = look in PATH.</summary>
    public string FfmpegPath
    {
        get => _ffmpegPath;
        set
        {
            if (Set(ref _ffmpegPath, value ?? ""))
                OnPropertyChanged(nameof(FfmpegStatus));
        }
    }

    /// <summary>Which ffmpeg streams will use, or that there is none (they are then saved as separate files or .ts).</summary>
    public string FfmpegStatus => Core.Media.Ffmpeg.Find(FfmpegPath) is { } found
        ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsFfmpegFound, found)
        : FfmpegPath.Trim().Length > 0 ? Strings.SettingsFfmpegMissingFile : Strings.SettingsFfmpegNone;

    public ICommand BrowseFfmpegCommand { get; }
    public bool NotifyOnCompletion { get; set; }
    public bool RunAtStartup { get; set; }
    public bool CloseToTray { get; set; }
    public string GlobalQuota { get; set; }
    public AppTheme Theme { get; set; }

    // One property per radio button keeps the XAML free of converters.
    public bool ThemeSystem { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }
    public bool ThemeLight { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
    public bool ThemeDark { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }

    public ICommand BrowseCommand { get; }

    // After the download

    public static IReadOnlyList<Choice<string>> HashAlgorithms { get; } =
        Core.PostDownload.FileHash.Algorithms.Select(a => new Choice<string>(a, Core.PostDownload.FileHash.DisplayName(a))).ToList();

    public bool ComputeHashOnCompletion { get; set; }
    public Choice<string> HashAlgorithmChoice { get; set; }
    public bool ScanOnCompletion { get; set; }

    private string _scannerPath;

    /// <summary>The antivirus program; empty = Windows Defender.</summary>
    public string ScannerPath
    {
        get => _scannerPath;
        set
        {
            if (Set(ref _scannerPath, value ?? ""))
                OnPropertyChanged(nameof(ScannerStatus));
        }
    }

    /// <summary>Which program scans, or why it cannot.</summary>
    public string ScannerStatus
    {
        get
        {
            var scanner = Core.PostDownload.VirusScanner.ResolveScanner(ScannerPath);
            return Core.PostDownload.VirusScanner.Check(scanner) ?? string.Format(CultureInfo.CurrentCulture, Strings.SettingsScannerFound, scanner);
        }
    }

    public string ScannerArguments { get; set; }
    public string ScanThreatCodes { get; set; }
    public string ScanTimeout { get; set; }
    public ICommand BrowseScannerCommand { get; }
    public ICommand ResetScannerCommand { get; }

    // Updates

    private bool _checkingUpdates;

    public bool UpdateCheckEnabled { get; set; }
    public string UpdateUrl { get; set; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand RestorePreviousCommand { get; }
    public string CurrentVersionText => string.Format(CultureInfo.CurrentCulture, Strings.SettingsUpdateCurrent, App.AppVersion);

    // Network

    public static IReadOnlyList<Choice<ProxyType>> ProxyTypes { get; } =
    [
        new(ProxyType.Http, "HTTP"),
        new(ProxyType.Https, "HTTPS"),
        new(ProxyType.Socks5, "SOCKS5"),
    ];

    public bool ProxyNone { get => _proxyMode == ProxyMode.None; set { if (value) SetProxyMode(ProxyMode.None); } }
    public bool ProxySystem { get => _proxyMode == ProxyMode.System; set { if (value) SetProxyMode(ProxyMode.System); } }
    public bool ProxyManual { get => _proxyMode == ProxyMode.Manual; set { if (value) SetProxyMode(ProxyMode.Manual); } }
    public bool ProxyPac { get => _proxyMode == ProxyMode.Pac; set { if (value) SetProxyMode(ProxyMode.Pac); } }

    private void SetProxyMode(ProxyMode mode)
    {
        _proxyMode = mode;
        OnPropertyChanged(nameof(ProxyNone));
        OnPropertyChanged(nameof(ProxySystem));
        OnPropertyChanged(nameof(ProxyManual));
        OnPropertyChanged(nameof(ProxyPac));
    }

    public Choice<ProxyType> ProxyTypeChoice { get; set; }
    public string ProxyHost { get; set; }
    public string ProxyPort { get; set; }
    public string ProxyUser { get; set; }

    /// <summary>Set from the password box; never bound, never shown.</summary>
    public string ProxyPassword { get; set; }

    public string ProxyBypass { get; set; }
    public string PacUrl { get; set; }
    public string TimeoutSeconds { get; set; }
    public string MaxPerServer { get; set; }
    public string UserAgent { get; set; }
    public bool FtpActiveMode { get; set; }

    /// <summary>The address "Test" downloads through the proxy being configured.</summary>
    public string TestUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";

    public string TestResult
    {
        get => _testResult;
        private set => Set(ref _testResult, value);
    }

    public ICommand TestProxyCommand { get; }

    // Logins

    public ObservableCollection<SiteCredential> Logins { get; } = [];

    public SiteCredential? SelectedLogin
    {
        get => _selectedLogin;
        set
        {
            Set(ref _selectedLogin, value);
            LoginHost = value is null ? "" : $"{value.Scheme}://{value.Host}{(value.Port is { } port ? $":{port}" : "")}";
            LoginUser = value?.Username ?? "";
            LoginWindowsAccount = value?.UseWindowsAccount ?? false;
            LoginPassword = "";
            LoginMessage = null;
        }
    }

    public string LoginHost { get => _loginHost; set => Set(ref _loginHost, value); }
    public string LoginUser { get => _loginUser; set => Set(ref _loginUser, value); }
    public bool LoginWindowsAccount { get => _loginWindows; set => Set(ref _loginWindows, value); }

    /// <summary>Set from the password box. Empty while editing a saved login keeps its password.</summary>
    public string LoginPassword { get; set; } = "";

    public string? LoginMessage { get => _loginMessage; private set => Set(ref _loginMessage, value); }

    public ICommand NewLoginCommand { get; }
    public ICommand SaveLoginCommand { get; }
    public ICommand DeleteLoginCommand { get; }

    // Certificates

    public ObservableCollection<CertificateEntry> Certificates { get; } = [];

    public CertificateEntry? SelectedCertificate { get => _selectedCertificate; set => Set(ref _selectedCertificate, value); }

    public ICommand ForgetCertificateCommand { get; }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public AppSettings? Result { get; private set; }

    /// <summary>Validates the fields; on success <see cref="Result"/> holds the new settings.</summary>
    public bool TrySave()
    {
        if (!int.TryParse(Connections, out var connections) || connections is < 1 or > 32)
            Error = Strings.ErrorConnectionsRange;
        else if (!int.TryParse(Concurrent, out var concurrent) || concurrent is < 1 or > 50)
            Error = Strings.ErrorConcurrentRange;
        else if (!long.TryParse(GlobalQuota, out var quota) || quota < 0)
            Error = Strings.ErrorQueueQuota;
        else if (!long.TryParse(SpeedLimit, out var limit) || limit < 0)
            Error = Strings.ErrorSpeedLimit;
        else if (!Path.IsPathFullyQualified(Folder.Trim()))
            Error = Strings.ErrorFolderNotAbsolute;
        else if (BuildNetwork(out var network) is { } networkError)
            Error = networkError;
        else if (!int.TryParse(ScanTimeout, out var scanTimeout) || scanTimeout is < 10 or > 3600)
            Error = Strings.ErrorScanTimeout;
        else if (ScanThreatCodes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is not { Length: > 0 } codes
                 || !codes.All(c => int.TryParse(c, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) && code != 0))
            Error = Strings.ErrorScanCodes;
        else if (ScanOnCompletion && Core.PostDownload.VirusScanner.Check(Core.PostDownload.VirusScanner.ResolveScanner(ScannerPath)) is { } scannerError)
            Error = scannerError;
        else if (UpdateUrl.Trim().Length > 0 && Core.Updates.UpdateChecker.Allowed(UpdateUrl.Trim()) is null)
            Error = Strings.UpdateErrorAddress;
        else if (UpdateCheckEnabled && UpdateUrl.Trim().Length == 0)
            Error = Strings.ErrorUpdateUrlMissing;
        else
        {
            var result = network;
            result.MaxConnections = connections;
            result.MaxConcurrentDownloads = concurrent;
            result.SpeedLimitKBps = limit;
            result.DownloadFolder = Folder.Trim();
            result.ConfirmBrowserDownloads = ConfirmBrowserDownloads;
            result.FfmpegPath = FfmpegPath.Trim();
            result.NotifyOnCompletion = NotifyOnCompletion;
            result.Theme = Theme;
            result.CloseToTray = CloseToTray;
            result.GlobalQuotaMBPerHour = quota;
            result.ComputeHashOnCompletion = ComputeHashOnCompletion;
            result.HashAlgorithm = HashAlgorithmChoice.Value;
            result.ScanOnCompletion = ScanOnCompletion;
            result.ScannerPath = ScannerPath.Trim();
            result.ScannerArguments = string.IsNullOrWhiteSpace(ScannerArguments) ? Core.PostDownload.VirusScanner.DefaultArguments : ScannerArguments.Trim();
            result.ScanThreatCodes = string.Join(",", codes);
            result.ScanTimeoutSeconds = scanTimeout;
            result.UpdateCheckEnabled = UpdateCheckEnabled;
            result.UpdateUrl = UpdateUrl.Trim();
            Result = result;
            return true;
        }
        return false;
    }

    /// <summary>The settings with the network fields as typed; returns the error to show, or null.</summary>
    private string? BuildNetwork(out AppSettings settings)
    {
        settings = _original.Clone();
        settings.ProxyMode = _proxyMode;
        settings.ProxyType = ProxyTypeChoice.Value;
        settings.ProxyHost = ProxyHost.Trim();
        settings.ProxyUser = ProxyUser.Trim();
        settings.ProxyPassword = ProxyPassword;
        settings.ProxyBypass = ProxyBypass.Trim();
        settings.PacUrl = PacUrl.Trim();
        settings.UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? AppSettings.DefaultUserAgent : UserAgent.Trim();
        settings.FtpActiveMode = FtpActiveMode;
        if (!int.TryParse(ProxyPort, out var port) || port is < 1 or > 65535)
        {
            if (_proxyMode == ProxyMode.Manual)
                return Strings.ErrorProxyPort;
            port = _original.ProxyPort;
        }
        settings.ProxyPort = port;
        if (_proxyMode == ProxyMode.Manual && settings.ProxyHost.Length == 0)
            return Strings.ErrorProxyHost;
        if (_proxyMode == ProxyMode.Pac && settings.PacUrl.Length == 0)
            return Strings.ErrorPacUrl;
        if (!int.TryParse(TimeoutSeconds, out var timeout) || timeout is < 5 or > 600)
            return Strings.ErrorTimeout;
        if (!int.TryParse(MaxPerServer, out var perServer) || perServer is < 1 or > 256)
            return Strings.ErrorPerServer;
        settings.TimeoutSeconds = timeout;
        settings.MaxConnectionsPerServer = perServer;
        return null;
    }

    /// <summary>Fetches the test address with the network as typed (not yet saved) and reports which route it took.</summary>
    private async void TestProxy()
    {
        if (BuildNetwork(out var settings) is { } error)
        {
            TestResult = error;
            return;
        }
        if (!Urls.TryParseHttp(TestUrl, out var target))
        {
            TestResult = Strings.ErrorInvalidUrl;
            return;
        }
        _testing = true;
        TestResult = Strings.ProxyTesting;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var network = new NetworkContext(settings, _credentials, _certificates);
            await network.Proxy.LoadPacAsync();
            var route = network.Proxy.Resolve(target) is { } proxy
                ? string.Format(CultureInfo.CurrentCulture, Strings.ProxyRouteVia, $"{proxy.Scheme}://{proxy.Host}:{proxy.Port}")
                : Strings.ProxyRouteDirect;
            if (network.Proxy.PacError is { } pacError)
                route += " " + string.Format(CultureInfo.CurrentCulture, Strings.ProxyPacFailed, pacError);
            using var http = network.CreateHttpClient();
            using var timeout = new CancellationTokenSource(network.Timeout);
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            TestResult = response.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired
                ? string.Format(CultureInfo.CurrentCulture, Strings.ProxyTestNeedsLogin, route)
                : string.Format(CultureInfo.CurrentCulture, Strings.ProxyTestOk, (int)response.StatusCode, watch.ElapsedMilliseconds, route);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            TestResult = string.Format(CultureInfo.CurrentCulture, Strings.ProxyTestFailed, ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            _testing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void SaveLogin()
    {
        var text = LoginHost.Trim();
        // "https://host:port", or a bare "host:port", which is https: a login is valid for one scheme only.
        var scheme = "https";
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator > 0)
        {
            scheme = text[..separator].ToLowerInvariant();
            text = text[(separator + 3)..].TrimEnd('/');
            if (!SiteCredential.Schemes.Contains(scheme))
            {
                LoginMessage = Strings.LoginSchemeUnknown;
                return;
            }
        }
        int? port = null;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsed) && parsed is > 0 and < 65536)
        {
            port = parsed;
            text = text[..colon];
        }
        var editing = SelectedLogin;
        if (editing is null && LoginPassword.Length == 0 && !LoginWindowsAccount)
        {
            LoginMessage = Strings.LoginPasswordRequired;
            return;
        }
        // A password that would travel readable (http with Basic, ftp) is saved only after an explicit yes.
        var plain = SiteCredential.IsPlainScheme(scheme) && !LoginWindowsAccount;
        if (plain && !(editing is { PlainConfirmed: true } && editing.Scheme == scheme)
            && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.LoginPlainSaveConfirm, scheme), Strings.SettingsTabCredentials))
            return;
        var credential = new SiteCredential
        {
            Id = editing?.Id ?? 0,
            Scheme = scheme,
            PlainConfirmed = plain,
            Host = text,
            Port = port,
            Username = LoginUser.Trim(),
            // An empty box while editing keeps the saved password.
            Password = LoginPassword.Length > 0 ? LoginPassword : editing?.Password ?? "",
            UseWindowsAccount = LoginWindowsAccount,
        };
        var saved = _credentials.Save(credential, remember: true);
        ReloadLogins();
        SelectedLogin = Logins.FirstOrDefault(l => l.Id == saved.Id);
        LoginMessage = string.Format(CultureInfo.CurrentCulture, Strings.LoginSaved, saved.Host);
    }

    private void DeleteLogin()
    {
        if (SelectedLogin is not { } login)
            return;
        if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteLogin, login), Strings.SettingsTabCredentials))
            return;
        _credentials.Delete(login.Id);
        ReloadLogins();
        SelectedLogin = null;
    }

    private void ForgetCertificate()
    {
        if (SelectedCertificate is not { } entry)
            return;
        _certificates.Forget(entry.Certificate);
        ReloadCertificates();
    }

    private void ReloadLogins()
    {
        Logins.Clear();
        foreach (var login in _credentials.Saved)
            Logins.Add(login);
    }

    private void ReloadCertificates()
    {
        Certificates.Clear();
        foreach (var c in _certificates.Trusted)
            Certificates.Add(new CertificateEntry(c, $"{c.Host}:{c.Port} — {c.Subject} — {CertificateText.Fingerprint(c.Fingerprint)[..23]}…"));
    }
}

/// <summary>Formatting shared by the certificate dialog and the settings.</summary>
public static class CertificateText
{
    /// <summary>"AB:CD:EF…": the usual way to read a fingerprint aloud or compare it.</summary>
    public static string Fingerprint(string hex) => string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
