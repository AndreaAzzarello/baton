using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.App.Views;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A day of the week as a check box.</summary>
public sealed class DayChoice(DayOfWeek day, bool isChecked, Action changed) : ObservableObject
{
    private bool _isChecked = isChecked;

    public DayOfWeek Day { get; } = day;
    public string Short => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(Day);
    public string Full => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(Day);

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

/// <summary>One queue being edited; values are text so that invalid input can be shown, not lost.</summary>
public sealed class QueueEditViewModel : ObservableObject
{
    private readonly Func<DateTime> _utcNow;
    private string _name;
    private string _maxConcurrent;
    private string _retryCount;
    private string _retryDelay;
    private string _quota;
    private Choice<EndAction> _endAction;
    private bool _scheduled;
    private string _start;
    private string _stop;
    private bool _once;
    private DateTime? _date;
    private string _repeat;
    private bool _wake;

    public QueueEditViewModel(DownloadQueue queue, Func<DateTime> utcNow)
    {
        Original = queue;
        _utcNow = utcNow;
        _name = queue.Name;
        _maxConcurrent = queue.MaxConcurrent.ToString(CultureInfo.CurrentCulture);
        _retryCount = queue.RetryCount.ToString(CultureInfo.CurrentCulture);
        _retryDelay = queue.RetryDelaySeconds.ToString(CultureInfo.CurrentCulture);
        _quota = queue.QuotaMBPerHour.ToString(CultureInfo.CurrentCulture);
        _endAction = EndActions.First(a => a.Value == queue.EndAction);
        var s = queue.Schedule;
        _scheduled = s.Enabled;
        _start = s.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        _stop = s.Stop?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _once = s.Mode == ScheduleMode.Once;
        _date = s.Date?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        _repeat = s.RepeatMinutes.ToString(CultureInfo.CurrentCulture);
        _wake = s.WakeToRun;
        // Monday first, as in Italy.
        Days = Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7))
            .Select(d => new DayChoice(d, s.Days.Contains(d), () => OnPropertyChanged(nameof(Preview)))).ToList();
    }

    public DownloadQueue Original { get; }
    public bool IsMain => Original.IsMain;
    public bool IsNew => Original.Id == 0;
    public IReadOnlyList<DayChoice> Days { get; }

    public static IReadOnlyList<Choice<EndAction>> EndActions { get; } =
        Enum.GetValues<EndAction>().Select(a => new Choice<EndAction>(a, CountdownWindow.ActionText(a))).ToList();

    public string Name { get => _name; set => Set(ref _name, value); }
    public string MaxConcurrent { get => _maxConcurrent; set => Set(ref _maxConcurrent, value); }
    public string RetryCount { get => _retryCount; set => Set(ref _retryCount, value); }
    public string RetryDelay { get => _retryDelay; set => Set(ref _retryDelay, value); }
    public string Quota { get => _quota; set => Set(ref _quota, value); }
    public Choice<EndAction> EndAction { get => _endAction; set => Set(ref _endAction, value); }
    public bool Scheduled { get => _scheduled; set { if (Set(ref _scheduled, value)) OnPropertyChanged(nameof(Preview)); } }
    public string Start { get => _start; set { if (Set(ref _start, value)) OnPropertyChanged(nameof(Preview)); } }
    public string Stop { get => _stop; set { if (Set(ref _stop, value)) OnPropertyChanged(nameof(Preview)); } }
    public bool Weekly { get => !_once; set { if (value) Once = false; } }

    public bool Once
    {
        get => _once;
        set
        {
            if (!Set(ref _once, value))
                return;
            OnPropertyChanged(nameof(Weekly));
            OnPropertyChanged(nameof(Preview));
        }
    }

    public DateTime? Date { get => _date; set { if (Set(ref _date, value)) OnPropertyChanged(nameof(Preview)); } }
    public string Repeat { get => _repeat; set { if (Set(ref _repeat, value)) OnPropertyChanged(nameof(Preview)); } }
    public bool WakeToRun { get => _wake; set => Set(ref _wake, value); }

    /// <summary>When the schedule would start next, recomputed while typing.</summary>
    public string Preview
    {
        get
        {
            if (!Scheduled)
                return Strings.QueueNotScheduled;
            if (TryBuild(out var queue) is not null)
                return Strings.QueueScheduleInvalid;
            return ScheduleCalculator.NextStart(queue.Schedule, _utcNow(), TimeZoneInfo.Local) is { } next
                ? string.Format(CultureInfo.CurrentCulture, Strings.QueueNextStart, TimeZoneInfo.ConvertTimeFromUtc(next, TimeZoneInfo.Local))
                : Strings.QueueNoNextStart;
        }
    }

    public override string ToString() => Name;

    /// <summary>Builds the queue from the fields; returns the error message, or null when everything is valid.</summary>
    public string? TryBuild(out DownloadQueue queue)
    {
        queue = Original.Clone();
        queue.Name = Name.Trim();
        if (queue.Name.Length == 0)
            return Strings.ErrorQueueName;
        if (!int.TryParse(MaxConcurrent, out var max) || max is < 1 or > 20)
            return Strings.ErrorQueueConcurrent;
        if (!int.TryParse(RetryCount, out var retries) || retries is < 0 or > 100)
            return Strings.ErrorQueueRetries;
        if (!int.TryParse(RetryDelay, out var delay) || delay is < 0 or > 86400)
            return Strings.ErrorQueueRetryDelay;
        if (!long.TryParse(Quota, out var quota) || quota < 0)
            return Strings.ErrorQueueQuota;
        queue.MaxConcurrent = max;
        queue.RetryCount = retries;
        queue.RetryDelaySeconds = delay;
        queue.QuotaMBPerHour = quota;
        queue.EndAction = EndAction.Value;

        var schedule = queue.Schedule;
        schedule.Enabled = Scheduled;
        schedule.WakeToRun = WakeToRun;
        schedule.Mode = Once ? ScheduleMode.Once : ScheduleMode.Weekly;
        schedule.Days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToArray();
        schedule.Date = Date is { } date ? DateOnly.FromDateTime(date) : null;
        if (!Scheduled)
            return null; // the other schedule fields are kept but not checked
        if (!TimeOnly.TryParse(Start, CultureInfo.CurrentCulture, out var start))
            return Strings.ErrorQueueStart;
        TimeOnly? stop = null;
        if (!string.IsNullOrWhiteSpace(Stop))
        {
            if (!TimeOnly.TryParse(Stop, CultureInfo.CurrentCulture, out var parsed) || parsed == start)
                return Strings.ErrorQueueStop;
            stop = parsed;
        }
        if (!int.TryParse(Repeat, out var repeat) || repeat is < 0 or > 24 * 60)
            return Strings.ErrorQueueRepeat;
        if (schedule.Mode == ScheduleMode.Weekly && schedule.Days.Length == 0)
            return Strings.ErrorQueueDays;
        if (schedule.Mode == ScheduleMode.Once && schedule.Date is null)
            return Strings.ErrorQueueDate;
        schedule.Start = start;
        schedule.Stop = stop;
        schedule.RepeatMinutes = repeat;
        return null;
    }
}

public sealed class QueuesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly List<DownloadQueue> _deleted = [];
    private readonly Func<DateTime> _utcNow;
    private QueueEditViewModel? _selected;
    private string? _error;
    private bool _startupEnabled;

    public QueuesViewModel(IDialogService dialogs, IReadOnlyList<DownloadQueue> queues, Func<DateTime> utcNow, bool startupEnabled,
        Action enableStartup, long? selectId = null)
    {
        _dialogs = dialogs;
        _utcNow = utcNow;
        _startupEnabled = startupEnabled;
        Items = new ObservableCollection<QueueEditViewModel>(queues.Select(q => Edit(q)));
        _selected = Items.FirstOrDefault(i => i.Original.Id == selectId) ?? Items.FirstOrDefault();
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, () => Selected is { IsMain: false });
        EnableStartupCommand = new RelayCommand(() =>
        {
            enableStartup();
            _startupEnabled = true;
            OnPropertyChanged(nameof(ShowStartupWarning));
        });
    }

    public ObservableCollection<QueueEditViewModel> Items { get; }

    public QueueEditViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand EnableStartupCommand { get; }

    /// <summary>A schedule only works while the app runs: warn when it does not start with Windows.</summary>
    public bool ShowStartupWarning => !_startupEnabled && Items.Any(i => i.Scheduled);

    public IReadOnlyList<DownloadQueue> Deleted => _deleted;

    public List<DownloadQueue> Result { get; } = [];

    public bool Validate()
    {
        Result.Clear();
        foreach (var item in Items)
        {
            var error = item.TryBuild(out var queue);
            if (error is null && Items.Count(i => string.Equals(i.Name.Trim(), item.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)) > 1)
                error = string.Format(CultureInfo.CurrentCulture, Strings.ErrorQueueDuplicate, item.Name.Trim());
            if (error is not null)
            {
                Selected = item;
                Error = $"{item.Name}: {error}";
                return false;
            }
            Result.Add(queue);
        }
        Error = null;
        return true;
    }

    private QueueEditViewModel Edit(DownloadQueue queue)
    {
        var edit = new QueueEditViewModel(queue, _utcNow);
        edit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueEditViewModel.Scheduled))
                OnPropertyChanged(nameof(ShowStartupWarning));
        };
        return edit;
    }

    private void Add()
    {
        var item = Edit(new DownloadQueue { Name = Strings.QueueNewName, MaxConcurrent = 2 });
        Items.Add(item);
        Selected = item;
    }

    private void Delete()
    {
        if (Selected is not { IsMain: false } item)
            return;
        if (!item.IsNew && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteQueue, item.Name), Strings.QueuesTitle))
            return;
        if (!item.IsNew)
            _deleted.Add(item.Original);
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        OnPropertyChanged(nameof(ShowStartupWarning));
    }
}
