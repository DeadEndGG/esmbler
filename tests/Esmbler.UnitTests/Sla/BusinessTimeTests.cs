using Esmbler.Kernel.Sla;
using Esmbler.Sdk.Specs;

namespace Esmbler.UnitTests.Sla;

/// <summary>REQ-BIZ-005, REQ-ACC-13 (DST part): business-time arithmetic across weekends, holidays, time zones and daylight-saving changes (ADR 0018).</summary>
public class BusinessTimeTests
{
    private static BusinessCalendarSpec NewYork(params string[] holidays) => new()
    {
        Id = "tenant__ny",
        Label = "New York",
        TimeZone = "America/New_York",
        Week = [.. new[] { "mon", "tue", "wed", "thu", "fri" }.Select(d => new WorkingHoursSpec { Day = d, Start = "09:00", End = "17:00" })],
        Holidays = [.. holidays.Select(h => new HolidaySpec { Date = h })],
    };

    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Elapsed_time_runs_around_the_clock()
    {
        var time = BusinessTime.For(null);
        Assert.Equal(Utc("2026-03-08T05:00:00Z"), time.Add(Utc("2026-03-07T05:00:00Z"), TimeSpan.FromHours(24)));
        Assert.Equal(TimeSpan.FromHours(3), time.Between(Utc("2026-03-07T05:00:00Z"), Utc("2026-03-07T08:00:00Z")));
    }

    [Fact]
    public void Business_time_skips_nights_weekends_and_holidays()
    {
        var time = BusinessTime.For(NewYork("2026-11-26"));

        // Friday 16:00 New York (EST, UTC-5) + 2 business hours = Monday 10:00.
        Assert.Equal(Utc("2026-11-30T15:00:00Z"), time.Add(Utc("2026-11-27T21:00:00Z"), TimeSpan.FromHours(2)));

        // Wednesday 16:00 + 2 h: Thanksgiving Thursday is a holiday, so Friday 10:00.
        Assert.Equal(Utc("2026-11-27T15:00:00Z"), time.Add(Utc("2026-11-25T21:00:00Z"), TimeSpan.FromHours(2)));

        // Starting before opening counts from 09:00; starting on Saturday counts from Monday 09:00.
        Assert.Equal(Utc("2026-11-30T15:00:00Z"), time.Add(Utc("2026-11-28T15:00:00Z"), TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromHours(17), time.Between(Utc("2026-11-25T14:00:00Z"), Utc("2026-11-30T15:00:00Z")));
    }

    [Fact]
    public void Working_hours_follow_the_wall_clock_across_daylight_saving_changes()
    {
        var time = BusinessTime.For(NewYork());

        // Friday 6 March 2026 is EST (UTC-5); clocks spring forward on Sunday 8 March; Monday 9 March is EDT (UTC-4).
        // Friday 16:00 EST + 2 business hours = Monday 10:00 EDT = 14:00 UTC (not 15:00 UTC).
        Assert.Equal(Utc("2026-03-09T14:00:00Z"), time.Add(Utc("2026-03-06T21:00:00Z"), TimeSpan.FromHours(2)));

        // Clocks fall back on Sunday 1 November 2026: Friday 16:00 EDT + 2 h = Monday 10:00 EST = 15:00 UTC.
        Assert.Equal(Utc("2026-11-02T15:00:00Z"), time.Add(Utc("2026-10-30T20:00:00Z"), TimeSpan.FromHours(2)));

        // A full working day is 8 hours on both sides of the change.
        Assert.Equal(TimeSpan.FromHours(16), time.Between(Utc("2026-03-06T14:00:00Z"), Utc("2026-03-09T21:00:00Z")));
    }

    [Fact]
    public void A_working_day_that_crosses_a_change_keeps_its_local_hours()
    {
        // A 24/7 calendar in local time loses an hour on the spring-forward day and gains one in the autumn.
        var always = BusinessTime.For(new BusinessCalendarSpec
        {
            Id = "tenant__always",
            Label = "Always",
            TimeZone = "Europe/London",
            Week = [.. Weekdays.All.Select(d => new WorkingHoursSpec { Day = d, Start = "00:00", End = "24:00" })],
        });
        Assert.Equal(TimeSpan.FromHours(23), always.Between(Utc("2026-03-29T00:00:00Z"), Utc("2026-03-29T23:00:00Z")));
        Assert.Equal(TimeSpan.FromHours(25), always.Between(Utc("2026-10-24T23:00:00Z"), Utc("2026-10-26T00:00:00Z")));

        // An interval starting inside the spring-forward gap (01:30 London does not exist) starts at 02:00 BST.
        var night = BusinessTime.For(new BusinessCalendarSpec
        {
            Id = "tenant__night",
            Label = "Night",
            TimeZone = "Europe/London",
            Week = [new WorkingHoursSpec { Day = "sun", Start = "01:30", End = "03:00" }],
        });
        var first = night.Intervals(Utc("2026-03-28T12:00:00Z")).First();
        Assert.Equal((Utc("2026-03-29T01:00:00Z"), Utc("2026-03-29T02:00:00Z")), first);
    }

    [Fact]
    public void A_calendar_without_working_time_ahead_has_no_deadline()
    {
        var closed = BusinessTime.For(new BusinessCalendarSpec { Id = "tenant__closed", Label = "Closed", TimeZone = "UTC", Week = [] });
        Assert.Null(closed.Add(Utc("2026-01-01T00:00:00Z"), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Intervals_that_meet_at_an_ambiguous_fall_back_time_are_counted_once()
    {
        // Sunday 1 November 2026 in New York: 01:00–02:00 happens twice. 00:30–01:30 then 01:30–03:00 local.
        var night = BusinessTime.For(new BusinessCalendarSpec
        {
            Id = "tenant__night",
            Label = "Night",
            TimeZone = "America/New_York",
            Week = [new WorkingHoursSpec { Day = "sun", Start = "00:30", End = "01:30" }, new WorkingHoursSpec { Day = "sun", Start = "01:30", End = "03:00" }],
        });

        // 00:30 EDT (04:30Z) to 03:00 EST (08:00Z) is 3.5 real hours.
        Assert.Equal(TimeSpan.FromHours(3.5), night.Between(Utc("2026-11-01T00:00:00Z"), Utc("2026-11-01T12:00:00Z")));
    }
}
