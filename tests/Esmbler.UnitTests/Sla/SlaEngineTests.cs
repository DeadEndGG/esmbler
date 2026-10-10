using Esmbler.Kernel.Sla;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Sla;

/// <summary>REQ-BIZ-005, REQ-MIG-004: SLA timers start, pause, resume, stop, reopen, warn and breach on the Tickets 1.5.0 policies; running timers keep their snapshot (ADR 0018).</summary>
public class SlaEngineTests
{
    private const string Ticket = "core.tickets.ticket";

    private static readonly SpecCatalog Config = Compile("1.5.0").Config;

    private static EntitySpec Entity => Config.FindEntity(Ticket)!;

    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> Vars(string status, string priority = "p3", string? priorStatus = null) => new()
    {
        ["record"] = new Dictionary<string, object?> { ["status"] = status, ["priority"] = priority },
        ["prior"] = priorStatus is null ? null : new Dictionary<string, object?> { ["status"] = priorStatus, ["priority"] = priority },
    };

    private static SlaTimer Timer(List<SlaTimer> timers, string target) => timers.Single(t => t.TargetId == target && t.Status != SlaStatuses.Cancelled && t.Cycle == timers.Where(x => x.TargetId == target).Max(x => x.Cycle));

    [Fact]
    public void A_new_ticket_starts_response_and_resolution_in_service_desk_hours()
    {
        var timers = new List<SlaTimer>();
        var monday10 = Utc("2026-11-30T15:00:00Z"); // 10:00 New York (EST)
        Assert.Empty(SlaEngine.Apply(Config, Entity, Vars("new"), isCreate: true, timers, monday10));

        var response = Timer(timers, "response");
        Assert.Equal((SlaStatuses.Running, 480), (response.Status, response.Snapshot.GoalMinutes));
        Assert.Equal(Utc("2026-11-30T23:00:00Z"), response.DueAt); // 8 business hours: Monday 18:00
        Assert.Equal("America/New_York", response.Snapshot.Calendar!.TimeZone);

        var resolution = Timer(timers, "resolution");
        Assert.Equal(Utc("2026-12-03T15:00:00Z"), resolution.DueAt); // 30 business hours: Thursday 10:00
        Assert.NotNull(resolution.WarnAt);
    }

    [Fact]
    public void Critical_tickets_are_measured_around_the_clock()
    {
        var timers = new List<SlaTimer>();
        var saturday = Utc("2026-11-28T03:00:00Z");
        SlaEngine.Apply(Config, Entity, Vars("new", "p1"), isCreate: true, timers, saturday);
        var response = Timer(timers, "response");
        Assert.Null(response.Snapshot.Calendar);
        Assert.Equal(saturday.AddMinutes(30), response.DueAt);
        Assert.Equal(saturday.AddHours(4), Timer(timers, "resolution").DueAt);
    }

    [Fact]
    public void Waiting_on_the_customer_pauses_resolution_and_picking_it_up_meets_the_response_target()
    {
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-30T15:00:00Z");
        SlaEngine.Apply(Config, Entity, Vars("new"), isCreate: true, timers, t0);

        SlaEngine.Apply(Config, Entity, Vars("on_hold", priorStatus: "new"), isCreate: false, timers, t0.AddHours(1));
        var response = Timer(timers, "response");
        Assert.Equal((SlaStatuses.Stopped, SlaOutcomes.Met), (response.Status, response.Outcome));
        var resolution = Timer(timers, "resolution");
        Assert.Equal(SlaStatuses.Paused, resolution.Status);
        Assert.Equal(3600, resolution.ElapsedSeconds);
        Assert.Null(resolution.DueAt);

        // Two days later the customer answers: 29 business hours remain from Wednesday 10:00.
        SlaEngine.Apply(Config, Entity, Vars("in_progress", priorStatus: "on_hold"), isCreate: false, timers, Utc("2026-12-02T15:00:00Z"));
        Assert.Equal(SlaStatuses.Running, resolution.Status);
        Assert.Equal(Utc("2026-12-07T14:00:00Z"), resolution.DueAt); // Wed 8 h + Thu 10 h + Fri 10 h + Monday 09:00
    }

    [Fact]
    public void Resolving_stops_and_reopening_resumes_the_same_timer()
    {
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-30T15:00:00Z");
        SlaEngine.Apply(Config, Entity, Vars("new"), isCreate: true, timers, t0);
        SlaEngine.Apply(Config, Entity, Vars("resolved", priorStatus: "new"), isCreate: false, timers, t0.AddHours(2));
        var resolution = Timer(timers, "resolution");
        Assert.Equal((SlaStatuses.Stopped, SlaOutcomes.Met, 7200L), (resolution.Status, resolution.Outcome, resolution.ElapsedSeconds));

        SlaEngine.Apply(Config, Entity, Vars("in_progress", priorStatus: "resolved"), isCreate: false, timers, t0.AddHours(3));
        Assert.Same(resolution, Timer(timers, "resolution"));
        Assert.Equal((SlaStatuses.Running, (string?)null), (resolution.Status, resolution.Outcome));
        Assert.Equal(t0.AddHours(3), resolution.SegmentStartedAt);

        // Response uses onReopen none: it stays met.
        Assert.Equal(SlaOutcomes.Met, Timer(timers, "response").Outcome);
    }

    [Fact]
    public void A_breach_is_published_once_and_stays_recorded()
    {
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-28T03:00:00Z");
        SlaEngine.Apply(Config, Entity, Vars("new", "p1"), isCreate: true, timers, t0);
        var response = Timer(timers, "response");

        var warning = SlaEngine.Check(response, t0.AddMinutes(23));
        Assert.Equal("warning", Assert.Single(warning).Kind);
        Assert.Empty(SlaEngine.Check(response, t0.AddMinutes(24)));

        var breach = SlaEngine.Check(response, t0.AddMinutes(31));
        Assert.Equal(SlaOutcomes.Breached, Assert.Single(breach).Kind);
        Assert.Equal(t0.AddMinutes(30), response.BreachedAt);
        Assert.Empty(SlaEngine.Check(response, t0.AddMinutes(40)));

        // Picking it up late stops the timer as breached without a second signal.
        Assert.Empty(SlaEngine.Apply(Config, Entity, Vars("assigned", "p1", "new"), isCreate: false, timers, t0.AddMinutes(45)));
        Assert.Equal((SlaStatuses.Stopped, SlaOutcomes.Breached), (response.Status, response.Outcome));
    }

    [Fact]
    public void Running_timers_keep_their_goal_and_calendar_when_the_policy_changes()
    {
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-30T15:00:00Z");
        SlaEngine.Apply(Config, Entity, Vars("new"), isCreate: true, timers, t0);

        var changed = Compile("1.5.0").Config;
        var target = changed.FindSlaPolicy("core.tickets.sla.ticket")!.FindTarget("resolution")!;
        target.Goals = [new SlaGoalSpec { Minutes = 60, Calendar = SlaCalendars.AroundTheClock }];
        changed.FindBusinessCalendar("core.tickets.calendar.business_hours")!.TimeZone = "Asia/Tokyo";

        SlaEngine.Apply(changed, Entity, Vars("on_hold", priorStatus: "new"), isCreate: false, timers, t0.AddHours(1));
        SlaEngine.Apply(changed, Entity, Vars("in_progress", priorStatus: "on_hold"), isCreate: false, timers, t0.AddHours(2));
        var resolution = Timer(timers, "resolution");
        Assert.Equal(1800, resolution.Snapshot.GoalMinutes);
        Assert.Equal("America/New_York", resolution.Snapshot.Calendar!.TimeZone);

        // A new ticket gets the new goal.
        var fresh = new List<SlaTimer>();
        SlaEngine.Apply(changed, Entity, Vars("new"), isCreate: true, fresh, t0);
        Assert.Equal(60, Timer(fresh, "resolution").Snapshot.GoalMinutes);
    }

    [Fact]
    public void Restart_on_reopen_starts_a_new_cycle_and_keeps_the_old_outcome()
    {
        var config = Compile("1.5.0").Config;
        config.FindSlaPolicy("core.tickets.sla.ticket")!.FindTarget("resolution")!.OnReopen = SlaReopen.Restart;
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-30T15:00:00Z");
        SlaEngine.Apply(config, Entity, Vars("new"), isCreate: true, timers, t0);
        SlaEngine.Apply(config, Entity, Vars("resolved", priorStatus: "new"), isCreate: false, timers, t0.AddHours(1));
        SlaEngine.Apply(config, Entity, Vars("in_progress", priorStatus: "resolved"), isCreate: false, timers, t0.AddHours(2));
        var cycles = timers.Where(t => t.TargetId == "resolution").OrderBy(t => t.Cycle).ToList();
        Assert.Equal(2, cycles.Count);
        Assert.Equal((SlaStatuses.Stopped, SlaOutcomes.Met), (cycles[0].Status, cycles[0].Outcome));
        Assert.Equal((1, SlaStatuses.Running, 0L), (cycles[1].Cycle, cycles[1].Status, cycles[1].ElapsedSeconds));
    }

    [Fact]
    public void A_deadline_that_passed_before_a_pause_is_a_breach_at_the_deadline()
    {
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-28T03:00:00Z");
        SlaEngine.Apply(Config, Entity, Vars("new", "p1"), isCreate: true, timers, t0);
        var resolution = Timer(timers, "resolution");

        // Nobody touched it until five hours later (the check job had not run yet), when the agent puts it on hold.
        var signals = SlaEngine.Apply(Config, Entity, Vars("on_hold", "p1", "new"), isCreate: false, timers, t0.AddHours(5));
        Assert.Contains(signals, s => s.Timer == resolution && s.Kind == SlaOutcomes.Breached);
        Assert.Equal(t0.AddHours(4), resolution.BreachedAt);
        Assert.Equal(SlaStatuses.Paused, resolution.Status);
    }

    [Fact]
    public void A_restored_record_starts_new_cycles_after_its_timers_were_cancelled()
    {
        var config = Compile("1.5.0").Config;
        config.FindSlaPolicy("core.tickets.sla.ticket")!.FindTarget("resolution")!.OnReopen = SlaReopen.Restart;
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-30T15:00:00Z");
        SlaEngine.Apply(config, Entity, Vars("new"), isCreate: true, timers, t0);
        SlaEngine.Apply(config, Entity, Vars("resolved", priorStatus: "new"), isCreate: false, timers, t0.AddHours(1));
        SlaEngine.Apply(config, Entity, Vars("in_progress", priorStatus: "resolved"), isCreate: false, timers, t0.AddHours(2));

        // Delete cancels the open cycle; restore evaluates the record as if created.
        foreach (var open in timers.Where(t => t.Status is SlaStatuses.Running or SlaStatuses.Paused))
        {
            open.Status = SlaStatuses.Cancelled;
        }

        SlaEngine.Apply(config, Entity, Vars("in_progress"), isCreate: true, timers, t0.AddHours(3));
        var cycles = timers.Where(t => t.TargetId == "resolution").Select(t => (t.Cycle, t.Status)).OrderBy(c => c.Cycle).ToList();
        Assert.Equal([(0, SlaStatuses.Stopped), (1, SlaStatuses.Cancelled), (2, SlaStatuses.Running)], cycles);
        Assert.Equal(cycles.Count, cycles.Select(c => c.Cycle).Distinct().Count());
    }

    [Fact]
    public void A_disabled_policy_starts_no_new_clocks_but_lets_running_ones_finish()
    {
        var config = Compile("1.5.0").Config;
        var policy = config.FindSlaPolicy("core.tickets.sla.ticket")!;
        policy.FindTarget("resolution")!.OnReopen = SlaReopen.Restart;
        var timers = new List<SlaTimer>();
        var t0 = Utc("2026-11-30T15:00:00Z");
        SlaEngine.Apply(config, Entity, Vars("new"), isCreate: true, timers, t0);
        policy.Enabled = false;
        SlaEngine.Apply(config, Entity, Vars("resolved", priorStatus: "new"), isCreate: false, timers, t0.AddHours(1));
        Assert.Equal(SlaOutcomes.Met, Timer(timers, "resolution").Outcome);
        SlaEngine.Apply(config, Entity, Vars("in_progress", priorStatus: "resolved"), isCreate: false, timers, t0.AddHours(2));
        Assert.Single(timers, t => t.TargetId == "resolution");
        Assert.Empty(new List<SlaTimer>().Also(fresh => SlaEngine.Apply(config, Entity, Vars("new"), isCreate: true, fresh, t0)));
    }

    [Fact]
    public void A_clock_paused_across_a_daylight_saving_change_resumes_with_the_right_deadline()
    {
        // REQ-ACC-13 (DST SLA): P3 resolution, 30 service desk hours (08:00–18:00 New York).
        var timers = new List<SlaTimer>();
        var friday = Utc("2026-10-30T14:00:00Z"); // Friday 10:00 EDT
        SlaEngine.Apply(Config, Entity, Vars("new"), isCreate: true, timers, friday);
        var resolution = Timer(timers, "resolution");

        // On hold from Friday 12:00 EDT (2 h measured) until Monday 9:00 EST, after clocks fell back on Sunday.
        SlaEngine.Apply(Config, Entity, Vars("on_hold", priorStatus: "new"), isCreate: false, timers, Utc("2026-10-30T16:00:00Z"));
        Assert.Equal(7200, resolution.ElapsedSeconds);
        SlaEngine.Apply(Config, Entity, Vars("in_progress", priorStatus: "on_hold"), isCreate: false, timers, Utc("2026-11-02T14:00:00Z"));

        // 28 hours left from Monday 09:00 EST: Monday 9 h, Tuesday 10 h, Wednesday 08:00 + 9 h = 17:00 EST = 22:00 UTC.
        Assert.Equal(Utc("2026-11-04T22:00:00Z"), resolution.DueAt);
    }
}

internal static class TestExtensions
{
    public static List<T> Also<T>(this List<T> list, Action<List<T>> action)
    {
        action(list);
        return list;
    }
}
