using Baton.Core;

namespace Baton.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeClock(DateTime utcNow, TimeZoneInfo? zone = null) : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    public TimeZoneInfo Zone { get; } = zone ?? Rome;

    /// <summary>Central European time: summer time from the last Sunday of March to the last Sunday of October.</summary>
    public static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    public void Advance(TimeSpan by) => UtcNow += by;

    public static DateTime Local(int y, int mo, int d, int h, int mi = 0) =>
        ScheduleCalculator.ToUtc(new DateTime(y, mo, d, h, mi, 0), Rome);
}

/// <summary>Schedule windows, daylight saving changes and the start/stop events derived from them.</summary>
public sealed class QueueScheduleTests
{
    private static readonly TimeZoneInfo Rome = FakeClock.Rome;

    private static QueueSchedule Daily(int startH, int startM, int? stopH = null, int stopM = 0) => new()
    {
        Enabled = true,
        Start = new TimeOnly(startH, startM),
        Stop = stopH is { } h ? new TimeOnly(h, stopM) : null,
    };

    [Fact]
    public void Window_IsInLocalTime_AndCrossesMidnight()
    {
        var schedule = Daily(23, 0, 1, 30); // 23:00 → 01:30 the next day
        var inside = FakeClock.Local(2026, 6, 10, 0, 45);
        var window = ScheduleCalculator.Current(schedule, inside, Rome);

        Assert.NotNull(window);
        Assert.Equal(FakeClock.Local(2026, 6, 9, 23, 0), window.Value.StartUtc);
        Assert.Equal(FakeClock.Local(2026, 6, 10, 1, 30), window.Value.StopUtc);
        Assert.Null(ScheduleCalculator.Current(schedule, FakeClock.Local(2026, 6, 10, 1, 30), Rome)); // the stop is exclusive
        Assert.Equal(new DateTime(2026, 6, 9, 21, 0, 0), window.Value.StartUtc); // 23:00 in summer is 21:00 UTC
    }

    [Fact]
    public void Weekdays_AndSingleDate_SelectTheDays()
    {
        var weekdays = Daily(8, 0, 9);
        weekdays.Days = [DayOfWeek.Monday, DayOfWeek.Wednesday];
        Assert.NotNull(ScheduleCalculator.Current(weekdays, FakeClock.Local(2026, 10, 12, 8, 30), Rome)); // Monday
        Assert.Null(ScheduleCalculator.Current(weekdays, FakeClock.Local(2026, 10, 13, 8, 30), Rome)); // Tuesday
        Assert.Equal(FakeClock.Local(2026, 10, 14, 8, 0), ScheduleCalculator.NextStart(weekdays, FakeClock.Local(2026, 10, 12, 10, 0), Rome));

        var once = Daily(8, 0, 9);
        once.Mode = ScheduleMode.Once;
        once.Date = new DateOnly(2026, 12, 24);
        Assert.NotNull(ScheduleCalculator.Current(once, FakeClock.Local(2026, 12, 24, 8, 10), Rome));
        Assert.Null(ScheduleCalculator.Current(once, FakeClock.Local(2026, 12, 31, 8, 10), Rome));
        Assert.Equal(FakeClock.Local(2026, 12, 24, 8, 0), ScheduleCalculator.NextStart(once, FakeClock.Local(2026, 10, 1, 0, 0), Rome));
        Assert.Null(ScheduleCalculator.NextStart(once, FakeClock.Local(2026, 12, 25, 0, 0), Rome));
    }

    [Fact]
    public void SpringForward_StartInTheMissingHour_MovesToTheFirstValidMinute()
    {
        // 29 March 2026: clocks jump from 02:00 to 03:00, so 02:30 does not exist.
        var schedule = Daily(2, 30, 4);
        var window = ScheduleCalculator.WindowOn(schedule, new DateOnly(2026, 3, 29), Rome)!.Value;

        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0), window.StartUtc); // 03:00 summer time
        Assert.Equal(new DateTime(2026, 3, 29, 2, 0, 0), window.StopUtc); // 04:00 summer time
    }

    [Fact]
    public void FallBack_RepeatedHour_StartsOnce_AndTheWindowLastsItsRealLength()
    {
        // 25 October 2026: clocks go back from 03:00 to 02:00, so 02:00–03:00 happens twice.
        var schedule = Daily(1, 0, 4);
        var window = ScheduleCalculator.WindowOn(schedule, new DateOnly(2026, 10, 25), Rome)!.Value;
        Assert.Equal(TimeSpan.FromHours(4), window.StopUtc - window.StartUtc); // 01:00 → 04:00 is four real hours that night

        var repeated = Daily(2, 30, 5);
        var start = ScheduleCalculator.WindowOn(repeated, new DateOnly(2026, 10, 25), Rome)!.Value.StartUtc;
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0), start); // the first 02:30 (summer time)

        // Walking through the night minute by minute gives exactly one start and one stop.
        var tracker = new ScheduleTracker();
        var queue = new DownloadQueue { Id = 1, Schedule = repeated };
        var changes = new List<ScheduleTracker.Change>();
        for (var t = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc); t < new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc); t = t.AddMinutes(1))
        {
            var change = tracker.Evaluate(queue, t, Rome);
            if (change != ScheduleTracker.Change.None)
                changes.Add(change);
        }
        Assert.Equal([ScheduleTracker.Change.Start, ScheduleTracker.Change.Stop], changes);
    }

    [Fact]
    public void Tracker_StartsWhenEnteringLate_ButNotAfterTheStop()
    {
        var queue = new DownloadQueue { Id = 1, Schedule = Daily(9, 0, 10) };

        // App opened at 09:40, inside the window: the queue starts (without retrying failed downloads).
        Assert.Equal(ScheduleTracker.Change.Resume, new ScheduleTracker().Evaluate(queue, FakeClock.Local(2026, 10, 14, 9, 40), Rome));
        // App opened at 10:05, after the stop: nothing happens.
        Assert.Equal(ScheduleTracker.Change.None, new ScheduleTracker().Evaluate(queue, FakeClock.Local(2026, 10, 14, 10, 5), Rome));
    }

    [Fact]
    public void Tracker_AfterSleep_StopsIfTheWindowEnded_AndStartsIfItBegan()
    {
        var queue = new DownloadQueue { Id = 1, Schedule = Daily(9, 0, 10) };
        var tracker = new ScheduleTracker();
        Assert.Equal(ScheduleTracker.Change.None, tracker.Evaluate(queue, FakeClock.Local(2026, 10, 14, 8, 59), Rome));
        Assert.Equal(ScheduleTracker.Change.Start, tracker.Evaluate(queue, FakeClock.Local(2026, 10, 14, 9, 5), Rome));
        // Asleep from 09:10 to 11:00: the first evaluation after waking stops the queue.
        Assert.Equal(ScheduleTracker.Change.Stop, tracker.Evaluate(queue, FakeClock.Local(2026, 10, 14, 11, 0), Rome));

        // Asleep from 08:00 to 09:30: the window began during sleep, so the queue starts on waking.
        var other = new ScheduleTracker();
        Assert.Equal(ScheduleTracker.Change.None, other.Evaluate(queue, FakeClock.Local(2026, 10, 15, 8, 0), Rome));
        Assert.Equal(ScheduleTracker.Change.Start, other.Evaluate(queue, FakeClock.Local(2026, 10, 15, 9, 30), Rome));

        // Asleep through the whole window: no start on waking.
        var third = new ScheduleTracker();
        Assert.Equal(ScheduleTracker.Change.None, third.Evaluate(queue, FakeClock.Local(2026, 10, 16, 8, 0), Rome));
        Assert.Equal(ScheduleTracker.Change.None, third.Evaluate(queue, FakeClock.Local(2026, 10, 16, 12, 0), Rome));
    }

    [Fact]
    public void Repeat_StartsAgainEveryInterval_InsideTheWindow()
    {
        var schedule = Daily(9, 0, 11);
        schedule.RepeatMinutes = 30;
        var queue = new DownloadQueue { Id = 1, Schedule = schedule };
        var tracker = new ScheduleTracker();
        var starts = 0;
        for (var t = FakeClock.Local(2026, 10, 14, 8, 0); t < FakeClock.Local(2026, 10, 14, 12, 0); t = t.AddMinutes(1))
        {
            if (tracker.Evaluate(queue, t, Rome) == ScheduleTracker.Change.Start)
                starts++;
        }
        Assert.Equal(4, starts); // 09:00, 09:30, 10:00, 10:30
    }

    [Fact]
    public void Schedule_SurvivesSerialization_AndDamagedDataFallsBackToNone()
    {
        var schedule = Daily(7, 15, 8, 45);
        schedule.Mode = ScheduleMode.Once;
        schedule.Date = new DateOnly(2026, 11, 2);
        schedule.Days = [DayOfWeek.Friday];
        schedule.RepeatMinutes = 20;
        schedule.WakeToRun = true;

        var copy = DownloadQueue.DeserializeSchedule(DownloadQueue.SerializeSchedule(schedule));
        Assert.Equal((schedule.Start, schedule.Stop, schedule.Mode, schedule.Date, schedule.RepeatMinutes, schedule.WakeToRun),
            (copy.Start, copy.Stop, copy.Mode, copy.Date, copy.RepeatMinutes, copy.WakeToRun));
        Assert.Equal([DayOfWeek.Friday], copy.Days);
        Assert.False(DownloadQueue.DeserializeSchedule("{broken").Enabled);
    }

    [Fact]
    public void HourlyQuota_SlidesOverTheLastHour()
    {
        var quota = new HourlyQuota();
        var t0 = new DateTime(2026, 10, 14, 10, 0, 30, DateTimeKind.Utc);
        quota.Add(60L * 1024 * 1024, t0);
        quota.Add(40L * 1024 * 1024, t0.AddMinutes(30));

        Assert.Equal(100L * 1024 * 1024, quota.Used(t0.AddMinutes(45)));
        Assert.True(HourlyQuota.IsExceeded(quota.Used(t0.AddMinutes(45)), 100));
        Assert.False(HourlyQuota.IsExceeded(quota.Used(t0.AddMinutes(45)), 0)); // 0 = no quota
        // The first 60 MB leave the window about an hour later; a 50 MB quota is free again then.
        var free = quota.FreeAt(50, t0.AddMinutes(45))!.Value;
        Assert.InRange(free, t0.AddMinutes(60), t0.AddMinutes(62));
        Assert.Equal(40L * 1024 * 1024, quota.Used(t0.AddMinutes(62)));
        Assert.Equal(0, quota.Used(t0.AddMinutes(92)));
    }

    private sealed class FakePower : IPowerController
    {
        public List<string> Calls { get; } = [];
        public void Sleep() => Calls.Add("sleep");
        public void Hibernate() => Calls.Add("hibernate");
        public void Shutdown() => Calls.Add("shutdown");
    }

    [Theory]
    [InlineData(EndAction.Shutdown, true, "save,shutdown")]
    [InlineData(EndAction.Hibernate, true, "hibernate")]
    [InlineData(EndAction.Sleep, true, "sleep")]
    [InlineData(EndAction.CloseApp, true, "save,close")]
    [InlineData(EndAction.Shutdown, false, "")] // countdown cancelled: nothing happens
    [InlineData(EndAction.None, true, "")]
    public async Task EndAction_RunsOnlyAfterTheCountdown(EndAction action, bool confirmed, string expected)
    {
        var power = new FakePower();
        var countdowns = new List<TimeSpan>();
        var runner = new EndActionRunner(
            power,
            (_, length) => { countdowns.Add(length); return Task.FromResult(confirmed); },
            () => { power.Calls.Add("save"); return Task.CompletedTask; },
            () => power.Calls.Add("close"));

        var done = await runner.RunAsync(action);

        Assert.Equal(expected, string.Join(",", power.Calls));
        Assert.Equal(confirmed && action != EndAction.None, done);
        if (action != EndAction.None)
            Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(countdowns));
    }
}
