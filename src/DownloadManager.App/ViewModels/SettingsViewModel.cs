using System.Globalization;
using System.IO;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _original;
    private string? _error;

    public SettingsViewModel(IDialogService dialogs, AppSettings current)
    {
        _dialogs = dialogs;
        _original = current.Clone();
        Connections = current.MaxConnections.ToString(CultureInfo.CurrentCulture);
        Concurrent = current.MaxConcurrentDownloads.ToString(CultureInfo.CurrentCulture);
        SpeedLimit = current.SpeedLimitKBps.ToString(CultureInfo.CurrentCulture);
        Folder = current.DownloadFolder;
        ConfirmBrowserDownloads = current.ConfirmBrowserDownloads;
        NotifyOnCompletion = current.NotifyOnCompletion;
        Theme = current.Theme;
        BrowseCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFolder(Strings.SettingsFolder, Directory.Exists(Folder) ? Folder : null) is { } picked)
            {
                Folder = picked;
                OnPropertyChanged(nameof(Folder));
            }
        });
    }

    public string Connections { get; set; }
    public string Concurrent { get; set; }
    public string SpeedLimit { get; set; }
    public string Folder { get; set; }
    public bool ConfirmBrowserDownloads { get; set; }
    public bool NotifyOnCompletion { get; set; }
    public AppTheme Theme { get; set; }

    // One property per radio button keeps the XAML free of converters.
    public bool ThemeSystem { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }
    public bool ThemeLight { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
    public bool ThemeDark { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }

    public ICommand BrowseCommand { get; }

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
        else if (!int.TryParse(Concurrent, out var concurrent) || concurrent is < 1 or > 20)
            Error = Strings.ErrorConcurrentRange;
        else if (!long.TryParse(SpeedLimit, out var limit) || limit < 0)
            Error = Strings.ErrorSpeedLimit;
        else if (!Path.IsPathFullyQualified(Folder.Trim()))
            Error = Strings.ErrorFolderNotAbsolute;
        else
        {
            var result = _original.Clone();
            result.MaxConnections = connections;
            result.MaxConcurrentDownloads = concurrent;
            result.SpeedLimitKBps = limit;
            result.DownloadFolder = Folder.Trim();
            result.ConfirmBrowserDownloads = ConfirmBrowserDownloads;
            result.NotifyOnCompletion = NotifyOnCompletion;
            result.Theme = Theme;
            Result = result;
            return true;
        }
        return false;
    }
}
