using System.Globalization;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.Core.Resources;
using DownloadManager.Core.Updates;

namespace DownloadManager.App.ViewModels;

/// <summary>
/// A newer version: its notes, then, only when the user confirms, the download, the hash check and the install
/// (which closes the app). Closing the window cancels a download in progress.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly Func<IProgress<double>, CancellationToken, Task> _install;
    private readonly CancellationTokenSource _cancel = new();
    private bool _busy;
    private double _progress;
    private string? _status;

    public UpdateViewModel(UpdateInfo update, string currentVersion, Func<IProgress<double>, CancellationToken, Task> install)
    {
        _install = install;
        Title = string.Format(CultureInfo.CurrentCulture, Strings.UpdateAvailable, update.Version.ToString(3), currentVersion);
        Notes = update.Notes.Trim().Length > 0 ? update.Notes.Trim() : Strings.UpdateNoNotes;
        InstallCommand = new RelayCommand(Install, () => !_busy);
    }

    public string Title { get; }
    public string Notes { get; }
    public ICommand InstallCommand { get; }
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Status { get => _status; private set => Set(ref _status, value); }

    public void Cancel() => _cancel.Cancel();

    private async void Install()
    {
        IsBusy = true;
        Status = Strings.UpdateDownloading;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _install(new Progress<double>(p => Progress = p * 100), _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Status = null;
        }
        catch (Exception ex) when (ex is UpdateException or System.Net.Http.HttpRequestException or System.IO.IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            Status = string.Format(CultureInfo.CurrentCulture, Strings.UpdateFailed, ex.Message);
            Core.Log.Error("update", "install failed", ex);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
