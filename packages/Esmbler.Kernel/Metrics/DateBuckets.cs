using System.Globalization;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Metrics;

/// <summary>One date bucket of a metric: [Start, End) in UTC and its label in the metric's time zone.</summary>
public sealed record DateBucket(DateTimeOffset Start, DateTimeOffset End, string Label, string Key);

/// <summary>
/// Rolling date windows for metrics (ADR 0019): the last N days, weeks (Monday to Sunday) or months, aligned to their
/// local starts in a time zone, including the current, partial one. Local midnights are converted to UTC per bucket, so
/// a daylight-saving change makes a day 23 or 25 hours long rather than shifting every later bucket.
/// </summary>
public static class DateBuckets
{
    /// <summary>The window [start, end) for <paramref name="range"/> at <paramref name="now"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Window(MetricDateRangeSpec range, TimeZoneInfo zone, DateTimeOffset now)
    {
        var current = UnitStart(range.Unit, LocalDate(now, zone));
        var start = Shift(range.Unit, current, -(range.Last - 1));
        var end = Shift(range.Unit, current, 1);
        return (ToUtc(start, zone), ToUtc(end, zone));
    }

    /// <summary>The buckets of <paramref name="bucket"/> size covering the window, oldest first.</summary>
    public static IReadOnlyList<DateBucket> Split(MetricDateRangeSpec range, string bucket, TimeZoneInfo zone, DateTimeOffset now)
    {
        var (windowStart, windowEnd) = Window(range, zone, now);
        var day = UnitStart(bucket, LocalDate(windowStart, zone));
        var buckets = new List<DateBucket>();
        while (true)
        {
            var next = Shift(bucket, day, 1);
            var start = ToUtc(day, zone);
            if (start >= windowEnd)
            {
                break;
            }

            var end = ToUtc(next, zone);
            buckets.Add(new DateBucket(start < windowStart ? windowStart : start, end > windowEnd ? windowEnd : end, Label(bucket, day), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            day = next;
        }

        return buckets;
    }

    public static TimeZoneInfo Zone(MetricDateRangeSpec range, SpecCatalog config, string? viewerZone)
    {
        var id = range.Calendar is { } calendar && config.FindBusinessCalendar(calendar) is { } cal ? cal.TimeZone : range.TimeZone ?? viewerZone ?? "UTC";
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static DateOnly UnitStart(string unit, DateOnly day) => unit switch
    {
        MetricBuckets.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        MetricBuckets.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    private static DateOnly Shift(string unit, DateOnly day, int count) => unit switch
    {
        MetricBuckets.Week => day.AddDays(7 * count),
        MetricBuckets.Month => day.AddMonths(count),
        _ => day.AddDays(count),
    };

    /// <summary>Local midnight of a day in UTC (midnight inside a spring-forward gap moves to the first valid minute).</summary>
    private static DateTimeOffset ToUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        for (var guard = 0; zone.IsInvalidTime(local) && guard < 240; guard++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static string Label(string bucket, DateOnly day) => bucket switch
    {
        MetricBuckets.Month => day.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        MetricBuckets.Week => "Week of " + day.ToString("MMM d", CultureInfo.InvariantCulture),
        _ => day.ToString("MMM d", CultureInfo.InvariantCulture),
    };
}
