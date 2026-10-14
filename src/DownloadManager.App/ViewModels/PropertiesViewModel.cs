using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>
/// Details of one download, its category, and "Update address": a new link for the same file (for example
/// after the old one expired) keeps the data already downloaded when the server proves it is the same file.
/// </summary>
public sealed class PropertiesViewModel : ObservableObject
{
    private readonly DownloadService _service;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private DownloadSnapshot _snapshot;
    private string _url;
    private Category? _category;
    private bool _busy;
    private string? _message;

    public PropertiesViewModel(DownloadSnapshot snapshot, DownloadService service, IDialogService dialogs, IShellService shell)
    {
        _snapshot = snapshot;
        _service = service;
        _dialogs = dialogs;
        _shell = shell;
        _url = snapshot.Url;
        Categories = service.Categories;
        _category = Categories.FirstOrDefault(c => c.Id == snapshot.CategoryId);
        UpdateUrlCommand = new RelayCommand(UpdateUrl, () => CanUpdateUrl);
        OpenFolderCommand = new RelayCommand(() => _shell.Reveal(_snapshot.FinalPath, _snapshot.Directory));
    }

    public IReadOnlyList<Category> Categories { get; }

    public ICommand UpdateUrlCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public string Name => _snapshot.DisplayName;
    public string Folder => _snapshot.Directory;
    public string StatusText => _snapshot.Status switch
    {
        DownloadStatus.Queued => Strings.StatusQueued,
        DownloadStatus.Downloading => Strings.StatusDownloading,
        DownloadStatus.Paused => Strings.StatusPaused,
        DownloadStatus.Completed => Strings.StatusCompleted,
        _ => _snapshot.Error is { } error ? $"{Strings.StatusFailed}: {error}" : Strings.StatusFailed,
    };
    public string SizeText => _snapshot.TotalSize is { } size ? $"{Format.Bytes(size)} ({size:N0} byte)" : Strings.Unknown;
    public string DownloadedText => Format.Bytes(_snapshot.Downloaded);
    public string ContentTypeText => _snapshot.ContentType ?? Strings.Unknown;
    public string ETagText => _snapshot.ETag ?? "—";
    public string LastModifiedText => _snapshot.LastModified is { } lm && DateTimeOffset.TryParse(lm, CultureInfo.InvariantCulture, out var date)
        ? date.LocalDateTime.ToString("G", CultureInfo.CurrentCulture)
        : "—";
    public string ResumableText => _snapshot.SupportsRanges ? Strings.Yes : Strings.ResumeNotSupported;
    public string AddedText => _snapshot.AddedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
    public string CompletedText => _snapshot.CompletedUtc?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? "—";
    public bool IsCompleted => _snapshot.Status == DownloadStatus.Completed;

    public string Url
    {
        get => _url;
        set
        {
            if (Set(ref _url, value))
                Message = null;
        }
    }

    public Category? Category
    {
        get => _category;
        set
        {
            if (value is null || !Set(ref _category, value))
                return;
            _service.SetCategory(_snapshot.Id, value.Id);
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>Outcome of the last address update, shown under the address.</summary>
    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    private bool CanUpdateUrl =>
        !IsBusy && !IsCompleted && Urls.TryParseDownload(Url, out var uri) && uri.AbsoluteUri != _snapshot.Url;

    private async void UpdateUrl()
    {
        IsBusy = true;
        Message = Strings.UrlCheckInProgress;
        try
        {
            var check = await _service.CheckNewUrlAsync(_snapshot.Id, Url);
            bool keep;
            switch (check.Match)
            {
                case UrlMatch.NothingToKeep:
                    keep = false;
                    break;
                case UrlMatch.Same:
                    keep = true;
                    break;
                case UrlMatch.Unverifiable:
                    var answer = _dialogs.Ask(string.Format(CultureInfo.CurrentCulture, Strings.AskKeepUnverified, check.Reason), Strings.UpdateUrlTitle);
                    if (answer == Answer.Cancel)
                    {
                        Message = null;
                        return;
                    }
                    keep = answer == Answer.Yes;
                    break;
                default:
                    if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.AskRestartDifferent, check.Reason), Strings.UpdateUrlTitle))
                    {
                        Message = null;
                        return;
                    }
                    keep = false;
                    break;
            }

            await _service.ChangeUrlAsync(_snapshot.Id, check, keep);
            _snapshot = _service.GetSnapshot(_snapshot.Id) ?? _snapshot;
            _url = _snapshot.Url;
            OnPropertyChanged(string.Empty);
            Message = keep
                ? string.Format(CultureInfo.CurrentCulture, Strings.UrlUpdatedKept, Format.Bytes(_snapshot.Downloaded))
                : Strings.UrlUpdatedRestart;
        }
        catch (Exception ex) when (ex is HttpRequestException or DownloadFailedException or ArgumentException or InvalidOperationException
                                       or TaskCanceledException or IOException)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.UrlCheckFailed, ex.Message);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
