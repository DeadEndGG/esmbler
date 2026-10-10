using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Infrastructure.Sla;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Records;

/// <summary>
/// REQ-BIZ-005, REQ-MIG-004, REQ-ACC-13 (SLA part): SLA timers change with every record write in the same transaction,
/// pause while waiting, stop and resume, publish warnings and breaches from durable timers, keep their snapshot when
/// policies change, and stay in their workspace (ADR 0018).
/// </summary>
[Collection(PlatformCollection.Name)]
public class SlaTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";
    private const string Request = "core.tickets.request";

    private RecordService Records => f.Get<RecordService>();

    private SlaService Sla => f.Get<SlaService>();

    private Task<RecordView> NewTicketAsync(ProvisionedTenant t, string priority = "p3", string user = "agent") =>
        Records.CreateAsync(f.As(t, user), Ticket, new JsonObject { ["title"] = "Laptop will not boot", ["requester"] = t.Users["dana"].ToString(), ["priority"] = priority, ["impact"] = "high" }, null, null, default);

    private async Task<SlaTimerView> TimerAsync(ProvisionedTenant t, Guid id, string target, string viewer = "agent", string entity = Ticket) =>
        (await Sla.ForRecordAsync(f.As(t, viewer), entity, id, default)).Single(x => x.TargetId == target);

    private Task<SlaTimerRow> RowAsync(ProvisionedTenant t, Guid timer) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.SlaTimers.AsNoTracking().FirstAsync(x => x.Id == timer), default);

    private Task<List<string>> SlaEventsAsync(ProvisionedTenant t, Guid record) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Outbox.Where(o => o.RecordId == record && o.Type.StartsWith("platform.sla.")).OrderBy(o => o.CreatedAt).Select(o => o.Type).ToListAsync(), default);

    /// <summary>Makes a timer's scheduled check due now (as if its time had come) and runs the job runner.</summary>
    private async Task FireCheckAsync(ProvisionedTenant t, Guid timer, Action<SlaTimerRowPatch> patch)
    {
        var p = new SlaTimerRowPatch();
        patch(p);
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, async db =>
        {
            var row = await db.SlaTimers.AsTracking().FirstAsync(x => x.Id == timer);
            if (p.WarnPassed)
            {
                row.WarnAt = past;
            }

            if (p.DuePassed)
            {
                row.DueAt = past;
            }

            await db.SaveChangesAsync();
            return await db.Jobs.Where(j => j.DedupKey == SlaService.JobKey(timer) && j.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, past));
        }, default);
        var runner = f.Get<JobRunner>();
        while (await runner.RunOnceAsync(default) > 0)
        {
        }
    }

    private sealed class SlaTimerRowPatch
    {
        public bool WarnPassed { get; set; }

        public bool DuePassed { get; set; }
    }

    [Fact]
    public void Job_keys_are_per_timer() => Assert.NotEqual(SlaService.JobKey(Guid.NewGuid()), SlaService.JobKey(Guid.NewGuid()));

    [Fact]
    public async Task A_ticket_gets_response_and_resolution_targets_that_move_with_its_status()
    {
        var t = await f.NewTenantAsync("sla", "1.5.0");
        var agent = f.As(t, "agent");
        var ticket = await NewTicketAsync(t);

        var response = await TimerAsync(t, ticket.Id, "response");
        Assert.Equal(("running", 480, "Service desk hours", "America/New_York"), (response.Status, response.GoalMinutes, response.Calendar, response.TimeZone));
        Assert.NotNull(response.DueAt);
        var resolution = await TimerAsync(t, ticket.Id, "resolution", viewer: "dana"); // the requester sees their ticket's targets
        Assert.Equal(1800, resolution.GoalMinutes);

        // Picking it up meets the response target; waiting on the customer pauses resolution.
        var started = await Records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        Assert.Equal(("stopped", "met"), ((await TimerAsync(t, ticket.Id, "response")).Status, (await TimerAsync(t, ticket.Id, "response")).Outcome));
        var held = await Records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "hold", [], null, null, default);
        var paused = await TimerAsync(t, ticket.Id, "resolution");
        Assert.Equal("paused", paused.Status);
        Assert.Null(paused.DueAt);
        Assert.Null(await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.Where(j => j.DedupKey == SlaService.JobKey(paused.Id) && j.Status == "pending").Select(j => (Guid?)j.Id).FirstOrDefaultAsync(), default));

        var resumed = await Records.TransitionAsync(agent, Ticket, ticket.Id, held.Revision, "resume", [], null, null, default);
        Assert.Equal("running", (await TimerAsync(t, ticket.Id, "resolution")).Status);
        var resolved = await Records.TransitionAsync(agent, Ticket, ticket.Id, resumed.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Reseated the RAM" }, null, null, default);
        var done = await TimerAsync(t, ticket.Id, "resolution");
        Assert.Equal(("stopped", "met"), (done.Status, done.Outcome));

        // Reopening resumes the same resolution clock.
        await Records.TransitionAsync(agent, Ticket, ticket.Id, resolved.Revision, "reopen", [], null, null, default);
        var reopened = await TimerAsync(t, ticket.Id, "resolution");
        Assert.Equal((done.Id, "running", (string?)null), (reopened.Id, reopened.Status, reopened.Outcome));
    }

    [Fact]
    public async Task Durable_timers_publish_the_warning_and_the_breach_once()
    {
        var t = await f.NewTenantAsync("slab", "1.5.0");
        var ticket = await NewTicketAsync(t, "p1");
        var response = await TimerAsync(t, ticket.Id, "response");
        Assert.Null(response.Calendar); // P1 is measured around the clock
        Assert.Equal(30, response.GoalMinutes);

        await FireCheckAsync(t, response.Id, p => p.WarnPassed = true);
        Assert.Equal(["platform.sla.warning"], await SlaEventsAsync(t, ticket.Id));
        var warned = await RowAsync(t, response.Id);
        Assert.NotNull(warned.WarnedAt);
        var next = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.SingleAsync(j => j.DedupKey == SlaService.JobKey(response.Id) && j.Status == "pending"), default);
        Assert.Equal(warned.DueAt!.Value, next.AvailableAt, TimeSpan.FromSeconds(1)); // the next check is at the due time

        await FireCheckAsync(t, response.Id, p => p.DuePassed = true);
        await FireCheckAsync(t, response.Id, p => p.DuePassed = true);
        Assert.Equal(["platform.sla.warning", "platform.sla.breached"], await SlaEventsAsync(t, ticket.Id));
        var breached = await TimerAsync(t, ticket.Id, "response");
        Assert.NotNull(breached.BreachedAt);

        var json = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Outbox.Where(o => o.RecordId == ticket.Id && o.Type == EventTypes.SlaBreached).Select(o => o.Event).SingleAsync(), default);
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(json, SpecJson.Options)!;
        Assert.Equal((SlaService.Source, EventSeverities.High, "response"), (envelope.Source, envelope.Severity, envelope.Payload["target"]!.GetValue<string>()));
        Assert.Equal(ticket.Revision, envelope.RecordRevision);

        // Picking it up late records the miss.
        await Records.TransitionAsync(f.As(t, "agent"), Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        Assert.Equal("breached", (await TimerAsync(t, ticket.Id, "response")).Outcome);
    }

    [Fact]
    public async Task Running_timers_keep_their_goal_when_a_publish_changes_it()
    {
        var t = await f.NewTenantAsync("slav", "1.5.0");
        var before = await NewTicketAsync(t, "p2");
        Assert.Equal(600, (await TimerAsync(t, before.Id, "resolution")).GoalMinutes);

        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var (config, _, _, _) = await f.Get<PreviewService>().WorkingConfigAsync(admin, default);
        var targets = config.FindSlaPolicy("core.tickets.sla.ticket")!.Targets.Select(SpecJson.Clone).ToList();
        targets.Single(x => x.Id == "resolution").Goals = [new SlaGoalSpec { Minutes = 60, Calendar = SlaCalendars.AroundTheClock }];
        await drafts.ApplyAsync(admin, new UpdateSlaPolicy { Policy = "core.tickets.sla.ticket", Set = new SlaPolicyPatch { Targets = targets } }, draft.Version, "ui", default);
        var state = await drafts.CurrentAsync(admin, default);
        Assert.True(state!.ArtifactHash is not null, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        await f.Get<PublishService>().PublishAsync(admin, state.ArtifactHash!, state.PublishedRevision, default);

        // The running timer keeps 600 business minutes, also after another write; new tickets get the new goal.
        var updated = await Records.UpdateAsync(f.As(t, "agent"), Ticket, before.Id, before.Revision, new JsonObject { ["title"] = "Laptop still will not boot" }, null, null, default);
        Assert.Equal(600, (await TimerAsync(t, updated.Id, "resolution")).GoalMinutes);
        var after = await NewTicketAsync(t, "p2");
        var fresh = await TimerAsync(t, after.Id, "resolution");
        Assert.Equal((60, (string?)null), (fresh.GoalMinutes, fresh.Calendar));
    }

    [Fact]
    public async Task Requests_do_not_count_time_waiting_for_approval_and_deleted_records_stop_their_clocks()
    {
        var t = await f.NewTenantAsync("slar", "1.5.0");
        var created = await Records.CreateAsync(f.As(t, "dana"), Request, new JsonObject { ["catalog_item"] = "core.tickets.catalog.new_laptop", ["variables"] = new JsonObject { ["model"] = "standard", ["justification"] = "New starter" } }, null, null, default);
        var dispatcher = f.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }

        var request = await Records.GetAsync(f.As(t, "agent"), Request, created.Id, default);
        Assert.Equal("awaiting_approval", request.Data["status"]!.GetValue<string>());
        Assert.Equal("paused", (await TimerAsync(t, created.Id, "fulfillment", entity: Request)).Status);

        var ticket = await NewTicketAsync(t);
        await Records.DeleteAsync(f.As(t, "admin"), Ticket, ticket.Id, ticket.Revision, default);
        var rows = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.SlaTimers.Where(x => x.RecordId == ticket.Id).Select(x => x.Status).ToListAsync(), default);
        Assert.All(rows, s => Assert.Equal("cancelled", s));
    }

    [Fact]
    public async Task Other_workspaces_and_people_without_access_see_no_timers()
    {
        var t = await f.NewTenantAsync("slai", "1.5.0");
        var ticket = await NewTicketAsync(t);
        var foreign = await Assert.ThrowsAsync<KernelException>(() => Sla.ForRecordAsync(f.As(f.Globex, "morgan"), Ticket, ticket.Id, default));
        Assert.Equal(404, foreign.Status);
        var stranger = await Assert.ThrowsAsync<KernelException>(() => Sla.ForRecordAsync(f.As(t, "jordan"), Ticket, ticket.Id, default));
        Assert.Equal(404, stranger.Status);
        var visible = await f.Get<Database>().InTenantAsync(f.Globex.TenantId, f.Globex.Environments["prod"], null, db => db.SlaTimers.CountAsync(x => x.RecordId == ticket.Id), default);
        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task A_restored_ticket_gets_fresh_clocks_and_keeps_the_cancelled_history()
    {
        var t = await f.NewTenantAsync("slad", "1.5.0");
        var ticket = await NewTicketAsync(t);
        var first = await TimerAsync(t, ticket.Id, "resolution");
        await Records.DeleteAsync(f.As(t, "admin"), Ticket, ticket.Id, ticket.Revision, default);
        await Records.RestoreAsync(f.As(t, "admin"), Ticket, ticket.Id, default);

        var fresh = await TimerAsync(t, ticket.Id, "resolution");
        Assert.NotEqual(first.Id, fresh.Id);
        Assert.Equal(("running", 1), (fresh.Status, fresh.Cycle));
        Assert.Equal("cancelled", (await RowAsync(t, first.Id)).Status);
    }

    [Fact]
    public async Task A_workflow_migration_that_cancels_requests_stops_their_clocks()
    {
        var t = await f.NewTenantAsync("slam", "1.5.0");
        var created = await Records.CreateAsync(f.As(t, "dana"), Request, new JsonObject { ["catalog_item"] = "core.tickets.catalog.onboarding", ["variables"] = new JsonObject { ["new_hire_name"] = "Ada", ["start_date"] = "2026-12-01", ["department"] = "engineering" } }, null, null, default);
        Assert.Equal("running", (await TimerAsync(t, created.Id, "fulfillment", entity: Request)).Status);

        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var v = (await drafts.OpenAsync(admin, default)).Version;
        v = (await drafts.ApplyAsync(admin, new AddTransition { Workflow = "core.tickets.request_lifecycle", Transition = new WorkflowTransition { Id = "tenant__nudge", Label = "Nudge", From = ["submitted"], To = "submitted", Roles = ["core.tickets.agent"] } }, v, "ui", default)).Version;
        await drafts.ApplyAsync(admin, new SetActiveInstancePolicy { Workflow = "core.tickets.request_lifecycle", Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Cancel, CancelState = "cancelled" } }, v, "ui", default);
        var state = await drafts.CurrentAsync(admin, default);
        Assert.True(state!.ArtifactHash is not null, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        await f.Get<PublishService>().PublishAsync(admin, state.ArtifactHash!, state.PublishedRevision, default);
        var runner = f.Get<JobRunner>();
        while (await runner.RunOnceAsync(default) > 0)
        {
        }

        Assert.Equal("cancelled", (await Records.GetAsync(f.As(t, "agent"), Request, created.Id, default)).Data["status"]!.GetValue<string>());
        var timer = await TimerAsync(t, created.Id, "fulfillment", entity: Request);
        Assert.Equal(("stopped", "met"), (timer.Status, timer.Outcome));
    }
}
