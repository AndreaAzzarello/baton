using System.ComponentModel;
using System.Runtime.CompilerServices;
using DownloadManager.Core;

namespace DownloadManager.App;

public sealed class DownloadItemViewModel : INotifyPropertyChanged
{
    private DownloadSnapshot _snapshot;
    private long _lastBytes;
    private double _speed;

    public DownloadItemViewModel(DownloadSnapshot snapshot)
    {
        _snapshot = snapshot;
        _lastBytes = snapshot.Downloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadSnapshot Snapshot => _snapshot;
    public long Id => _snapshot.Id;
    public string Name => _snapshot.DisplayName;
    public string Url => _snapshot.Url;
    public DownloadStatus Status => _snapshot.Status;
    public bool IsActive => Status == DownloadStatus.Downloading;
    public double Speed => _speed;

    public string SizeText => _snapshot.TotalSize is { } size ? Format.Bytes(size) : "—";

    public double Progress =>
        _snapshot.TotalSize is { } size and > 0 ? 100.0 * _snapshot.Downloaded / size
        : Status == DownloadStatus.Completed ? 100 : 0;

    public bool IsIndeterminate => IsActive && _snapshot.TotalSize is null;

    public string ProgressText => Status == DownloadStatus.Completed
        ? "100%"
        : _snapshot.TotalSize is > 0 ? $"{Progress:0.0}%" : Format.Bytes(_snapshot.Downloaded);

    public string SpeedText => IsActive ? Format.Speed(_speed) : "";

    public string EtaText => IsActive && _speed > 1 && _snapshot.TotalSize is { } size
        ? Format.Duration(TimeSpan.FromSeconds((size - _snapshot.Downloaded) / _speed))
        : "";

    public string ConnectionsText => IsActive && _snapshot.Connections > 0 ? _snapshot.Connections.ToString() : "";

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "In coda",
        DownloadStatus.Downloading => _snapshot.Downloaded == 0 ? "Connessione…" : "In download",
        DownloadStatus.Paused => "In pausa",
        DownloadStatus.Completed => "Completato",
        DownloadStatus.Failed => "Errore",
        _ => "",
    };

    public string StatusGlyph => Status switch
    {
        DownloadStatus.Queued => "",
        DownloadStatus.Downloading => "",
        DownloadStatus.Paused => "",
        DownloadStatus.Completed => "",
        DownloadStatus.Failed => "",
        _ => "",
    };

    public string ToolTip => _snapshot.Error is { } error ? $"{Url}\n\n{error}" : Url;

    public string AddedText => _snapshot.AddedUtc.ToLocalTime().ToString("g");

    public void Update(DownloadSnapshot snapshot, double elapsedSeconds)
    {
        var delta = snapshot.Downloaded - _lastBytes;
        _lastBytes = snapshot.Downloaded;
        if (snapshot.Status == DownloadStatus.Downloading && elapsedSeconds > 0 && delta >= 0)
            _speed = _speed == 0 ? delta / elapsedSeconds : 0.7 * _speed + 0.3 * (delta / elapsedSeconds);
        else
            _speed = 0;
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty); // refresh every binding
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
