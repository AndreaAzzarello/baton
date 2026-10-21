using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Integration;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>
/// Opens the grabber's windows: the wizard for a new or changed project, and the preview of a run. Used by the main
/// window, the project list and requests from the browser ("Capture this site").
/// </summary>
public sealed class GrabberUi(GrabberService grabber, DownloadService downloads, IDialogService dialogs, IShellService shell)
{
    public GrabberService Service => grabber;

    /// <summary>The wizard for a new project (optionally prefilled), then the first run if the user asked for it.</summary>
    public GrabberProject? Create(GrabberProject? template = null, bool standalone = false) => Edit(template ?? new GrabberProject(), standalone);

    /// <summary>A new project for a page sent by the browser: its address, its title as the name, its cookies.</summary>
    public GrabberProject? CreateFromBrowser(GrabRequest request)
    {
        var url = new Uri(request.Url);
        var title = string.IsNullOrWhiteSpace(request.Title) ? url.Host : request.Title.Trim();
        var project = new GrabberProject
        {
            Name = title.Length > 80 ? title[..80].TrimEnd() : title,
            StartUrl = url.AbsoluteUri,
            Cookies = request.Cookies ?? [],
            UseCookies = request.Cookies is { Count: > 0 },
        };
        return Create(project, standalone: true);
    }

    public GrabberProject? Edit(GrabberProject project, bool standalone = false)
    {
        var wizard = new GrabberWizardViewModel(dialogs, project, downloads.Categories, downloads.Queues, downloads.Settings.DownloadFolder, () => downloads.UtcNow);
        if (!dialogs.ShowGrabberWizard(wizard, standalone) || wizard.Result is not { } result)
            return null;
        // The cookies are the project's as they are now: a run may have forgotten them while the wizard was open.
        if (!wizard.CookiesCleared && result.Id != 0 && grabber.Find(result.Id) is { } current)
            result.Cookies = current.Cookies;
        GrabberProject saved;
        try
        {
            saved = grabber.Save(result);
        }
        catch (ArgumentException ex)
        {
            dialogs.ShowError(ex.Message, Strings.GrabberWizardTitle);
            return null;
        }
        if (wizard.RunNow)
            Run(saved.Id);
        return saved;
    }

    /// <summary>Starts a run (or shows the one already running) in its preview window.</summary>
    public void Run(long projectId)
    {
        GrabberRun run;
        try
        {
            run = grabber.Start(projectId);
        }
        catch (ArgumentException ex)
        {
            dialogs.ShowError(ex.Message, Strings.GrabberProjectsTitle);
            return;
        }
        Show(run);
    }

    /// <summary>The preview of a run, running or finished.</summary>
    public void Show(GrabberRun run) =>
        dialogs.ShowGrabberRun(run,
            () => new GrabberRunViewModel(grabber, run, dialogs, shell, downloads.Categories, downloads.Queues));

    public void ShowProjects() => dialogs.ShowGrabberProjects(() => new GrabberProjectsViewModel(this, dialogs, shell));
}

/// <summary>A saved project as a row of the project list.</summary>
public sealed class GrabberProjectRow(GrabberProject project, string state, string next) : ObservableObject
{
    public GrabberProject Project { get; } = project;
    public long Id => Project.Id;
    public string Name => Project.Name;
    public string Url => Project.StartUrl;
    public string ModeText => Project.Mode == GrabberMode.Mirror ? Strings.GrabberModeMirrorShort : Strings.GrabberModeFilesShort;

    public string LastRunText => Project.LastRunUtc is { } run
        ? TimeZoneInfo.ConvertTimeFromUtc(run, TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture)
        : Strings.GrabberNeverRun;

    public string LastResult => Project.LastResult ?? "";
    public string State { get; } = state;
    public string NextText { get; } = next;
    public override string ToString() => $"{Name}, {ModeText}, {State}";
}

/// <summary>"Grabber": the saved projects, with what each did last and when it runs next.</summary>
public sealed class GrabberProjectsViewModel : ObservableObject
{
    private readonly GrabberUi _ui;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GrabberProjectRow? _selected;

    public GrabberProjectsViewModel(GrabberUi ui, IDialogService dialogs, IShellService shell)
    {
        _ui = ui;
        _dialogs = dialogs;
        _shell = shell;
        NewCommand = new RelayCommand(() => Select(_ui.Create()?.Id));
        EditCommand = new RelayCommand(() => Select(_ui.Edit(Selected!.Project)?.Id), () => Selected is not null && !IsRunning(Selected.Id));
        RunCommand = new RelayCommand(() => _ui.Run(Selected!.Id), () => Selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected?.Project is { } p && FolderOf(p) is { } f && Directory.Exists(f));
        _ui.Service.ProjectsChanged += OnProjectsChanged;
        Refresh();
    }

    public ObservableCollection<GrabberProjectRow> Projects { get; } = [];

    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public GrabberProjectRow? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public bool IsEmpty => Projects.Count == 0;

    public void Detach() => _ui.Service.ProjectsChanged -= OnProjectsChanged;

    private void OnProjectsChanged() => _dispatcher.InvokeAsync(Refresh);

    private bool IsRunning(long id) => _ui.Service.ActiveRun(id) is not null;

    public void Refresh()
    {
        var keep = Selected?.Id;
        Projects.Clear();
        foreach (var project in _ui.Service.Projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var state = IsRunning(project.Id) ? Strings.GrabberStateRunning
                : project.Schedule.Enabled ? Strings.GrabberStateScheduled : Strings.GrabberStateManual;
            var next = _ui.Service.NextRun(project) is { } n
                ? TimeZoneInfo.ConvertTimeFromUtc(n, TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture)
                : "";
            Projects.Add(new GrabberProjectRow(project, state, next));
        }
        Selected = Projects.FirstOrDefault(p => p.Id == keep) ?? Projects.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Select(long? id)
    {
        Refresh();
        if (id is { } wanted)
            Selected = Projects.FirstOrDefault(p => p.Id == wanted) ?? Selected;
    }

    private void Delete()
    {
        if (Selected is not { } row)
            return;
        if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.GrabberConfirmDelete, row.Name), Strings.GrabberProjectsTitle))
            return;
        _ui.Service.Delete(row.Id);
        Refresh();
    }

    private static string? FolderOf(GrabberProject project) =>
        project.Mode == GrabberMode.Mirror && project.Folder.Length > 0 ? project.Folder : null;

    private void OpenFolder()
    {
        if (Selected?.Project is { } project && FolderOf(project) is { } folder)
            _shell.OpenFolder(folder);
    }
}
