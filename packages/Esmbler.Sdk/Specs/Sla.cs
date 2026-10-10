namespace Esmbler.Sdk.Specs;

/// <summary>
/// Business hours (ADR 0018): working intervals per weekday in one IANA time zone, minus holidays. Intervals are wall
/// clock times, so a daylight-saving change moves them with the clock (08:00 stays 08:00).
/// </summary>
public sealed class BusinessCalendarSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    /// <summary>IANA time zone, e.g. Europe/London or America/New_York.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Working intervals; a weekday without an interval is not worked. Several intervals per day are allowed (lunch breaks).</summary>
    public List<WorkingHoursSpec> Week { get; set; } = [];

    /// <summary>Whole days off (local dates), e.g. public holidays.</summary>
    public List<HolidaySpec> Holidays { get; set; } = [];
}

/// <summary>One working interval on a weekday, in the calendar's local time.</summary>
public sealed class WorkingHoursSpec
{
    /// <summary><c>mon</c>, <c>tue</c>, <c>wed</c>, <c>thu</c>, <c>fri</c>, <c>sat</c> or <c>sun</c>.</summary>
    public required string Day { get; set; }

    /// <summary>Start, <c>HH:mm</c> (local).</summary>
    public required string Start { get; set; }

    /// <summary>End, <c>HH:mm</c> (local), after the start; <c>24:00</c> means midnight at the end of the day.</summary>
    public required string End { get; set; }
}

public sealed class HolidaySpec
{
    /// <summary>Local date, <c>YYYY-MM-DD</c>.</summary>
    public required string Date { get; set; }

    public string? Label { get; set; }
}

public static class Weekdays
{
    public static IReadOnlyList<string> All { get; } = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static DayOfWeek ToDayOfWeek(string day) => day switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        "sun" => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Unknown weekday."),
    };
}

/// <summary>
/// An SLA policy (ADR 0018): measured targets (for example response and resolution) on the records of one entity.
/// Each target is a timer that starts, pauses, resumes and stops on conditions over the record, with a goal chosen by
/// condition (typically by priority) and measured in business hours of a calendar or in elapsed time. Running timers
/// keep a snapshot of the target, goal and calendar they started with.
/// </summary>
public sealed class SlaPolicySpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    public required string Entity { get; set; }

    /// <summary>Condition over <c>record</c>; when false for a record the policy does not apply to it. Null applies to every record.</summary>
    public string? When { get; set; }

    public bool Enabled { get; set; } = true;

    public List<SlaTargetSpec> Targets { get; set; } = [];

    public SlaTargetSpec? FindTarget(string id) => Targets.Find(t => t.Id == id);
}

/// <summary>One measured target of an SLA policy.</summary>
public sealed class SlaTargetSpec
{
    /// <summary>snake_case, unique within the policy, e.g. <c>response</c> or <c>resolution</c>.</summary>
    public required string Id { get; set; }

    public required string Label { get; set; }

    /// <summary>Condition that starts the timer; null starts it when the record is created.</summary>
    public string? Start { get; set; }

    /// <summary>Condition that stops the timer (target met if within the goal), e.g. <c>record.status in ['resolved', 'closed']</c>.</summary>
    public required string Stop { get; set; }

    /// <summary>Condition while which the clock does not run, e.g. <c>record.status == 'on_hold'</c>.</summary>
    public string? Pause { get; set; }

    /// <summary>What happens when a stopped timer's stop condition becomes false again (a reopened record): <c>resume</c> (default), <c>restart</c> or <c>none</c>.</summary>
    public string OnReopen { get; set; } = SlaReopen.Resume;

    /// <summary>Business calendar ID; null measures elapsed time around the clock.</summary>
    public string? Calendar { get; set; }

    /// <summary>Goals in order; the first whose condition holds when the timer starts applies. A goal without a condition always applies.</summary>
    public List<SlaGoalSpec> Goals { get; set; } = [];

    /// <summary>Publish a warning when this share of the goal has elapsed (1–99); null for no warning.</summary>
    public int? WarnAtPercent { get; set; } = 75;
}

/// <summary>A goal duration for the records its condition selects.</summary>
public sealed class SlaGoalSpec
{
    /// <summary>Condition over <c>record</c>, e.g. <c>record.priority == 'p1'</c>; null always applies.</summary>
    public string? When { get; set; }

    /// <summary>Goal in minutes of the target's calendar (business minutes) or elapsed minutes.</summary>
    public required int Minutes { get; set; }

    /// <summary>Overrides the target's calendar for this goal: a calendar ID, or <c>24x7</c> for elapsed time.</summary>
    public string? Calendar { get; set; }
}

public static class SlaReopen
{
    public const string Resume = "resume";
    public const string Restart = "restart";
    public const string None = "none";

    public static IReadOnlyList<string> All { get; } = [Resume, Restart, None];
}

/// <summary>Calendar value that means elapsed time around the clock.</summary>
public static class SlaCalendars
{
    public const string AroundTheClock = "24x7";
}
