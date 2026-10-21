using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Core;

/// <summary>What happens when a queue has processed all its downloads.</summary>
public enum EndAction
{
    None,
    CloseApp,
    Sleep,
    Hibernate,
    Shutdown,
}

public enum QueueState
{
    /// <summary>Downloads in the queue are being started.</summary>
    Running,

    /// <summary>Stopped now, with a scheduled start ahead.</summary>
    Scheduled,

    Stopped,
}

public enum ScheduleMode
{
    /// <summary>Every week on the chosen days.</summary>
    Weekly,

    /// <summary>On a single date.</summary>
    Once,
}

/// <summary>When a queue starts and stops by itself. Times are wall-clock times in the local time zone.</summary>
public sealed class QueueSchedule
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(2, 0);

    /// <summary>Null: the window lasts until midnight (the queue still stops by itself when it has nothing left).</summary>
    public TimeOnly? Stop { get; set; }

    public ScheduleMode Mode { get; set; } = ScheduleMode.Weekly;
    public DayOfWeek[] Days { get; set; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    public DateOnly? Date { get; set; }

    /// <summary>Inside the window the queue is started again every so many minutes (failed downloads are retried); 0 = once.</summary>
    public int RepeatMinutes { get; set; }

    /// <summary>Ask Windows to wake the PC from sleep or hibernation for the start (a Windows timer; needs wake timers allowed).</summary>
    public bool WakeToRun { get; set; }

    public QueueSchedule Clone()
    {
        var copy = (QueueSchedule)MemberwiseClone();
        copy.Days = [.. Days];
        return copy;
    }
}

/// <summary>A named list of downloads processed in order, with its own limits and schedule.</summary>
public sealed class DownloadQueue
{
    public const string MainKey = "main";

    public long Id { get; set; }

    /// <summary>"main" for the main queue, which cannot be deleted; null for the user's queues.</summary>
    public string? Key { get; set; }

    public string Name { get; set; } = "";
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>Started by the user or by the schedule, and not stopped since.</summary>
    public bool Running { get; set; }

    public QueueSchedule Schedule { get; set; } = new();

    /// <summary>Further attempts for a download that fails while the queue runs it.</summary>
    public int RetryCount { get; set; }

    public int RetryDelaySeconds { get; set; } = 60;

    /// <summary>MB this queue may transfer in any 60 minutes; 0 = no quota.</summary>
    public long QuotaMBPerHour { get; set; }

    public EndAction EndAction { get; set; }
    public int SortOrder { get; set; }

    public bool IsMain => Key == MainKey;

    /// <summary>
    /// A queue with an end action or a schedule works in "runs": it stops by itself when it has nothing left.
    /// A plain queue stays started, so downloads added later begin right away (as the single queue always did).
    /// </summary>
    public bool StopsWhenDone => EndAction != EndAction.None || Schedule.Enabled;

    public override string ToString() => Name;

    public DownloadQueue Clone()
    {
        var copy = (DownloadQueue)MemberwiseClone();
        copy.Schedule = Schedule.Clone();
        return copy;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string SerializeSchedule(QueueSchedule schedule) => JsonSerializer.Serialize(schedule, Json);

    public static QueueSchedule DeserializeSchedule(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new QueueSchedule() : JsonSerializer.Deserialize<QueueSchedule>(json, Json) ?? new QueueSchedule();
        }
        catch (JsonException)
        {
            return new QueueSchedule(); // a damaged schedule is dropped, the queue itself survives
        }
    }
}

/// <summary>The state of a queue for the UI.</summary>
public sealed record QueueStatus(long QueueId, QueueState State, DateTime? NextStartUtc, int Count, int Active);
