namespace Baton.Core.Integration;

/// <summary>A sliding one-minute budget of requests: <see cref="TryTake"/> is false once the limit is reached within the last minute.</summary>
public sealed class RateWindow(int perMinute, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly Queue<DateTime> _recent = new();
    private readonly object _lock = new();

    public bool TryTake()
    {
        var now = _clock();
        lock (_lock)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
                _recent.Dequeue();
            if (_recent.Count >= perMinute)
                return false;
            _recent.Enqueue(now);
            return true;
        }
    }
}
