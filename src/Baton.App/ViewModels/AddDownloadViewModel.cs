using System.Globalization;
using System.IO;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Resources;
using Baton.Core.Rules;

namespace Baton.App.ViewModels;

/// <summary>
/// The add dialog. The category follows the address until the user picks one, and the folder follows the
/// category until the user changes it, so in the common case everything is chosen automatically.
/// </summary>
public sealed class AddDownloadViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private string _url;
    private string _folder = "";
    private string _fileName;
    private Category _category;
    private bool _categoryChosen;
    private bool _folderChosen;
    private bool _updatingFolder;
    private bool _subfolderChoice;
    private string? _error;
    private readonly Func<string, string?, RulePreview>? _ruleLookup;
    private readonly DownloadQueue? _defaultQueue;
    private DownloadQueue? _queue;
    private bool _queueChosen;
    private RulePreview? _rule;

    /// <param name="ruleLookup">What the rules would do with an address and file name (the origin and size are the caller's); null: no rules.</param>
    public AddDownloadViewModel(IDialogService dialogs, IReadOnlyList<Category> categories, AppSettings settings,
        string? url, string? fileName = null, string? note = null, IReadOnlyList<DownloadQueue>? queues = null, long? queueId = null,
        Func<string, string?, RulePreview>? ruleLookup = null)
    {
        Queues = queues ?? [];
        _defaultQueue = _queue = Queues.FirstOrDefault(q => q.Id == queueId) ?? Queues.FirstOrDefault(q => q.IsMain) ?? Queues.FirstOrDefault();
        _ruleLookup = ruleLookup;
        _dialogs = dialogs;
        _settings = settings;
        _subfolderChoice = settings.CategorySubfolders;
        Categories = categories;
        _url = url ?? "";
        _fileName = fileName ?? "";
        Note = note;
        _rule = LookUpRule();
        _category = Guess();
        _queue = RuleQueue() ?? _defaultQueue;
        SetFolderFromCategory();
        Validate();
        BrowseCommand = new RelayCommand(Browse);
        EditCategoryFolderCommand = new RelayCommand(EditCategoryFolder, () => EditCategory is not null);
    }

    public IReadOnlyList<Category> Categories { get; }

    public IReadOnlyList<DownloadQueue> Queues { get; }

    /// <summary>
    /// The queue the download joins: "Download now" starts it at once, "Add to queue" waits for the queue. The one preselected
    /// (the queue being viewed, or the main one) is not a choice: only a different one picked here stands over a rule.
    /// </summary>
    public DownloadQueue? Queue
    {
        get => _queue;
        set
        {
            if (value is null || !Set(ref _queue, value))
                return;
            _queueChosen = true;
        }
    }

    /// <summary>"Rule: name" when a rule applies to this address and the user has not overridden what it sets; null otherwise.</summary>
    public string? RuleText => _rule?.Rule is { } rule ? string.Format(CultureInfo.CurrentCulture, Strings.AddRuleApplied, rule.Name) : null;

    public bool HasRule => RuleText is not null;

    public string? Note { get; }

    /// <summary>The address comes with data the dialog cannot show (a form's fields, a stream's chosen quality).</summary>
    public bool UrlReadOnly { get; init; }

    public ICommand BrowseCommand { get; }

    /// <summary>Set by the main window: opens the category editor on this category, returns its folder once saved (null if cancelled).</summary>
    public Func<Category, string?>? EditCategory { get; init; }

    public ICommand EditCategoryFolderCommand { get; }

    /// <summary>
    /// "Folder of the Video category" when the folder comes from a category with an absolute folder of its own instead of
    /// the default folder, so that choice is never silent; null otherwise.
    /// </summary>
    public string? FolderSource => !_folderChosen && Category.OverridesDefault(_settings.DownloadFolder)
        ? string.Format(CultureInfo.CurrentCulture, Strings.AddFolderFromCategory, Category.DisplayName)
        : null;

    public bool HasFolderSource => FolderSource is not null;

    /// <summary>The main folder or the type's subfolder, for this download only; offered when the type has a relative folder.</summary>
    public bool CanChooseSubfolder => Category.Folder.Length > 0 && !Path.IsPathFullyQualified(Category.Folder) && !Category.RememberLast;

    public bool UseSubfolder
    {
        get => _subfolderChoice;
        set
        {
            if (!Set(ref _subfolderChoice, value))
                return;
            OnPropertyChanged(nameof(UseMainFolder));
            if (!_folderChosen)
                SetFolderFromCategory();
        }
    }

    public bool UseMainFolder { get => !_subfolderChoice; set => UseSubfolder = !value; }

    /// <summary>The user typed or picked a folder: it is the one to remember for the category.</summary>
    public bool FolderChosen => _folderChosen;

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
            OnPropertyChanged(nameof(CanChooseSubfolder));
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
            OnPropertyChanged(nameof(FolderSource));
            OnPropertyChanged(nameof(HasFolderSource));
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
        _folderChosen || _subfolderChoice != _settings.CategorySubfolders ? Folder.Trim() : null, // the per-download choice is explicit too
        string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim(),
        _categoryChosen ? Category.Id : null,
        _queueChosen || RuleQueue() is null ? Queue?.Id : null); // the preselected queue is not explicit: a rule's queue (shown here) is left to the rule

    private Category Guess() =>
        (_rule?.Rule?.Actions.CategoryId is { } ruleCategory ? Categories.FirstOrDefault(c => c.Id == ruleCategory) : null)
        ?? Core.Categories.MatchUrl(Categories, Url.Trim(), string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim());

    private RulePreview? LookUpRule() =>
        _ruleLookup is null || !Urls.TryParseDownload(Url.Trim(), out _) ? null
            : _ruleLookup(Url.Trim(), string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim());

    private DownloadQueue? RuleQueue() =>
        _rule?.Rule?.Actions.QueueId is { } id ? Queues.FirstOrDefault(q => q.Id == id) : null;

    /// <summary>The address or name changed: the rule that applies may be another one, and with it the queue and folder shown.</summary>
    private void RefreshRule()
    {
        _rule = LookUpRule();
        OnPropertyChanged(nameof(RuleText));
        OnPropertyChanged(nameof(HasRule));
        if (!_queueChosen)
            Set(ref _queue, RuleQueue() ?? _defaultQueue, nameof(Queue));
    }

    private void FollowGuess()
    {
        RefreshRule();
        if (!_categoryChosen && Set(ref _category, Guess(), nameof(Category)))
            OnPropertyChanged(nameof(CanChooseSubfolder));
        if (!_folderChosen)
            SetFolderFromCategory(); // also when only the rule changed: its folder is shown
    }

    private void SetFolderFromCategory()
    {
        _updatingFolder = true;
        Folder = _rule?.Folder is { Length: > 0 } ruleFolder ? ruleFolder : Category.ProposedFolder(_settings, _subfolderChoice);
        _updatingFolder = false;
        OnPropertyChanged(nameof(FolderSource));
        OnPropertyChanged(nameof(HasFolderSource));
    }

    private void EditCategoryFolder()
    {
        if (EditCategory?.Invoke(Category) is not { } folder)
            return;
        Category.Folder = folder; // the dialog's own copy, so the folder shown follows the saved one
        if (!_folderChosen)
            SetFolderFromCategory();
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
