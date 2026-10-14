namespace DownloadManager.Core;

/// <summary>The current time and time zone; replaced in tests to simulate days, daylight saving changes and sleep.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    TimeZoneInfo Zone { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public TimeZoneInfo Zone => TimeZoneInfo.Local;
}

/// <summary>One occurrence of a schedule: [Start, Stop) in UTC.</summary>
public readonly record struct ScheduleWindow(DateTime StartUtc, DateTime StopUtc)
{
    public bool Contains(DateTime utc) => utc >= StartUtc && utc < StopUtc;
}

/// <summary>
/// Turns a schedule written in local wall-clock time into real instants. Times that do not exist (the hour
/// skipped when daylight saving starts) move forward to the first valid minute; times that occur twice (the
/// hour repeated when it ends) use their first occurrence, so a schedule never runs twice in one night.
/// </summary>
public static class ScheduleCalculator
{
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; zone.IsInvalidTime(local) && i < 24 * 60; i++)
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static bool RunsOn(QueueSchedule schedule, DateOnly date) =>
        schedule.Mode == ScheduleMode.Once ? schedule.Date == date : schedule.Days.Contains(date.DayOfWeek);

    /// <summary>The occurrence that starts on <paramref name="date"/>, if the schedule runs that day.</summary>
    public static ScheduleWindow? WindowOn(QueueSchedule schedule, DateOnly date, TimeZoneInfo zone)
    {
        if (!schedule.Enabled || !RunsOn(schedule, date))
            return null;
        var startLocal = date.ToDateTime(schedule.Start);
        var stopLocal = schedule.Stop is { } stop
            ? stop > schedule.Start ? date.ToDateTime(stop) : date.AddDays(1).ToDateTime(stop) // a stop before the start is on the next day
            : date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var start = ToUtc(startLocal, zone);
        var end = ToUtc(stopLocal, zone);
        return end > start ? new ScheduleWindow(start, end) : null;
    }

    /// <summary>The occurrence containing <paramref name="utcNow"/>, or null when the queue should not run now.</summary>
    public static ScheduleWindow? Current(QueueSchedule schedule, DateTime utcNow, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        // A window that crosses midnight started yesterday.
        foreach (var date in new[] { today, today.AddDays(-1) })
        {
            if (WindowOn(schedule, date, zone) is { } window && window.Contains(utcNow))
                return window;
        }
        return null;
    }

    public static DateTime? NextStart(QueueSchedule schedule, DateTime utcNow, TimeZoneInfo zone)
    {
        if (!schedule.Enabled)
            return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        for (var day = 0; day <= 8; day++)
        {
            if (WindowOn(schedule, today.AddDays(day), zone) is { } window && window.StartUtc > utcNow)
                return window.StartUtc;
        }
        if (schedule.Mode == ScheduleMode.Once && schedule.Date is { } date && date > today.AddDays(8))
            return WindowOn(schedule, date, zone)?.StartUtc;
        return null;
    }

    /// <summary>
    /// Which start the schedule is in: each window has one start, plus one every <see cref="QueueSchedule.RepeatMinutes"/>
    /// while it lasts. Null outside the windows. A change of value is a start event.
    /// </summary>
    public static (DateTime WindowStart, long Slot)? Slot(QueueSchedule schedule, DateTime utcNow, TimeZoneInfo zone)
    {
        if (Current(schedule, utcNow, zone) is not { } window)
            return null;
        var slot = schedule.RepeatMinutes > 0 ? (long)((utcNow - window.StartUtc).TotalMinutes / schedule.RepeatMinutes) : 0;
        return (window.StartUtc, slot);
    }
}

/// <summary>
/// Turns the schedule into start and stop events. It reacts to the state, not to a timer firing at the exact
/// minute, so it also works when the app starts late, after the PC wakes from sleep, or when the clock jumps:
/// a queue starts on entering a window (or a repeat slot) and stops on leaving it.
/// </summary>
public sealed class ScheduleTracker
{
    private readonly Dictionary<long, (DateTime WindowStart, long Slot)?> _last = [];

    public enum Change
    {
        None,

        /// <summary>The window (or a repeat slot) began while the app was watching.</summary>
        Start,

        /// <summary>
        /// The app started, or the schedule was changed, in the middle of a window: the queue starts, but failed
        /// downloads are not retried, so a restart (for example after the queue's own shutdown) cannot loop.
        /// </summary>
        Resume,

        Stop,
    }

    public Change Evaluate(DownloadQueue queue, DateTime utcNow, TimeZoneInfo zone)
    {
        var current = queue.Schedule.Enabled ? ScheduleCalculator.Slot(queue.Schedule, utcNow, zone) : null;
        var seen = _last.TryGetValue(queue.Id, out var previous);
        _last[queue.Id] = current;
        if (current == previous)
            return Change.None;
        if (current is not null)
            return seen ? Change.Start : Change.Resume; // entered a window or a new repeat slot, or first look inside one
        return previous is not null ? Change.Stop : Change.None;
    }

    public void Forget(long queueId) => _last.Remove(queueId);
}
