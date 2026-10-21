using System.Globalization;
using System.IO;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>
/// The add dialog. The category follows the address until the user picks one, and the folder follows the
/// category until the user changes it, so in the common case everything is chosen automatically.
/// </summary>
public sealed class AddDownloadViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly string _defaultFolder;
    private string _url;
    private string _folder = "";
    private string _fileName;
    private Category _category;
    private bool _categoryChosen;
    private bool _folderChosen;
    private bool _updatingFolder;
    private string? _error;

    public AddDownloadViewModel(IDialogService dialogs, IReadOnlyList<Category> categories, string defaultFolder,
        string? url, string? fileName = null, string? note = null, IReadOnlyList<DownloadQueue>? queues = null, long? queueId = null)
    {
        Queues = queues ?? [];
        Queue = Queues.FirstOrDefault(q => q.Id == queueId) ?? Queues.FirstOrDefault(q => q.IsMain) ?? Queues.FirstOrDefault();
        _dialogs = dialogs;
        _defaultFolder = defaultFolder;
        Categories = categories;
        _url = url ?? "";
        _fileName = fileName ?? "";
        Note = note;
        _category = Guess();
        SetFolderFromCategory();
        Validate();
        BrowseCommand = new RelayCommand(Browse);
    }

    public IReadOnlyList<Category> Categories { get; }

    public IReadOnlyList<DownloadQueue> Queues { get; }

    /// <summary>The queue the download joins: "Download now" starts it at once, "Add to queue" waits for the queue.</summary>
    public DownloadQueue? Queue { get; set; }

    public string? Note { get; }

    /// <summary>The address comes with data the dialog cannot show (a form's fields, a stream's chosen quality).</summary>
    public bool UrlReadOnly { get; init; }

    public ICommand BrowseCommand { get; }

    public string Url
    {
        get => _url;
        set
        {
            if (!Set(ref _url, value))
                return;
            FollowGuess();
            Validate();
        }
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (Set(ref _fileName, value))
                FollowGuess();
        }
    }

    public Category Category
    {
        get => _category;
        set
        {
            if (value is null || !Set(ref _category, value))
                return;
            _categoryChosen = true;
            if (!_folderChosen)
                SetFolderFromCategory();
        }
    }

    public string Folder
    {
        get => _folder;
        set
        {
            if (!Set(ref _folder, value))
                return;
            if (!_updatingFolder)
                _folderChosen = true;
            Validate();
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool CanSubmit { get; private set; }

    private string _expectedHash = "";

    /// <summary>Optional: the MD5, SHA-1 or SHA-256 the file must have, checked when it is finished.</summary>
    public string ExpectedHash
    {
        get => _expectedHash;
        set
        {
            if (!Set(ref _expectedHash, value))
                return;
            OnPropertyChanged(nameof(HashHint));
            Validate();
        }
    }

    /// <summary>Which algorithm the pasted hash is, so the user sees it was understood.</summary>
    public string HashHint => ExpectedHash.Trim().Length == 0 ? Strings.AddHashHint
        : Core.PostDownload.FileHash.TryParse(ExpectedHash, out var algorithm, out _)
            ? string.Format(CultureInfo.CurrentCulture, Strings.AddHashRecognized, Core.PostDownload.FileHash.DisplayName(algorithm))
            : "";

    /// <summary>The expected hash as the service takes it, or null.</summary>
    public string? ExpectedHashOrNull => ExpectedHash.Trim().Length == 0 ? null : ExpectedHash.Trim();

    /// <summary>Set by the dialog: start right away (true) or add to the queue (false).</summary>
    public bool StartNow { get; set; }

    /// <summary>The download as the service should receive it: unchanged automatic choices stay automatic.</summary>
    public (string Url, string? Folder, string? FileName, long? CategoryId, long? QueueId) ToRequest() => (
        Url.Trim(),
        _folderChosen ? Folder.Trim() : null,
        string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim(),
        _categoryChosen ? Category.Id : null,
        Queue?.Id);

    private Category Guess() =>
        Core.Categories.MatchUrl(Categories, Url.Trim(), string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim());

    private void FollowGuess()
    {
        if (_categoryChosen)
            return;
        var guess = Guess();
        if (Set(ref _category, guess, nameof(Category)) && !_folderChosen)
            SetFolderFromCategory();
    }

    private void SetFolderFromCategory()
    {
        _updatingFolder = true;
        Folder = Category.ResolveFolder(_defaultFolder);
        _updatingFolder = false;
    }

    private void Browse()
    {
        if (_dialogs.PickFolder(Strings.PickDestinationFolder, Directory.Exists(Folder) ? Folder : null) is { } picked)
            Folder = picked;
    }

    private void Validate()
    {
        string? error = null;
        if (Url.Trim().Length > 0 && !Urls.TryParseDownload(Url, out _))
            error = Strings.ErrorInvalidUrl;
        else if (Folder.Trim().Length == 0 || !Path.IsPathFullyQualified(Folder.Trim()))
            error = Strings.ErrorFolderNotAbsolute;
        else if (ExpectedHash.Trim().Length > 0 && !Core.PostDownload.FileHash.TryParse(ExpectedHash, out _, out _))
            error = Strings.ErrorHashFormat;
        Error = error;
        CanSubmit = error is null && Url.Trim().Length > 0;
        OnPropertyChanged(nameof(CanSubmit));
    }
}
