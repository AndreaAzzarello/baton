using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core.Automation;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>A line of the activity log as the window shows it.</summary>
public sealed record ActivityRow(string Time, string Origin, string Action, string Outcome, string Detail);

/// <summary>The activity log window: what the local API, MCP, the rules and the duplicate check did, filtered by origin.</summary>
public sealed class ActivityLogViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AutomationLog _log;
    private Choice<string?> _filter;

    public ActivityLogViewModel(IDialogService dialogs, AutomationLog log)
    {
        _dialogs = dialogs;
        _log = log;
        Filters =
        [
            new(null, Strings.ActivityLogAll),
            new(AutomationLog.Api, Strings.ActivityLogOriginApi),
            new(AutomationLog.Mcp, Strings.ActivityLogOriginMcp),
            new(AutomationLog.RulesOrigin, Strings.ActivityLogOriginRules),
            new(AutomationLog.Duplicates, Strings.ActivityLogOriginDuplicates),
        ];
        _filter = Filters[0];
        RefreshCommand = new RelayCommand(Refresh);
        ClearCommand = new RelayCommand(() =>
        {
            if (!_dialogs.Confirm(Strings.ActivityLogClearConfirm, Strings.ActivityLogTitle))
                return;
            _log.Clear();
            Refresh();
        }, () => Rows.Count > 0);
        Refresh();
    }

    public IReadOnlyList<Choice<string?>> Filters { get; }

    public Choice<string?> Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
                Refresh();
        }
    }

    public ObservableCollection<ActivityRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }

    public void Refresh()
    {
        Rows.Clear();
        foreach (var e in _log.Read(Filter.Value))
            Rows.Add(new ActivityRow(e.AtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), OriginText(e.Origin), ActivityText.Action(e.Action), ActivityText.Outcome(e.Outcome), ActivityText.Detail(e.Detail)));
        OnPropertyChanged(nameof(IsEmpty));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string OriginText(string origin) => origin switch
    {
        AutomationLog.Api => Strings.ActivityLogOriginApi,
        AutomationLog.Mcp => Strings.ActivityLogOriginMcp,
        AutomationLog.RulesOrigin => Strings.ActivityLogOriginRules,
        AutomationLog.Duplicates => Strings.ActivityLogOriginDuplicates,
        _ => origin,
    };
}
