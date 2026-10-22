using System.Globalization;
using System.IO;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>A preset of file types in the wizard ("Images", "Documents"...).</summary>
public sealed class PresetChoice(string key, string title, string[] extensions, bool isChecked, Action changed) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string[] Extensions { get; } = extensions;
    public string ToolTip => string.Join(" ", Extensions);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                changed();
        }
    }
}

/// <summary>One step of the wizard, for the list on its left.</summary>
public sealed class WizardStep(int index, string title) : ObservableObject
{
    private bool _isCurrent;
    private bool _isDone;

    public int Index { get; } = index;
    public int Number => Index + 1;
    public string Title { get; } = title;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }

    public string AccessibleName => string.Format(CultureInfo.CurrentCulture, Strings.GrabberStepName, Number, Title);
}

/// <summary>
/// "New grabber project": address and mode, scope (depth, domains, robots.txt, pace), filters, access and limits,
/// destination and schedule. Values are kept as text, so invalid input is shown and explained instead of lost.
/// </summary>
public sealed class GrabberWizardViewModel : ObservableObject
{
    public const int StepCount = 5;

    private readonly IDialogService _dialogs;
    private readonly Func<DateTime> _utcNow;
    private readonly GrabberProject _original;
    private readonly string _defaultFolder;
    private int _step;
    private string? _error;
    private bool _robotsWarningAccepted;

    // Step 1
    private string _name;
    private string _startUrl;
    private bool _mirror;
    private bool _stripScripts;
    // Step 2
    private string _depth;
    private bool _sameDomainOnly;
    private string _domains;
    private bool _respectRobots;
    private string _rate;
    private string _userAgent;
    // Step 3
    private string _extensions;
    private string _minSizeKB;
    private string _maxSizeMB;
    private string _include;
    private string _exclude;
    // Step 4
    private bool _useCookies;
    private bool _forgetCookies;
    private bool _cookiesCleared;
    private bool _useCredentials;
    private bool _useProxy;
    private string _maxPages;
    private string _maxFiles;
    private string _maxTotalMB;
    private string _maxPerPattern;
    // Step 5
    private string _folder;
    private bool _folderChosen;
    private CategoryChoice _category;
    private DownloadQueue? _queue;
    private bool _scheduled;
    private bool _once;
    private DateTime? _date;
    private string _start;
    private string _repeatHours;

    public GrabberWizardViewModel(IDialogService dialogs, GrabberProject project, IReadOnlyList<Category> categories, IReadOnlyList<DownloadQueue> queues,
        string downloadFolder, Func<DateTime> utcNow)
    {
        _dialogs = dialogs;
        _utcNow = utcNow;
        _original = project.Clone();
        IsNew = project.Id == 0;
        _defaultFolder = Path.Combine(downloadFolder, Strings.GrabberSitesFolder);

        _name = project.Name;
        _startUrl = project.StartUrl;
        _mirror = project.Mode == GrabberMode.Mirror;
        _stripScripts = project.StripScripts;
        _depth = project.MaxDepth.ToString(CultureInfo.CurrentCulture);
        _sameDomainOnly = project.SameDomainOnly;
        _domains = string.Join(Environment.NewLine, project.AllowedDomains);
        _respectRobots = project.RespectRobots;
        _robotsWarningAccepted = !project.RespectRobots; // already accepted when the project was saved
        _rate = project.RequestsPerSecond.ToString("0.##", CultureInfo.CurrentCulture);
        _userAgent = project.UserAgent;
        var presetExtensions = new HashSet<string>();
        Presets = GrabberFilters.Presets.Select(p =>
        {
            var on = p.Value.Length > 0 && p.Value.All(project.Extensions.Contains);
            if (on)
                presetExtensions.UnionWith(p.Value);
            return new PresetChoice(p.Key, PresetTitle(p.Key), p.Value, on, () => OnPropertyChanged(nameof(ExtensionsSummary)));
        }).ToList();
        _extensions = string.Join(" ", project.Extensions.Where(e => !presetExtensions.Contains(e)));
        _minSizeKB = project.MinSizeBytes > 0 ? (project.MinSizeBytes / 1024).ToString(CultureInfo.CurrentCulture) : "";
        _maxSizeMB = project.MaxSizeBytes > 0 ? (project.MaxSizeBytes / (1024 * 1024)).ToString(CultureInfo.CurrentCulture) : "";
        _include = project.IncludePattern;
        _exclude = project.ExcludePattern;
        _useCookies = project.UseCookies && project.Cookies.Count > 0;
        _forgetCookies = project.ForgetCookiesAfterRun;
        ClearCookiesCommand = new RelayCommand(() =>
        {
            _cookiesCleared = true;
            UseCookies = false;
            OnPropertyChanged(nameof(CookieCount));
            OnPropertyChanged(nameof(HasCookies));
            OnPropertyChanged(nameof(CookieText));
        }, () => HasCookies);
        _useCredentials = project.UseSavedCredentials;
        _useProxy = project.UseProxy;
        _maxPages = project.MaxPages.ToString(CultureInfo.CurrentCulture);
        _maxFiles = project.MaxFiles.ToString(CultureInfo.CurrentCulture);
        _maxTotalMB = project.MaxTotalMB.ToString(CultureInfo.CurrentCulture);
        _maxPerPattern = project.MaxPagesPerPattern.ToString(CultureInfo.CurrentCulture);
        _folderChosen = project.Folder.Length > 0;
        _folder = project.Folder;

        Categories = [new CategoryChoice(null, Strings.BatchCategoryAuto), .. categories.Select(c => new CategoryChoice(c, c.DisplayName))];
        _category = Categories.FirstOrDefault(c => c.Category?.Id == project.CategoryId) ?? Categories[0];
        Queues = queues;
        _queue = queues.FirstOrDefault(q => q.Id == project.QueueId) ?? queues.FirstOrDefault(q => q.IsMain) ?? queues.FirstOrDefault();

        var s = project.Schedule;
        _scheduled = s.Enabled;
        _once = s.Mode == ScheduleMode.Once;
        _date = s.Date?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today.AddDays(1);
        _start = s.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        _repeatHours = s.RepeatMinutes > 0 ? (s.RepeatMinutes / 60.0).ToString("0.##", CultureInfo.CurrentCulture) : "";
        Days = Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7))
            .Select(d => new DayChoice(d, s.Days.Contains(d), () => OnPropertyChanged(nameof(SchedulePreview)))).ToList();
        if (!_mirror && !_folderChosen)
            _folder = "";
        else if (_mirror && !_folderChosen)
            _folder = _defaultFolder;

        Steps =
        [
            new WizardStep(0, Strings.GrabberStepAddress), new WizardStep(1, Strings.GrabberStepScope), new WizardStep(2, Strings.GrabberStepFilters),
            new WizardStep(3, Strings.GrabberStepAccess), new WizardStep(4, Strings.GrabberStepDestination),
        ];
        Steps[0].IsCurrent = true;
        BackCommand = new RelayCommand(() => GoTo(_step - 1), () => _step > 0);
        NextCommand = new RelayCommand(Next, () => _step < StepCount - 1);
        GoToCommand = new RelayCommand(p => { if (p is WizardStep step) Jump(step.Index); });
        BrowseCommand = new RelayCommand(Browse);
    }

    public bool IsNew { get; }
    public string Title => IsNew ? Strings.GrabberWizardTitle : string.Format(CultureInfo.CurrentCulture, Strings.GrabberWizardEditTitle, _original.Name);
    public IReadOnlyList<WizardStep> Steps { get; }
    public IReadOnlyList<PresetChoice> Presets { get; }
    public IReadOnlyList<CategoryChoice> Categories { get; }
    public IReadOnlyList<DownloadQueue> Queues { get; }
    public IReadOnlyList<DayChoice> Days { get; }

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand BrowseCommand { get; }

    public int Step => _step;
    public bool IsLastStep => _step == StepCount - 1;
    public string StepTitle => Steps[_step].Title;
    public string StepCounter => string.Format(CultureInfo.CurrentCulture, Strings.GrabberStepCounter, _step + 1, StepCount);

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>The project built from the fields, once saved.</summary>
    public GrabberProject? Result { get; private set; }

    /// <summary>Set by the window: run the project right after saving it.</summary>
    public bool RunNow { get; set; }

    // ------------------------------------------------------------ step 1: address and mode

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                _nameFromUrl = false;
        }
    }

    public string StartUrl
    {
        get => _startUrl;
        set
        {
            if (!Set(ref _startUrl, value))
                return;
            // The name follows the address until the user types one.
            if (IsNew && Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (string.IsNullOrWhiteSpace(_name) || _nameFromUrl))
            {
                Name = uri.Host;
                _nameFromUrl = true;
            }
        }
    }

    private bool _nameFromUrl;

    public bool FilesMode
    {
        get => !_mirror;
        set
        {
            if (value)
                MirrorMode = false;
        }
    }

    /// <summary>Offline copy without scripts (default); turning it off is warned about.</summary>
    public bool StripScripts
    {
        get => _stripScripts;
        set
        {
            if (Set(ref _stripScripts, value))
                OnPropertyChanged(nameof(StripScriptsWarning));
        }
    }

    public string? StripScriptsWarning => _stripScripts ? null : Strings.GrabberStripScriptsWarning;

    public bool MirrorMode
    {
        get => _mirror;
        set
        {
            if (!Set(ref _mirror, value))
                return;
            OnPropertyChanged(nameof(FilesMode));
            OnPropertyChanged(nameof(ShowQueue));
            OnPropertyChanged(nameof(FolderLabel));
            OnPropertyChanged(nameof(FolderHint));
            OnPropertyChanged(nameof(ExtensionsSummary));
            if (!_folderChosen)
            {
                _folder = value ? _defaultFolder : "";
                OnPropertyChanged(nameof(Folder));
            }
        }
    }

    public int CookieCount => _cookiesCleared ? 0 : _original.Cookies.Count;
    public bool HasCookies => CookieCount > 0;

    public string CookieText => HasCookies
        ? string.Format(CultureInfo.CurrentCulture, Strings.GrabberCookiesFromBrowser, CookieCount)
        : Strings.GrabberNoCookies;

    // ------------------------------------------------------------ step 2: scope

    public string Depth { get => _depth; set => Set(ref _depth, value); }

    public bool SameDomainOnly
    {
        get => _sameDomainOnly;
        set
        {
            if (Set(ref _sameDomainOnly, value))
                OnPropertyChanged(nameof(OtherDomains));
        }
    }

    public bool OtherDomains
    {
        get => !_sameDomainOnly;
        set
        {
            if (value)
                SameDomainOnly = false;
        }
    }

    public string Domains { get => _domains; set => Set(ref _domains, value); }

    public bool RespectRobots
    {
        get => _respectRobots;
        set
        {
            if (!Set(ref _respectRobots, value))
                return;
            if (value)
                _robotsWarningAccepted = false;
            OnPropertyChanged(nameof(ShowRobotsWarning));
        }
    }

    public bool ShowRobotsWarning => !_respectRobots;

    public string Rate { get => _rate; set => Set(ref _rate, value); }
    public string UserAgent { get => _userAgent; set => Set(ref _userAgent, value); }

    // ------------------------------------------------------------ step 3: filters

    public string Extensions
    {
        get => _extensions;
        set
        {
            if (Set(ref _extensions, value))
                OnPropertyChanged(nameof(ExtensionsSummary));
        }
    }

    /// <summary>What will be kept, in words.</summary>
    public string ExtensionsSummary
    {
        get
        {
            var all = ChosenExtensions();
            if (all.Count == 0)
                return _mirror ? Strings.GrabberTypesMirrorAll : Strings.GrabberTypesAll;
            return string.Format(CultureInfo.CurrentCulture, _mirror ? Strings.GrabberTypesMirrorSome : Strings.GrabberTypesSome, string.Join(" ", all));
        }
    }

    public string MinSizeKB { get => _minSizeKB; set => Set(ref _minSizeKB, value); }
    public string MaxSizeMB { get => _maxSizeMB; set => Set(ref _maxSizeMB, value); }

    public string Include
    {
        get => _include;
        set
        {
            if (Set(ref _include, value))
                OnPropertyChanged(nameof(IncludeError));
        }
    }

    public string Exclude
    {
        get => _exclude;
        set
        {
            if (Set(ref _exclude, value))
                OnPropertyChanged(nameof(ExcludeError));
        }
    }

    public string? IncludeError => GrabberFilters.CheckPattern(_include);
    public string? ExcludeError => GrabberFilters.CheckPattern(_exclude);

    // ------------------------------------------------------------ step 4: access and limits

    public bool UseCookies { get => _useCookies; set => Set(ref _useCookies, value); }

    /// <summary>The browser's cookies are a login: by default they are dropped once a run has used them.</summary>
    public bool ForgetCookiesAfterRun { get => _forgetCookies; set => Set(ref _forgetCookies, value); }

    /// <summary>Drops the project's cookies when it is saved.</summary>
    public ICommand ClearCookiesCommand { get; }

    public bool CookiesCleared => _cookiesCleared;
    public bool UseCredentials { get => _useCredentials; set => Set(ref _useCredentials, value); }
    public bool UseProxy { get => _useProxy; set => Set(ref _useProxy, value); }
    public string MaxPages { get => _maxPages; set => Set(ref _maxPages, value); }
    public string MaxFiles { get => _maxFiles; set => Set(ref _maxFiles, value); }
    public string MaxTotalMB { get => _maxTotalMB; set => Set(ref _maxTotalMB, value); }
    public string MaxPerPattern { get => _maxPerPattern; set => Set(ref _maxPerPattern, value); }

    // ------------------------------------------------------------ step 5: destination and schedule

    public string Folder
    {
        get => _folder;
        set
        {
            if (Set(ref _folder, value))
                _folderChosen = value.Trim().Length > 0;
        }
    }

    public string FolderLabel => _mirror ? Strings.GrabberFolderMirrorLabel : Strings.GrabberFolderFilesLabel;
    public string FolderHint => _mirror ? Strings.GrabberFolderMirrorHint : Strings.GrabberFolderFilesHint;

    /// <summary>Queue and category matter only for files sent to the download list.</summary>
    public bool ShowQueue => !_mirror;

    public CategoryChoice Category { get => _category; set { if (value is not null) Set(ref _category, value); } }
    public DownloadQueue? Queue { get => _queue; set => Set(ref _queue, value); }

    public bool Scheduled { get => _scheduled; set { if (Set(ref _scheduled, value)) OnPropertyChanged(nameof(SchedulePreview)); } }
    public bool Weekly { get => !_once; set { if (value) Once = false; } }

    public bool Once
    {
        get => _once;
        set
        {
            if (!Set(ref _once, value))
                return;
            OnPropertyChanged(nameof(Weekly));
            OnPropertyChanged(nameof(SchedulePreview));
        }
    }

    public DateTime? Date { get => _date; set { if (Set(ref _date, value)) OnPropertyChanged(nameof(SchedulePreview)); } }
    public string Start { get => _start; set { if (Set(ref _start, value)) OnPropertyChanged(nameof(SchedulePreview)); } }
    public string RepeatHours { get => _repeatHours; set { if (Set(ref _repeatHours, value)) OnPropertyChanged(nameof(SchedulePreview)); } }

    public string SchedulePreview
    {
        get
        {
            if (!Scheduled)
                return Strings.GrabberNotScheduled;
            var schedule = new QueueSchedule();
            if (BuildSchedule(schedule) is not null)
                return Strings.QueueScheduleInvalid;
            var next = ScheduleCalculator.NextStart(schedule, _utcNow(), TimeZoneInfo.Local);
            var text = next is { } n
                ? string.Format(CultureInfo.CurrentCulture, Strings.GrabberNextRun, TimeZoneInfo.ConvertTimeFromUtc(n, TimeZoneInfo.Local))
                : Strings.QueueNoNextStart;
            return _mirror ? text : $"{text} {Strings.GrabberScheduleFilesHint}";
        }
    }

    // ------------------------------------------------------------ navigation

    private void Next()
    {
        if (ValidateStep(_step) is { } error)
        {
            Error = error;
            return;
        }
        if (_step == 1 && !_respectRobots && !_robotsWarningAccepted)
        {
            if (!_dialogs.Confirm(Strings.GrabberRobotsConfirm, Strings.GrabberWizardTitle))
                return;
            _robotsWarningAccepted = true;
        }
        GoTo(_step + 1);
    }

    /// <summary>The step list on the left: backwards always, forwards only through valid steps.</summary>
    private void Jump(int target)
    {
        while (_step < target)
        {
            var before = _step;
            Next();
            if (_step == before)
                return;
        }
        if (target < _step)
            GoTo(target);
    }

    private void GoTo(int step)
    {
        if (step < 0 || step >= StepCount)
            return;
        Error = null;
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == step;
            Steps[i].IsDone = i < step;
        }
        _step = step;
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepCounter));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Validates every step, moves to the first wrong one, and builds the project. False when something is wrong.</summary>
    public bool Finish()
    {
        for (var i = 0; i < StepCount; i++)
        {
            if (ValidateStep(i) is { } error)
            {
                GoTo(i);
                Error = error;
                return false;
            }
        }
        if (!_respectRobots && !_robotsWarningAccepted)
        {
            if (!_dialogs.Confirm(Strings.GrabberRobotsConfirm, Strings.GrabberWizardTitle))
                return false;
            _robotsWarningAccepted = true;
        }
        Result = Build();
        return true;
    }

    private string? ValidateStep(int step)
    {
        switch (step)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(Name))
                    return Strings.GrabberErrorName;
                if (!Urls.TryParseHttp(StartUrl, out _))
                    return Strings.GrabberErrorUrl;
                return null;
            case 1:
                if (!int.TryParse(Depth, NumberStyles.Integer, CultureInfo.CurrentCulture, out var depth) || depth is < 0 or > 20)
                    return Strings.GrabberErrorDepth;
                if (!double.TryParse(Rate, NumberStyles.Float, CultureInfo.CurrentCulture, out var rate) || rate is < 0.1 or > 20)
                    return Strings.GrabberErrorRate;
                if (!_sameDomainOnly && ParseList(Domains).Any(d => Uri.CheckHostName(d.TrimStart('*', '.')) == UriHostNameType.Unknown))
                    return Strings.GrabberErrorDomains;
                if (UserAgent.Any(char.IsControl) || UserAgent.Length > 300)
                    return Strings.GrabberErrorUserAgent;
                return null;
            case 2:
                if (MinSizeKB.Trim().Length > 0 && (!long.TryParse(MinSizeKB, NumberStyles.Integer, CultureInfo.CurrentCulture, out var min) || min < 0))
                    return Strings.GrabberErrorSize;
                if (MaxSizeMB.Trim().Length > 0 && (!long.TryParse(MaxSizeMB, NumberStyles.Integer, CultureInfo.CurrentCulture, out var max) || max < 1))
                    return Strings.GrabberErrorSize;
                if (MinSizeKB.Trim().Length > 0 && MaxSizeMB.Trim().Length > 0 && long.Parse(MinSizeKB, CultureInfo.CurrentCulture) > long.Parse(MaxSizeMB, CultureInfo.CurrentCulture) * 1024)
                    return Strings.GrabberErrorSizeOrder;
                return IncludeError ?? ExcludeError;
            case 3:
                if (!int.TryParse(MaxPages, out var pages) || pages is < 1 or > 100_000)
                    return Strings.GrabberErrorMaxPages;
                if (!int.TryParse(MaxFiles, out var files) || files is < 1 or > 200_000)
                    return Strings.GrabberErrorMaxFiles;
                if (!long.TryParse(MaxTotalMB, out var total) || total is < 1 or > 10_000_000)
                    return Strings.GrabberErrorMaxTotal;
                if (!int.TryParse(MaxPerPattern, out var perPattern) || perPattern is < 1 or > 100_000)
                    return Strings.GrabberErrorMaxPattern;
                return null;
            case 4:
                var folder = Folder.Trim();
                if (_mirror && folder.Length == 0)
                    return Strings.GrabberErrorFolderMissing;
                if (folder.Length > 0 && !Path.IsPathFullyQualified(folder))
                    return Strings.ErrorFolderNotAbsolute;
                return Scheduled ? BuildSchedule(new QueueSchedule()) : null;
            default:
                return null;
        }
    }

    private string? BuildSchedule(QueueSchedule schedule)
    {
        schedule.Enabled = Scheduled;
        schedule.Mode = Once ? ScheduleMode.Once : ScheduleMode.Weekly;
        schedule.Days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToArray();
        schedule.Date = Date is { } date ? DateOnly.FromDateTime(date) : null;
        if (!TimeOnly.TryParse(Start, CultureInfo.CurrentCulture, out var start))
            return Strings.ErrorQueueStart;
        var repeat = 0;
        if (RepeatHours.Trim().Length > 0)
        {
            if (!double.TryParse(RepeatHours, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours) || hours is < 0 or > 24)
                return Strings.GrabberErrorRepeat;
            repeat = (int)Math.Round(hours * 60);
        }
        if (schedule.Mode == ScheduleMode.Weekly && schedule.Days.Length == 0)
            return Strings.ErrorQueueDays;
        if (schedule.Mode == ScheduleMode.Once && schedule.Date is null)
            return Strings.ErrorQueueDate;
        schedule.Start = start;
        schedule.Stop = null;
        schedule.RepeatMinutes = repeat;
        return null;
    }

    private GrabberProject Build()
    {
        var p = _original.Clone();
        p.Name = Name.Trim();
        p.StartUrl = StartUrl.Trim();
        p.Mode = _mirror ? GrabberMode.Mirror : GrabberMode.Files;
        p.MaxDepth = int.Parse(Depth, CultureInfo.CurrentCulture);
        p.SameDomainOnly = _sameDomainOnly;
        p.AllowedDomains = _sameDomainOnly ? [] : ParseList(Domains);
        p.RespectRobots = _respectRobots;
        p.RequestsPerSecond = double.Parse(Rate, CultureInfo.CurrentCulture);
        p.UserAgent = UserAgent.Trim();
        p.Extensions = ChosenExtensions();
        p.MinSizeBytes = MinSizeKB.Trim().Length > 0 ? long.Parse(MinSizeKB, CultureInfo.CurrentCulture) * 1024 : 0;
        p.MaxSizeBytes = MaxSizeMB.Trim().Length > 0 ? long.Parse(MaxSizeMB, CultureInfo.CurrentCulture) * 1024 * 1024 : 0;
        p.IncludePattern = Include.Trim();
        p.ExcludePattern = Exclude.Trim();
        p.UseCookies = _useCookies;
        p.ForgetCookiesAfterRun = _forgetCookies;
        p.StripScripts = _stripScripts;
        if (_cookiesCleared)
            p.Cookies = [];
        p.UseSavedCredentials = _useCredentials;
        p.UseProxy = _useProxy;
        p.MaxPages = int.Parse(MaxPages, CultureInfo.CurrentCulture);
        p.MaxFiles = int.Parse(MaxFiles, CultureInfo.CurrentCulture);
        p.MaxTotalMB = long.Parse(MaxTotalMB, CultureInfo.CurrentCulture);
        p.MaxPagesPerPattern = int.Parse(MaxPerPattern, CultureInfo.CurrentCulture);
        p.Folder = Folder.Trim();
        p.CategoryId = Category.Category?.Id;
        p.QueueId = Queue?.Id;
        var schedule = p.Schedule;
        if (Scheduled)
            BuildSchedule(schedule);
        schedule.Enabled = Scheduled;
        return p;
    }

    private List<string> ChosenExtensions() =>
        Presets.Where(p => p.IsChecked).SelectMany(p => p.Extensions)
            .Concat(ParseList(Extensions).Select(e => e.TrimStart('*', '.').ToLowerInvariant()))
            .Where(e => e.Length > 0).Distinct().ToList();

    private static List<string> ParseList(string text) =>
        text.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();

    private static string PresetTitle(string key) => key switch
    {
        "images" => Strings.GrabberPresetImages,
        "documents" => Strings.GrabberPresetDocuments,
        "audio" => Strings.GrabberPresetAudio,
        "video" => Strings.GrabberPresetVideo,
        "archives" => Strings.GrabberPresetArchives,
        _ => key,
    };

    private void Browse()
    {
        var current = Folder.Trim();
        if (_dialogs.PickFolder(Strings.PickDestinationFolder, Directory.Exists(current) ? current : null) is { } picked)
            Folder = picked;
    }
}
