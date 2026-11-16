using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Resources;
using Baton.Core.Rules;

namespace Baton.App.ViewModels;

/// <summary>An origin as a check box in a rule's conditions.</summary>
public sealed class OriginChoice(string id, string text, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Id { get; } = id;
    public string Text { get; } = text;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>One rule being edited; nothing is saved until the window is confirmed.</summary>
public sealed class RuleEditViewModel : ObservableObject
{
    private const long MB = 1024 * 1024;
    private string _name;
    private bool _enabled;
    private string _domains;
    private string _urlPattern;
    private string _urlRegex;
    private string _extensions;
    private Choice<long?> _conditionCategory;
    private string _minSize;
    private string _maxSize;
    private string _folder;
    private Choice<long?> _actionCategory;
    private Choice<long?> _queue;
    private Choice<RuleStart?> _start;
    private string _rename;
    private bool _scan;
    private string _connections;
    private string _userAgent;
    private string _speedLimit;
    private bool _skipConfirmation;
    private bool _showInFolder;
    private bool _openWhenDone;

    public RuleEditViewModel(Rule rule, IReadOnlyList<Category> categories, IReadOnlyList<DownloadQueue> queues)
    {
        Original = rule;
        var any = new Choice<long?>(null, Strings.RuleAnyCategory);
        var keep = new Choice<long?>(null, Strings.RuleKeep);
        ConditionCategories = [any, .. categories.Select(c => new Choice<long?>(c.Id, c.DisplayName))];
        ActionCategories = [keep, .. categories.Select(c => new Choice<long?>(c.Id, c.DisplayName))];
        Queues = [keep, .. queues.Select(q => new Choice<long?>(q.Id, q.DisplayName))];
        Starts =
        [
            new(null, Strings.RuleKeep), new(RuleStart.Now, Strings.RuleStartNow),
            new(RuleStart.Queued, Strings.RuleStartQueued), new(RuleStart.Paused, Strings.RuleStartPaused),
        ];
        var c = rule.Conditions;
        var a = rule.Actions;
        _name = rule.Name;
        _enabled = rule.Enabled;
        _domains = string.Join(' ', c.Domains);
        _urlPattern = c.UrlPattern ?? "";
        _urlRegex = c.UrlRegex ?? "";
        _extensions = string.Join(' ', c.Extensions);
        _conditionCategory = ConditionCategories.FirstOrDefault(x => x.Value == c.CategoryId) ?? any;
        _minSize = c.MinSize is { } min ? (min / (double)MB).ToString("0.##", CultureInfo.CurrentCulture) : "";
        _maxSize = c.MaxSize is { } max ? (max / (double)MB).ToString("0.##", CultureInfo.CurrentCulture) : "";
        Origins =
        [
            new(RuleOrigins.Browser, Strings.RuleOriginBrowser, c.Origins.Contains(RuleOrigins.Browser)),
            new(RuleOrigins.Dialog, Strings.RuleOriginDialog, c.Origins.Contains(RuleOrigins.Dialog)),
            new(RuleOrigins.Batch, Strings.RuleOriginBatch, c.Origins.Contains(RuleOrigins.Batch)),
            new(RuleOrigins.Clipboard, Strings.RuleOriginClipboard, c.Origins.Contains(RuleOrigins.Clipboard)),
            new(RuleOrigins.Cli, Strings.RuleOriginCli, c.Origins.Contains(RuleOrigins.Cli)),
            new(RuleOrigins.Api, Strings.RuleOriginApi, c.Origins.Contains(RuleOrigins.Api)),
            new(RuleOrigins.Mcp, Strings.RuleOriginMcp, c.Origins.Contains(RuleOrigins.Mcp)),
        ];
        _folder = a.Folder ?? "";
        _actionCategory = ActionCategories.FirstOrDefault(x => x.Value == a.CategoryId) ?? keep;
        _queue = Queues.FirstOrDefault(x => x.Value == a.QueueId) ?? keep;
        _start = Starts.First(x => x.Value == a.Start);
        _rename = a.Rename ?? "";
        _scan = a.Scan;
        _connections = a.Connections?.ToString(CultureInfo.CurrentCulture) ?? "";
        _userAgent = a.UserAgent ?? "";
        _speedLimit = a.SpeedLimitKBps?.ToString(CultureInfo.CurrentCulture) ?? "";
        _skipConfirmation = a.SkipConfirmation;
        _showInFolder = a.ShowInFolder;
        _openWhenDone = a.OpenWhenDone;
        foreach (var origin in Origins)
            origin.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Origins));
    }

    public Rule Original { get; }
    public bool IsImported => Original.Imported;

    /// <summary>This version cannot read the stored rule: it is shown off, cannot be edited, and is saved as it is.</summary>
    public bool IsUnreadable => Original.Unreadable;
    public bool IsEditable => !Original.Unreadable;

    /// <summary>The name in the list; an unreadable rule says so.</summary>
    public string ListLabel => Original.Unreadable ? Name + " — " + Strings.RuleUnreadable : Name;

    public IReadOnlyList<Choice<long?>> ConditionCategories { get; }
    public IReadOnlyList<Choice<long?>> ActionCategories { get; }
    public IReadOnlyList<Choice<long?>> Queues { get; }
    public IReadOnlyList<Choice<RuleStart?>> Starts { get; }
    public IReadOnlyList<OriginChoice> Origins { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(ListLabel));
        }
    }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Domains { get => _domains; set => Set(ref _domains, value); }
    public string UrlPattern { get => _urlPattern; set => Set(ref _urlPattern, value); }
    public string UrlRegex { get => _urlRegex; set => Set(ref _urlRegex, value); }
    public string Extensions { get => _extensions; set => Set(ref _extensions, value); }
    public Choice<long?> ConditionCategory { get => _conditionCategory; set => Set(ref _conditionCategory, value); }
    public string MinSize { get => _minSize; set => Set(ref _minSize, value); }
    public string MaxSize { get => _maxSize; set => Set(ref _maxSize, value); }
    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public Choice<long?> ActionCategory { get => _actionCategory; set => Set(ref _actionCategory, value); }
    public Choice<long?> Queue { get => _queue; set => Set(ref _queue, value); }
    public Choice<RuleStart?> Start { get => _start; set => Set(ref _start, value); }
    public string Rename { get => _rename; set => Set(ref _rename, value); }
    public bool Scan { get => _scan; set => Set(ref _scan, value); }
    public string Connections { get => _connections; set => Set(ref _connections, value); }
    public string UserAgent { get => _userAgent; set => Set(ref _userAgent, value); }
    public string SpeedLimit { get => _speedLimit; set => Set(ref _speedLimit, value); }
    public bool SkipConfirmation { get => _skipConfirmation; set => Set(ref _skipConfirmation, value); }
    public bool ShowInFolder { get => _showInFolder; set => Set(ref _showInFolder, value); }
    public bool OpenWhenDone { get => _openWhenDone; set => Set(ref _openWhenDone, value); }

    private static List<string> Words(string text) =>
        text.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool TryNumber(string text, out double value)
    {
        value = 0;
        return string.IsNullOrWhiteSpace(text) || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && value >= 0;
    }

    /// <summary>The first problem that stops this rule from being saved, or null.</summary>
    public string? Validate()
    {
        if (Original.Unreadable)
            return null; // nothing of it is edited or saved
        var name = Name.Trim();
        if (name.Length == 0)
            return Strings.RuleErrorName;
        if (!TryNumber(MinSize, out _) || !TryNumber(MaxSize, out _) || !TryNumber(Connections, out _) || !TryNumber(SpeedLimit, out _))
            return string.Format(CultureInfo.CurrentCulture, Strings.RuleErrorNumber, name);
        if (!string.IsNullOrWhiteSpace(UrlRegex) && RuleEngine.ValidateRegex(UrlRegex.Trim()) is { } regexError)
            return string.Format(CultureInfo.CurrentCulture, Strings.RuleErrorRegex, name, regexError);
        if (!string.IsNullOrWhiteSpace(Folder) && !Path.IsPathFullyQualified(Folder.Trim()))
            return string.Format(CultureInfo.CurrentCulture, Strings.RuleErrorFolder, name);
        if (Conditions().IsEmpty)
            return string.Format(CultureInfo.CurrentCulture, Strings.RuleErrorNoCondition, name);
        return null;
    }

    private RuleConditions Conditions()
    {
        double.TryParse(MinSize.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var min);
        double.TryParse(MaxSize.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var max);
        return new RuleConditions
        {
            Domains = Words(Domains),
            UrlPattern = string.IsNullOrWhiteSpace(UrlPattern) ? null : UrlPattern.Trim(),
            UrlRegex = string.IsNullOrWhiteSpace(UrlRegex) ? null : UrlRegex.Trim(),
            Extensions = Words(Extensions).Select(e => e.TrimStart('.')).Where(e => e.Length > 0).ToList(),
            CategoryId = ConditionCategory.Value,
            MinSize = string.IsNullOrWhiteSpace(MinSize) ? null : (long)(min * MB),
            MaxSize = string.IsNullOrWhiteSpace(MaxSize) ? null : (long)(max * MB),
            Origins = Origins.Where(o => o.IsChecked).Select(o => o.Id).ToList(),
        };
    }

    /// <summary>The rule as edited (call <see cref="Validate"/> first).</summary>
    public Rule ToRule()
    {
        if (Original.Unreadable)
            return Original.Clone();
        int.TryParse(Connections.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var connections);
        long.TryParse(SpeedLimit.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var limit);
        var rule = Original.Clone();
        rule.Name = Name.Trim();
        rule.Enabled = Enabled;
        rule.Conditions = Conditions();
        rule.Actions = new RuleActions
        {
            Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
            CategoryId = ActionCategory.Value,
            QueueId = Queue.Value,
            Start = Start.Value,
            Rename = string.IsNullOrWhiteSpace(Rename) ? null : Rename.Trim(),
            Scan = Scan,
            Connections = connections > 0 ? Math.Clamp(connections, 1, 32) : null,
            UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim(),
            SpeedLimitKBps = limit > 0 ? limit : null,
            SkipConfirmation = SkipConfirmation,
            ShowInFolder = ShowInFolder,
            OpenWhenDone = OpenWhenDone,
        };
        return rule;
    }

    public override string ToString() => Name;
}

/// <summary>The rules window: the list in order, one rule's form, and the "which rule applies to this address" preview.</summary>
public sealed class RulesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<Category> _categories;
    private readonly IReadOnlyList<DownloadQueue> _queues;
    private readonly string _defaultFolder;
    private RuleEditViewModel? _selected;
    private string? _error;
    private string _previewUrl = "";
    private Choice<string> _previewOrigin;

    public RulesViewModel(IDialogService dialogs, IReadOnlyList<Rule> rules, IReadOnlyList<Category> categories, IReadOnlyList<DownloadQueue> queues,
        string defaultFolder, long? selectId = null)
    {
        _dialogs = dialogs;
        _categories = categories;
        _queues = queues;
        _defaultFolder = defaultFolder;
        Items = new ObservableCollection<RuleEditViewModel>(rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Select(r => new RuleEditViewModel(r, categories, queues)));
        Items.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            foreach (RuleEditViewModel added in e.NewItems ?? Array.Empty<object>())
                added.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PreviewText));
        };
        foreach (var item in Items)
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PreviewText));
        _selected = Items.FirstOrDefault(i => i.Original.Id == selectId) ?? Items.FirstOrDefault();
        PreviewOrigins =
        [
            new(RuleOrigins.Browser, Strings.RuleOriginBrowser), new(RuleOrigins.Dialog, Strings.RuleOriginDialog),
            new(RuleOrigins.Batch, Strings.RuleOriginBatch), new(RuleOrigins.Clipboard, Strings.RuleOriginClipboard),
            new(RuleOrigins.Cli, Strings.RuleOriginCli), new(RuleOrigins.Api, Strings.RuleOriginApi), new(RuleOrigins.Mcp, Strings.RuleOriginMcp),
        ];
        _previewOrigin = PreviewOrigins[0];
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => Selected is not null && Items.IndexOf(Selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => Selected is not null && Items.IndexOf(Selected) < Items.Count - 1);
        BrowseCommand = new RelayCommand(Browse, () => Selected is not null);
    }

    public ObservableCollection<RuleEditViewModel> Items { get; }
    public IReadOnlyList<Choice<string>> PreviewOrigins { get; }
    public bool IsEmpty => Items.Count == 0;

    public RuleEditViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected is not null;

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public string PreviewUrl
    {
        get => _previewUrl;
        set
        {
            if (Set(ref _previewUrl, value))
                OnPropertyChanged(nameof(PreviewText));
        }
    }

    public Choice<string> PreviewOrigin
    {
        get => _previewOrigin;
        set
        {
            if (Set(ref _previewOrigin, value))
                OnPropertyChanged(nameof(PreviewText));
        }
    }

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand BrowseCommand { get; }

    /// <summary>The rules to save, in order (valid once <see cref="Validate"/> returned true).</summary>
    public IReadOnlyList<Rule> Result => Items.Select(i => i.ToRule()).ToList();

    /// <summary>Which of the rules as edited would apply to <see cref="PreviewUrl"/>, in words; empty while no address is typed.</summary>
    public string PreviewText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PreviewUrl))
                return "";
            if (!Urls.TryParseDownload(PreviewUrl.Trim(), out var parsed))
                return Strings.RulePreviewNone;
            var (uri, _, _) = Urls.SplitLogin(parsed);
            var valid = Items.Where(i => i.Validate() is null).Select(i => i.ToRule()).ToList();
            var name = uri.Query.Length == 0 && FileNames.FromUrl(uri) is { } guessed && Path.HasExtension(guessed) ? guessed : null;
            var category = Categories.MatchUrl(_categories.ToList(), uri.AbsoluteUri, name);
            if (RuleEngine.Match(valid, new RuleInput(uri, PreviewOrigin.Value, name, category.Id, null)) is not { } rule)
                return Strings.RulePreviewNone;
            var parts = new List<string> { string.Format(CultureInfo.CurrentCulture, Strings.RulePreviewApplies, rule.Name) };
            var chosen = _categories.FirstOrDefault(c => c.Id == rule.Actions.CategoryId);
            var folder = rule.Actions.Folder is { Length: > 0 } f ? f : chosen?.ResolveFolder(_defaultFolder);
            if (folder is not null)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.RulePreviewFolder, folder));
            if (rule.Actions.Rename is { Length: > 0 } pattern && name is not null)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.RulePreviewName, RenameTemplate.Apply(pattern, name, uri.IdnHost, (chosen ?? category).Name, DateTime.Now)));
            if (rule.Actions.SkipConfirmation)
                parts.Add(Strings.RulePreviewSkips);
            return string.Join(Environment.NewLine, parts);
        }
    }

    /// <summary>True when every rule can be saved; otherwise <see cref="Error"/> says why and the rule is selected.</summary>
    public bool Validate()
    {
        foreach (var item in Items)
        {
            if (item.Validate() is not { } problem)
                continue;
            Error = problem;
            Selected = item;
            return false;
        }
        Error = null;
        return true;
    }

    private void Add()
    {
        var rule = new Rule { Name = Strings.RuleNewName };
        var item = new RuleEditViewModel(rule, _categories, _queues);
        Items.Add(item);
        Selected = item;
    }

    private void Delete()
    {
        if (Selected is not { } item)
            return;
        // An unreadable rule cannot be seen again once deleted (it could be one a newer version understands): ask first.
        if (item.IsUnreadable && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.RuleUnreadableDeleteConfirm, item.Name), Strings.RulesTitle))
            return;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        OnPropertyChanged(nameof(PreviewText));
    }

    private void Move(int by)
    {
        if (Selected is not { } item)
            return;
        var index = Items.IndexOf(item);
        var target = index + by;
        if (target < 0 || target >= Items.Count)
            return;
        Items.Move(index, target);
        Selected = item;
        OnPropertyChanged(nameof(PreviewText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Browse()
    {
        if (Selected is not { } item)
            return;
        if (_dialogs.PickFolder(Strings.PickDestinationFolder, string.IsNullOrWhiteSpace(item.Folder) ? null : item.Folder) is { } folder)
            item.Folder = folder;
    }
}
