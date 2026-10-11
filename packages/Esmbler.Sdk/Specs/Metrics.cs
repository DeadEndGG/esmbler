namespace Esmbler.Sdk.Specs;

/// <summary>
/// A reusable metric definition (REQ-WS-004, ADR 0019): which records it counts (an entity and a filter in the list
/// filter language), what it measures, optionally grouped and limited to a date range, and who may see it. Dashboards
/// and the explorer evaluate the same definition through the same query compiler, so a figure and the list it drills
/// into always agree for the same viewer.
/// </summary>
public sealed class MetricSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    public required string Entity { get; set; }

    /// <summary>Records counted: an expression over <c>record</c> and <c>user</c> (the list filter language). Null counts every readable record.</summary>
    public string? Filter { get; set; }

    public MetricMeasureSpec Measure { get; set; } = new();

    public MetricGroupSpec? GroupBy { get; set; }

    public MetricDateRangeSpec? DateRange { get; set; }

    /// <summary>Only records with an SLA timer of this policy (and target) in a state: breached or at risk.</summary>
    public MetricSlaSpec? Sla { get; set; }

    /// <summary>
    /// Counts alerts about the entity's records instead of the records (time to acknowledge, time to resolve). The
    /// filter still applies to the record each alert is about; measures, date ranges and groups use alert fields
    /// (<see cref="MetricAlertFields"/>).
    /// </summary>
    public MetricAlertSpec? Alerts { get; set; }

    /// <summary>Roles that see the figure; empty means everyone who can read the entity. Never widens record access.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Display hint: <c>number</c> (default), <c>duration</c> or <c>percent</c>.</summary>
    public string? Format { get; set; }
}

/// <summary>What a metric computes over its records.</summary>
public sealed class MetricMeasureSpec
{
    /// <summary><c>count</c> (default), <c>sum</c>, <c>avg</c>, <c>min</c>, <c>max</c> (of a number field) or <c>duration</c> (average time from one instant to another).</summary>
    public string Kind { get; set; } = MetricMeasures.Count;

    /// <summary>Number field for sum, avg, min and max.</summary>
    public string? Field { get; set; }

    /// <summary>Duration start: a date_time field or <c>created_at</c>.</summary>
    public string? From { get; set; }

    /// <summary>Duration end: a date_time field or <c>updated_at</c>.</summary>
    public string? To { get; set; }

    /// <summary>Duration unit: <c>minutes</c>, <c>hours</c> (default) or <c>days</c>.</summary>
    public string Unit { get; set; } = "hours";
}

/// <summary>Splits a metric into groups, by a choice, user, group or toggle field, or by date buckets.</summary>
public sealed class MetricGroupSpec
{
    public required string Field { get; set; }

    /// <summary><c>day</c>, <c>week</c> or <c>month</c> for date fields (needs a date range).</summary>
    public string? Bucket { get; set; }

    /// <summary>At most this many groups, largest first (1–25).</summary>
    public int Limit { get; set; } = 12;

    /// <summary>Include records with no value as their own group.</summary>
    public bool IncludeEmpty { get; set; } = true;
}

/// <summary>A rolling window: the last <see cref="Last"/> days, weeks or months on a date field, aligned to their starts.</summary>
public sealed class MetricDateRangeSpec
{
    public required string Field { get; set; }

    public required int Last { get; set; }

    /// <summary><c>day</c> (default), <c>week</c> or <c>month</c>.</summary>
    public string Unit { get; set; } = "day";

    /// <summary>Business calendar whose time zone aligns the window; otherwise <see cref="TimeZone"/> or UTC.</summary>
    public string? Calendar { get; set; }

    public string? TimeZone { get; set; }
}

/// <summary>Restricts a metric to records whose SLA timer is in a state.</summary>
public sealed class MetricSlaSpec
{
    public required string Policy { get; set; }

    /// <summary>A target of the policy; null means any target.</summary>
    public string? Target { get; set; }

    /// <summary>
    /// <c>breached</c>: a breach was recorded (and stays recorded). <c>at_risk</c>: the timer is still running or paused,
    /// its warning was recorded and it has not breached (see <see cref="MetricSlaStates"/>).
    /// </summary>
    public string State { get; set; } = MetricSlaStates.Breached;
}

public static class MetricSlaStates
{
    public const string Breached = "breached";
    public const string AtRisk = "at_risk";

    public static IReadOnlyList<string> All { get; } = [Breached, AtRisk];
}

/// <summary>Which alerts an alert metric counts: alerts about records of the metric's entity, never suppressed ones.</summary>
public sealed class MetricAlertSpec
{
    /// <summary>Alert rules counted; empty means every rule.</summary>
    public List<string> Rules { get; set; } = [];

    /// <summary>Only alerts at least this severe (info, low, medium, high, critical).</summary>
    public string? MinSeverity { get; set; }
}

/// <summary>Fields an alert metric measures, dates and groups by.</summary>
public static class MetricAlertFields
{
    public const string OpenedAt = "opened_at";
    public const string AcknowledgedAt = "acknowledged_at";
    public const string ResolvedAt = "resolved_at";
    public const string Severity = "severity";
    public const string Rule = "rule";

    /// <summary>Instants: duration ends, date ranges and date buckets.</summary>
    public static IReadOnlyList<string> Times { get; } = [OpenedAt, AcknowledgedAt, ResolvedAt];

    /// <summary>Groups without a bucket.</summary>
    public static IReadOnlyList<string> Groups { get; } = [Severity, Rule];
}

public static class MetricMeasures
{
    public const string Count = "count";
    public const string Sum = "sum";
    public const string Avg = "avg";
    public const string Min = "min";
    public const string Max = "max";
    public const string Duration = "duration";

    public static IReadOnlyList<string> All { get; } = [Count, Sum, Avg, Min, Max, Duration];
}

public static class MetricBuckets
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static IReadOnlyList<string> All { get; } = [Day, Week, Month];
}
