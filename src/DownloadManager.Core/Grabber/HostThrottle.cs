using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace DownloadManager.Core.Grabber;

/// <summary>
/// Spaces the requests to each host: at most <see cref="RequestsPerSecond"/> start per second (slower when the site's
/// robots.txt asks for a crawl delay), and none at all while a host that answered 429 or 503 asked to wait.
/// </summary>
public sealed class HostThrottle(double requestsPerSecond)
{
    private readonly Dictionary<string, long> _next = new(StringComparer.OrdinalIgnoreCase); // in Stopwatch ticks
    private readonly Dictionary<string, TimeSpan> _interval = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double RequestsPerSecond { get; } = Math.Clamp(requestsPerSecond, 0.05, 50);

    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(1 / RequestsPerSecond);

    public static string KeyOf(Uri url) => $"{url.Host}:{url.Port}";

    /// <summary>A longer interval for one host (its robots.txt Crawl-delay), never a shorter one.</summary>
    public void SetCrawlDelay(Uri url, double seconds)
    {
        var interval = TimeSpan.FromSeconds(Math.Min(seconds, 60));
        lock (_next)
        {
            if (interval > DefaultInterval)
                _interval[KeyOf(url)] = interval;
        }
    }

    /// <summary>Waits for this host's next free slot and takes it.</summary>
    public async Task WaitAsync(Uri url, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_next)
        {
            var key = KeyOf(url);
            var now = _clock.Elapsed.Ticks;
            var interval = _interval.TryGetValue(key, out var own) ? own : DefaultInterval;
            var slot = _next.TryGetValue(key, out var next) && next > now ? next : now;
            _next[key] = slot + interval.Ticks;
            wait = TimeSpan.FromTicks(slot - now);
        }
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }

    /// <summary>Nothing goes to the host before <paramref name="delay"/> has passed (Retry-After).</summary>
    public void Pause(Uri url, TimeSpan delay)
    {
        lock (_next)
        {
            var key = KeyOf(url);
            var until = _clock.Elapsed.Ticks + delay.Ticks;
            if (!_next.TryGetValue(key, out var next) || next < until)
                _next[key] = until;
        }
    }

    /// <summary>
    /// How long a 429 or 503 response asks to wait (Retry-After in seconds or as a date); null when it does not say.
    /// </summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
            return date > now ? date - now : TimeSpan.Zero;
        // Some servers send a value the typed header cannot parse (e.g. "1.5").
        // "1e300" or "Infinity" would overflow a TimeSpan: anything beyond a day is simply "a day".
        if (response.Headers.TryGetValues("Retry-After", out var raw)
            && double.TryParse(raw.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return double.IsFinite(seconds) && seconds < 86_400 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromDays(1);
        return null;
    }

    public static bool IsBusy(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
