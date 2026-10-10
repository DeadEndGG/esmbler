using System.Globalization;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Sla;

/// <summary>
/// Business-time arithmetic over a <see cref="BusinessCalendarSpec"/> (ADR 0018, REQ-BIZ-005), or elapsed time around
/// the clock when there is no calendar. Working intervals are local wall-clock times converted to UTC per day, so a
/// daylight-saving change keeps 08:00 at 08:00: a day that loses an hour inside working hours has one hour less, a
/// day that gains one has one more. A local time that does not exist (spring forward) moves to the first valid
/// minute after the gap; an ambiguous one (fall back) starts at its first occurrence and ends at its second.
/// </summary>
public sealed class BusinessTime
{
    /// <summary>How far ahead a search looks before giving up (a calendar with no working time).</summary>
    private const int MaxDays = 3 * 366;

    private readonly TimeZoneInfo? _zone;
    private readonly Dictionary<DayOfWeek, List<(TimeSpan Start, TimeSpan End)>> _week = [];
    private readonly HashSet<DateOnly> _holidays = [];

    private BusinessTime(BusinessCalendarSpec? calendar)
    {
        if (calendar is null)
        {
            return;
        }

        _zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone);
        foreach (var hours in calendar.Week)
        {
            var day = Weekdays.ToDayOfWeek(hours.Day);
            if (!_week.TryGetValue(day, out var list))
            {
                _week[day] = list = [];
            }

            list.Add((ParseTime(hours.Start), ParseTime(hours.End)));
        }

        foreach (var list in _week.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        foreach (var holiday in calendar.Holidays)
        {
            _holidays.Add(DateOnly.ParseExact(holiday.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Business time on <paramref name="calendar"/>, or elapsed time when it is null.</summary>
    public static BusinessTime For(BusinessCalendarSpec? calendar) => new(calendar);

    public bool AroundTheClock => _zone is null;

    /// <summary>Parses <c>HH:mm</c> (00:00–24:00).</summary>
    public static TimeSpan ParseTime(string value)
    {
        if (value.Length != 5 || value[2] != ':' || !int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) || !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > 59 || h > 24 || (h == 24 && m != 0))
        {
            throw new FormatException($"'{value}' is not a time of day (HH:mm).");
        }

        return new TimeSpan(h, m, 0);
    }

    /// <summary>The instant <paramref name="duration"/> of business time after <paramref name="start"/>; null when the calendar has no working time ahead.</summary>
    public DateTimeOffset? Add(DateTimeOffset start, TimeSpan duration)
    {
        if (_zone is null)
        {
            return start + duration;
        }

        var remaining = duration;
        if (remaining <= TimeSpan.Zero)
        {
            return start;
        }

        foreach (var (from, to) in Intervals(start))
        {
            var begin = from > start ? from : start;
            var available = to - begin;
            if (available <= TimeSpan.Zero)
            {
                continue;
            }

            if (remaining <= available)
            {
                return begin + remaining;
            }

            remaining -= available;
        }

        return null;
    }

    /// <summary>Business time between two instants (zero when <paramref name="to"/> is not after <paramref name="from"/>).</summary>
    public TimeSpan Between(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return TimeSpan.Zero;
        }

        if (_zone is null)
        {
            return to - from;
        }

        var total = TimeSpan.Zero;
        foreach (var (start, end) in Intervals(from))
        {
            if (start >= to)
            {
                break;
            }

            var a = start > from ? start : from;
            var b = end < to ? end : to;
            if (b > a)
            {
                total += b - a;
            }
        }

        return total;
    }

    /// <summary>Working intervals in UTC, in order, from the local day containing <paramref name="from"/>.</summary>
    public IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Intervals(DateTimeOffset from)
    {
        if (_zone is null)
        {
            yield break;
        }

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime);
        var last = DateTimeOffset.MinValue;
        for (var i = 0; i < MaxDays; i++, day = day.AddDays(1))
        {
            if (_holidays.Contains(day) || !_week.TryGetValue(day.DayOfWeek, out var hours))
            {
                continue;
            }

            foreach (var (start, end) in hours)
            {
                // Intervals that touch at an ambiguous fall-back time overlap in UTC; never count an instant twice.
                var s = ToUtc(day, start, first: true);
                var e = ToUtc(day, end, first: false);
                if (s < last)
                {
                    s = last;
                }

                if (e > s)
                {
                    last = e;
                    yield return (s, e);
                }
            }
        }
    }

    private DateTimeOffset ToUtc(DateOnly day, TimeSpan time, bool first)
    {
        var local = day.ToDateTime(TimeOnly.MinValue).Add(time);
        for (var guard = 0; _zone!.IsInvalidTime(local) && guard < 240; guard++)
        {
            local = local.AddMinutes(1); // inside a spring-forward gap: the first minute that exists
        }

        if (_zone.IsAmbiguousTime(local))
        {
            var offsets = _zone.GetAmbiguousTimeOffsets(local);
            var offset = first ? offsets.Max() : offsets.Min(); // the larger offset is the earlier instant
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        return new DateTimeOffset(local, _zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
