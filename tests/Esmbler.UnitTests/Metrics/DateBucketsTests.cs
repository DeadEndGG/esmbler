using Esmbler.Kernel.Metrics;
using Esmbler.Sdk.Specs;

namespace Esmbler.UnitTests.Metrics;

/// <summary>REQ-WS-004: metric date windows and buckets align to local days, weeks and months across daylight-saving changes (ADR 0019).</summary>
public class DateBucketsTests
{
    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Weekly_buckets_start_on_local_mondays_and_cover_the_window()
    {
        var range = new MetricDateRangeSpec { Field = "resolved_at", Last = 8, Unit = MetricBuckets.Week };
        var now = Utc("2026-11-04T15:00:00Z"); // Wednesday
        var buckets = DateBuckets.Split(range, MetricBuckets.Week, NewYork, now);
        Assert.Equal(8, buckets.Count);
        Assert.Equal("2026-11-02", buckets[^1].Key);
        Assert.Equal("2026-09-14", buckets[0].Key);
        for (var i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(buckets[i - 1].End, buckets[i].Start);
        }

        // The week of 26 October contains the fall-back change: 169 hours.
        var fallBack = buckets.Single(b => b.Key == "2026-10-26");
        Assert.Equal(TimeSpan.FromHours(169), fallBack.End - fallBack.Start);
        Assert.Equal(Utc("2026-10-26T04:00:00Z"), fallBack.Start); // Monday 00:00 EDT
        Assert.Equal(Utc("2026-11-02T05:00:00Z"), fallBack.End);   // Monday 00:00 EST
    }

    [Fact]
    public void Day_and_month_windows_include_the_current_partial_unit()
    {
        var now = Utc("2026-03-08T12:00:00Z");
        var (start, end) = DateBuckets.Window(new MetricDateRangeSpec { Field = "created_at", Last = 2, Unit = MetricBuckets.Day }, NewYork, now);
        Assert.Equal(Utc("2026-03-07T05:00:00Z"), start); // Saturday 00:00 EST
        Assert.Equal(Utc("2026-03-09T04:00:00Z"), end);   // Monday 00:00 EDT: Sunday had 23 hours

        var months = DateBuckets.Split(new MetricDateRangeSpec { Field = "created_at", Last = 3, Unit = MetricBuckets.Month }, MetricBuckets.Month, TimeZoneInfo.Utc, now);
        Assert.Equal(["Jan 2026", "Feb 2026", "Mar 2026"], months.Select(m => m.Label));
    }
}
