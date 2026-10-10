using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-BIZ-005, REQ-MIG-004: business calendars and SLA policies, their validation and overlay operations (ADR 0018).</summary>
public class SlaPolicyTests
{
    private const string Ticket = "core.tickets.ticket";
    private const string Calendar = "core.tickets.calendar.business_hours";
    private const string TicketSla = "core.tickets.sla.ticket";

    private static BusinessCalendarSpec London() => new()
    {
        Id = "tenant__london",
        Label = "London office",
        TimeZone = "Europe/London",
        Week = [new WorkingHoursSpec { Day = "mon", Start = "09:00", End = "12:30" }, new WorkingHoursSpec { Day = "mon", Start = "13:30", End = "17:30" }],
        Holidays = [new HolidaySpec { Date = "2026-12-25", Label = "Christmas Day" }],
    };

    private static SlaPolicySpec Vip() => new()
    {
        Id = "tenant__vip",
        Label = "VIP",
        Entity = Ticket,
        When = "record.priority in ['p1', 'p2']",
        Targets =
        [
            new SlaTargetSpec
            {
                Id = "resolution",
                Label = "Resolution",
                Stop = "record.status in ['resolved', 'closed']",
                Pause = "record.status == 'on_hold'",
                Calendar = "tenant__london",
                Goals = [new SlaGoalSpec { When = "record.priority == 'p1'", Minutes = 120, Calendar = SlaCalendars.AroundTheClock }, new SlaGoalSpec { Minutes = 480 }],
            },
        ],
    };

    [Fact]
    public void Release_1_5_0_ships_a_calendar_and_service_levels_for_tickets_requests_and_tasks()
    {
        var result = Compile("1.5.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.Equal("America/New_York", cfg.FindBusinessCalendar(Calendar)!.TimeZone);
        Assert.Equal(["response", "resolution"], cfg.FindSlaPolicy(TicketSla)!.Targets.Select(t => t.Id));
        Assert.NotNull(cfg.FindSlaPolicy("core.tickets.sla.request"));
        Assert.NotNull(cfg.FindSlaPolicy("core.tickets.sla.task"));
        Assert.NotNull(cfg.FindEventType(Sdk.Events.EventTypes.SlaBreached));
        Assert.Contains(TicketSla, DependencyGraph.Build(cfg).ImpactOf(Calendar));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "config.goal_gap");
    }

    [Fact]
    public void Tenants_add_calendars_and_policies_and_tune_the_defaults()
    {
        var goals = new List<SlaTargetSpec>(Compile("1.5.0").Config.FindSlaPolicy(TicketSla)!.Targets);
        goals[0].Goals = [new SlaGoalSpec { Minutes = 60 }];
        var result = Compile(
            "1.5.0",
            new AddBusinessCalendar { Calendar = London() },
            new AddSlaPolicy { Policy = Vip() },
            new UpdateSlaPolicy { Policy = TicketSla, Set = new SlaPolicyPatch { Targets = goals } },
            new UpdateBusinessCalendar { Calendar = Calendar, Set = new BusinessCalendarPatch { TimeZone = "America/Chicago" } });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Equal(60, result.Config.FindSlaPolicy(TicketSla)!.Targets[0].Goals.Single().Minutes);
        Assert.Equal("America/Chicago", result.Config.FindBusinessCalendar(Calendar)!.TimeZone);
        Assert.Contains("tenant__vip", result.Config.Customized);
        Assert.Contains(TicketSla, result.Config.Customized);
    }

    [Fact]
    public void Defaults_are_disabled_rather_than_removed_and_used_calendars_stay()
    {
        string Last(params OverlayOp[] ops) => Compile("1.5.0", ops).Composition.Outcomes[^1].Status;
        Assert.Equal(OpStatus.Failed, Last(new RemoveSlaPolicy { Policy = TicketSla }));
        Assert.Equal(OpStatus.Failed, Last(new RemoveBusinessCalendar { Calendar = Calendar }));
        Assert.Equal(OpStatus.Failed, Last(new AddBusinessCalendar { Calendar = London() }, new AddSlaPolicy { Policy = Vip() }, new RemoveBusinessCalendar { Calendar = "tenant__london" }));
        Assert.Equal(OpStatus.Applied, Last(new AddBusinessCalendar { Calendar = London() }, new RemoveBusinessCalendar { Calendar = "tenant__london" }));
        Assert.Equal(OpStatus.Applied, Last(new UpdateSlaPolicy { Policy = TicketSla, Set = new SlaPolicyPatch { Enabled = false } }));
        Assert.Equal(OpStatus.Failed, Last(new AddSlaPolicy { Policy = Rename(Vip(), "core.tickets.sla.vip") }));
    }

    private static SlaPolicySpec Rename(SlaPolicySpec policy, string id)
    {
        policy.Id = id;
        return policy;
    }

    [Fact]
    public void Calendars_are_validated_with_suggestions()
    {
        var bad = London();
        bad.TimeZone = "London";
        bad.Week.Add(new WorkingHoursSpec { Day = "monday", Start = "09:00", End = "17:00" });
        bad.Week.Add(new WorkingHoursSpec { Day = "tue", Start = "18:00", End = "09:00" });
        bad.Week.Add(new WorkingHoursSpec { Day = "wed", Start = "9am", End = "17:00" });
        bad.Week.Add(new WorkingHoursSpec { Day = "mon", Start = "12:00", End = "14:00" });
        bad.Holidays.Add(new HolidaySpec { Date = "25/12/2026" });
        var result = Compile("1.5.0", new AddBusinessCalendar { Calendar = bad });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_time_zone");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_day");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_hours" && e.Message.Contains("is not after", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_hours" && e.Message.Contains("9am", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_hours" && e.Message.Contains("overlapping", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_date");
    }

    [Fact]
    public void Policies_are_validated_with_suggestions()
    {
        var bad = Vip();
        var target = bad.Targets[0];
        target.Stop = "";
        target.Calendar = "tenant__londn";
        target.OnReopen = "reset";
        target.WarnAtPercent = 100;
        target.Goals.Add(new SlaGoalSpec { Minutes = 0 });
        target.Pause = "record.stauts == 'on_hold'";
        var result = Compile("1.5.0", new AddBusinessCalendar { Calendar = London() }, new AddSlaPolicy { Policy = bad });
        Assert.Contains(result.Errors, e => e.Code == "config.missing_stop");
        Assert.Contains(result.Errors, e => e.Code == "config.not_found" && e.Suggestion == "tenant__london");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_reopen");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_warning");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_goal");
        Assert.Contains(result.Errors, e => e.Target == "tenant__vip/resolution" && e.Message.Contains("pause condition", StringComparison.Ordinal));

        var gap = Vip();
        gap.Targets[0].Goals.RemoveAt(1);
        Assert.Contains(Compile("1.5.0", new AddBusinessCalendar { Calendar = London() }, new AddSlaPolicy { Policy = gap }).Diagnostics, d => d.Code == "config.goal_gap");
    }

    [Fact]
    public void Calendars_and_goals_stay_within_bounds_that_keep_deadlines_cheap_to_compute()
    {
        var busy = London();
        busy.Week = [.. Enumerable.Range(0, 5).Select(i => new WorkingHoursSpec { Day = "tue", Start = $"{9 + i:00}:00", End = $"{9 + i:00}:30" })];
        busy.Week.Add(new WorkingHoursSpec { Day = "wed", Start = "09:00", End = "09:10" });
        busy.Holidays.Add(new HolidaySpec { Date = "2026-01-01", Label = new string('x', 81) });
        var result = Compile("1.5.0", new AddBusinessCalendar { Calendar = busy });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_hours" && e.Message.Contains("at most 4", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_hours" && e.Message.Contains("15 minutes", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_label");

        var slow = Vip();
        slow.Targets[0].Goals = [new SlaGoalSpec { Minutes = 525_600 }]; // a year of business minutes in 7.5 h a week
        Assert.Contains(Compile("1.5.0", new AddBusinessCalendar { Calendar = London() }, new AddSlaPolicy { Policy = slow }).Errors, e => e.Code == "config.goal_unreachable");
    }

    [Fact]
    public void Sla_conditions_cannot_read_fields_some_readers_may_not_see()
    {
        // estimated_effort is hidden from requesters, who can read their own tickets and their SLA panel.
        var leaky = Vip();
        leaky.Targets[0].Goals = [new SlaGoalSpec { When = "record.estimated_effort == 'xl'", Minutes = 60 }, new SlaGoalSpec { Minutes = 480 }];
        Assert.Contains(Compile("1.5.0", new AddBusinessCalendar { Calendar = London() }, new AddSlaPolicy { Policy = leaky }).Errors, e => e.Code == "config.sla_hidden_field");
    }
}
