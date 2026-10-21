using System.Diagnostics;

namespace Baton.Core;

/// <summary>
/// Global token bucket shared by every connection. Callers may overdraw the bucket;
/// the debt is repaid by waiting, which keeps the long-run average at the limit.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    /// <summary>0 means unlimited.</summary>
    public long BytesPerSecond
    {
        get { lock (_lock) return _bytesPerSecond; }
        set
        {
            lock (_lock)
            {
                _bytesPerSecond = Math.Max(0, value);
                _tokens = 0;
                _lastTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var rate = _bytesPerSecond;
                if (rate <= 0)
                    return;

                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
                _lastTimestamp = now;
                var burst = rate / 4.0;
                _tokens = Math.Min(burst, _tokens + elapsed * rate);

                if (_tokens > 0)
                {
                    _tokens -= bytes;
                    return;
                }
                delay = TimeSpan.FromSeconds(-_tokens / rate + 0.001);
            }
            await Task.Delay(delay, ct);
        }
    }
}
