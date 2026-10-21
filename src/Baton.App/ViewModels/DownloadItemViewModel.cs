using Baton.App.Mvvm;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>One row of the download list. Speed and time left are measured between refreshes.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadSnapshot _snapshot;
    private long _lastBytes;
    private double _speed;
    private string _categoryName;
    private string _queueName;

    public DownloadItemViewModel(DownloadSnapshot snapshot, string categoryName, string queueName)
    {
        _snapshot = snapshot;
        _lastBytes = snapshot.Downloaded;
        _categoryName = categoryName;
        _queueName = queueName;
    }

    public DownloadSnapshot Snapshot => _snapshot;
    public long Id => _snapshot.Id;
    public string Name => _snapshot.DisplayName;
    public string Url => _snapshot.Url;
    public string Directory => _snapshot.Directory;
    public DownloadStatus Status => _snapshot.Status;
    public long? CategoryId => _snapshot.CategoryId;
    public string CategoryName => _categoryName;
    public long? QueueId => _snapshot.QueueId;
    public string QueueName => _queueName;
    public int SortOrder => _snapshot.SortOrder;
    public bool IsActive => Status == DownloadStatus.Downloading;
    public bool IsCompleted => Status == DownloadStatus.Completed;
    public double Speed => _speed;
    public int Connections => IsActive ? _snapshot.Connections : 0;
    public DateTime AddedUtc => _snapshot.AddedUtc;
    public DateTime? CompletedUtc => _snapshot.CompletedUtc;

    /// <summary>Unknown sizes sort before every known one.</summary>
    public long SizeSort => _snapshot.TotalSize ?? -1;

    public string SizeText => _snapshot.TotalSize is { } size ? Format.Bytes(size) : "—";

    public double Progress =>
        _snapshot.TotalSize is { } size and > 0 ? 100.0 * _snapshot.Downloaded / size
        : Status == DownloadStatus.Completed ? 100 : 0;

    public bool IsIndeterminate => IsActive && _snapshot.TotalSize is null;

    public string ProgressText => Status == DownloadStatus.Completed
        ? "100%"
        : _snapshot.TotalSize is > 0 ? $"{Progress:0.0}%" : Format.Bytes(_snapshot.Downloaded);

    public string SpeedText => IsActive ? Format.Speed(_speed) : "";

    public TimeSpan? Remaining => IsActive && _speed > 1 && _snapshot.TotalSize is { } size
        ? TimeSpan.FromSeconds((size - _snapshot.Downloaded) / _speed)
        : null;

    /// <summary>Downloads without an estimate sort after every one with an estimate.</summary>
    public double RemainingSort => Remaining?.TotalSeconds ?? double.MaxValue;

    public string EtaText => Format.Duration(Remaining);

    public string ConnectionsText => Connections > 0 ? Connections.ToString() : "";

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => _snapshot.Error is not null ? Strings.StatusWaiting : Strings.StatusQueued, // retry or quota: see the tooltip
        // No connection open yet (first request, or retrying after a network error).
        DownloadStatus.Downloading => _snapshot.Connections == 0 ? Strings.StatusConnecting : Strings.StatusDownloading,
        DownloadStatus.Paused => Strings.StatusPaused,
        DownloadStatus.Completed => _snapshot.Check switch
        {
            { Pending: true } => Strings.StatusCompletedChecking,
            { HasProblem: true } => Strings.StatusCompletedProblem, // a hash that differs, or a threat: see the tooltip
            _ => _snapshot.Error is not null ? Strings.StatusCompletedWithNote : Strings.StatusCompleted, // e.g. a stream saved without ffmpeg
        },
        DownloadStatus.Failed => Strings.StatusFailed,
        _ => "",
    };

    /// <summary>Running first, then waiting, paused, failed and finished.</summary>
    public int StatusSort => Status switch
    {
        DownloadStatus.Downloading => 0,
        DownloadStatus.Queued => 1,
        DownloadStatus.Paused => 2,
        DownloadStatus.Failed => 3,
        _ => 4,
    };

    public string StatusGlyph => Status switch
    {
        DownloadStatus.Queued => "",
        DownloadStatus.Downloading => "",
        DownloadStatus.Paused => "",
        DownloadStatus.Completed => _snapshot.Check?.HasProblem == true ? "" : "",
        DownloadStatus.Failed => "",
        _ => "",
    };

    public string ToolTip => string.Join("\n\n", new[] { Url, _snapshot.Error, Core.PostDownload.PostCheck.Describe(_snapshot.Check) }.Where(t => !string.IsNullOrEmpty(t)));

    public string AddedText => _snapshot.AddedUtc.ToLocalTime().ToString("g");

    public string CompletedText => _snapshot.CompletedUtc?.ToLocalTime().ToString("g") ?? "";

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => $"{Name}, {StatusText}, {ProgressText}";

    public void Update(DownloadSnapshot snapshot, string categoryName, string queueName, double elapsedSeconds)
    {
        var delta = snapshot.Downloaded - _lastBytes;
        _lastBytes = snapshot.Downloaded;
        if (snapshot.Status == DownloadStatus.Downloading && elapsedSeconds > 0 && delta >= 0)
            _speed = _speed == 0 ? delta / elapsedSeconds : 0.7 * _speed + 0.3 * (delta / elapsedSeconds);
        else
            _speed = 0;
        _snapshot = snapshot;
        _categoryName = categoryName;
        _queueName = queueName;
        OnPropertyChanged(string.Empty);
    }
}
