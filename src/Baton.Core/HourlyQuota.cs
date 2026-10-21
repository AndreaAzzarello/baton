namespace Baton.Core;

/// <summary>
/// Bytes transferred in the last 60 minutes, counted per minute. A queue (or all downloads together) that
/// reached its quota waits until enough of the past hour has slid out of the window.
/// </summary>
public sealed class HourlyQuota
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly List<(DateTime Minute, long Bytes)> _minutes = []; // at most 61 entries
    private long _total;

    public void Add(long bytes, DateTime utcNow)
    {
        if (bytes <= 0)
            return;
        Expire(utcNow);
        var minute = new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (_minutes.Count > 0 && _minutes[^1].Minute == minute)
            _minutes[^1] = (minute, _minutes[^1].Bytes + bytes);
        else
            _minutes.Add((minute, bytes));
        _total += bytes;
    }

    public long Used(DateTime utcNow)
    {
        Expire(utcNow);
        return _total;
    }

    public static bool IsExceeded(long usedBytes, long quotaMB) => quotaMB > 0 && usedBytes >= quotaMB * 1024 * 1024;

    /// <summary>When usage will be below the quota again, or null if it already is.</summary>
    public DateTime? FreeAt(long quotaMB, DateTime utcNow)
    {
        if (!IsExceeded(Used(utcNow), quotaMB))
            return null;
        var remaining = _total;
        foreach (var (minute, bytes) in _minutes)
        {
            remaining -= bytes;
            if (!IsExceeded(remaining, quotaMB))
                return minute + Window + TimeSpan.FromMinutes(1);
        }
        return utcNow + Window;
    }

    private void Expire(DateTime utcNow)
    {
        // A minute bucket counts while any part of it is within the last hour.
        while (_minutes.Count > 0 && _minutes[0].Minute + Window + TimeSpan.FromMinutes(1) <= utcNow)
        {
            _total -= _minutes[0].Bytes;
            _minutes.RemoveAt(0);
        }
    }
}
