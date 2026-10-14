using System.Globalization;
using System.Windows.Input;
using DownloadManager.App.Mvvm;
using DownloadManager.App.Services;
using DownloadManager.Core;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.ViewModels;

/// <summary>One connection's byte range in the progress window.</summary>
public sealed record SegmentRow(int Number, string Range, string Downloaded, string State, double Percent)
{
    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => $"{Number}: {Range}, {Downloaded}, {State}";
}

/// <summary>
/// The progress window of one download: the list row (updated by the main window) supplies speed and time left;
/// the byte ranges and active connections are read from the service on every refresh.
/// </summary>
public sealed class ProgressViewModel : ObservableObject
{
    private readonly DownloadService _service;
    private readonly IShellService _shell;
    private IReadOnlyList<SegmentView> _segments = [];
    private IReadOnlyList<SegmentRow> _rows = [];

    public ProgressViewModel(DownloadItemViewModel item, DownloadService service, IShellService shell)
    {
        Item = item;
        _service = service;
        _shell = shell;
        ToggleCommand = new RelayCommand(Toggle, () => !Item.IsCompleted);
        OpenCommand = new RelayCommand(() => _shell.OpenFile(Item.Snapshot.FinalPath!), () => Item.IsCompleted && Item.Snapshot.FinalPath is not null);
        RevealCommand = new RelayCommand(() => _shell.Reveal(Item.Snapshot.FinalPath, Item.Directory));
        Refresh();
    }

    public DownloadItemViewModel Item { get; }

    public ICommand ToggleCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RevealCommand { get; }

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.ProgressTitle, Item.ProgressText, Item.Name);

    public string ToggleText => Item.Status is DownloadStatus.Downloading or DownloadStatus.Queued ? Strings.ActionPause : Strings.ActionResume;

    public IReadOnlyList<SegmentView> Segments => _segments;
    public IReadOnlyList<SegmentRow> Rows => _rows;
    public long TotalSize => Item.Snapshot.TotalSize ?? 0;

    public string DownloadedText => Item.Snapshot.TotalSize is { } total
        ? string.Format(CultureInfo.CurrentCulture, Strings.ProgressDownloadedOf, Format.Bytes(Item.Snapshot.Downloaded), Format.Bytes(total), Item.ProgressText)
        : Format.Bytes(Item.Snapshot.Downloaded);

    public string ResumableText => Item.Snapshot.SupportsRanges ? Strings.Yes : Strings.ResumeNotSupported;

    public string ConnectionsText => string.Format(CultureInfo.CurrentCulture, Strings.ProgressConnections, Item.Connections, _segments.Count);

    /// <summary>Called by the window's timer.</summary>
    public void Refresh()
    {
        _segments = _service.GetSegments(Item.Id);
        _rows = _segments.Select((s, i) => new SegmentRow(
            i + 1,
            s.End >= 0 ? $"{Format.Bytes(s.Start)} – {Format.Bytes(s.End + 1)}" : $"{Format.Bytes(s.Start)} – ?",
            Format.Bytes(s.Written),
            s.Active ? Strings.SegmentActive : s.End >= 0 && s.Start + s.Written > s.End ? Strings.SegmentDone : Strings.SegmentWaiting,
            s.End >= s.Start ? 100.0 * s.Written / (s.End - s.Start + 1) : 0)).ToList();
        OnPropertyChanged(string.Empty);
    }

    private async void Toggle()
    {
        if (Item.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
            await _service.PauseAsync(Item.Id);
        else
            _service.Start(Item.Id);
        Refresh();
    }
}
