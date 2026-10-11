using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>REQ-ACC-03 (upgrade preserves overlays, blocks conflicts) and REQ-ACC-04 (migration failure and safe resume).</summary>
[Collection(PlatformCollection.Name)]
public class UpgradeTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    private sealed class FailOnce(string migration, int afterBatches) : IMigrationFaultInjector
    {
        private bool _fired;

        public void AfterBatch(string migrationId, int batchesCommitted)
        {
            if (!_fired && migrationId == migration && batchesCommitted >= afterBatches)
            {
                _fired = true;
                throw new InvalidOperationException("Injected failure: worker lost during migration.");
            }
        }
    }

    [Fact]
    public async Task Skipping_a_release_still_runs_its_migrations_and_adds_the_catalog()
    {
        var t = await f.NewTenantAsync("skip");
        var admin = f.As(t, "admin");
        var records = f.Get<RecordService>();
        await records.CreateAsync(f.As(t, "agent"), Ticket, new JsonObject { ["title"] = "Legacy effort", ["requester"] = t.Users["dana"].ToString(), ["estimated_effort"] = "2.5" }, null, null, default);

        var upgrades = f.Get<UpgradeService>();
        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.2.0", default);
        Assert.True(plan.Compatible, string.Join("; ", plan.Errors.Select(e => e.Message)));
        Assert.Equal(["backfill_impact", "convert_effort"], plan.Migrations.Select(m => m.Id));
        Assert.Contains(plan.BaseChanges, c => c.Id == "core.tickets.request" && c.Change == "added");

        var started = await upgrades.StartAsync(admin, "core.tickets", "1.2.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        var done = await upgrades.GetAsync(admin, started.Id, default);
        Assert.Equal("activated", done.Status);
        Assert.All(done.Runs, r => Assert.Equal("completed", r.Status));

        var legacy = await records.QueryAsync(admin, new RecordQuery { Entity = Ticket, Filters = ["record.title == 'Legacy effort'"] }, default);
        Assert.Equal(2.5, legacy.Items.Single().Data["effort_hours"]!.GetValue<double>());
        var app = await f.Get<ExperienceService>().AppAsync(admin, default);
        Assert.Contains(app.Navigation, n => n.Entity == "core.tickets.request");
    }

    [Fact]
    public async Task Upgrading_to_approvals_keeps_requests_already_submitted_workable()
    {
        // ASM-046: requests submitted before their item needed approval stay on their workflow version and are
        // grandfathered; new requests go through approval.
        var t = await f.NewTenantAsync("upappr", "1.2.0");
        var admin = f.As(t, "admin");
        var records = f.Get<RecordService>();
        var before = await records.CreateAsync(f.As(t, "dana"), "core.tickets.request", new JsonObject { ["catalog_item"] = "core.tickets.catalog.new_laptop", ["variables"] = new JsonObject { ["model"] = "standard", ["justification"] = "Before approvals" } }, null, null, default);

        var upgrades = f.Get<UpgradeService>();
        var started = await upgrades.StartAsync(admin, "core.tickets", "1.3.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        Assert.Equal("activated", (await upgrades.GetAsync(admin, started.Id, default)).Status);
        var runner = f.Get<Kernel.Infrastructure.Events.JobRunner>();
        var dispatcher = f.Get<Kernel.Infrastructure.Events.OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0 | await runner.RunOnceAsync(default) > 0)
        {
        }

        var old = await records.GetAsync(f.As(t, "agent"), "core.tickets.request", before.Id, default);
        Assert.Equal("submitted", old.Data["status"]!.GetValue<string>());
        var moved = await records.TransitionAsync(f.As(t, "agent"), "core.tickets.request", before.Id, old.Revision, "start_fulfillment", [], null, null, default);
        Assert.Equal("fulfilling", moved.Data["status"]!.GetValue<string>());

        var after = await records.CreateAsync(f.As(t, "dana"), "core.tickets.request", new JsonObject { ["catalog_item"] = "core.tickets.catalog.new_laptop", ["variables"] = new JsonObject { ["model"] = "standard", ["justification"] = "After approvals" } }, null, null, default);
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }

        Assert.Equal("awaiting_approval", (await records.GetAsync(f.As(t, "agent"), "core.tickets.request", after.Id, default)).Data["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Upgrading_to_dashboards_keeps_customizations_and_running_sla_clocks()
    {
        // REQ-WS-004 (ADR 0019): 1.6.0 adds metrics, the Overview dashboard and metric-backed saved views. A customized
        // workspace with running SLA clocks upgrades without losing either, and its figures count its records.
        var t = await f.NewTenantAsync("updash", "1.5.0");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var v = (await drafts.OpenAsync(admin, default)).Version;
        foreach (var op in ConfigLifecycleTests.Customization())
        {
            v = (await drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var state = await drafts.CurrentAsync(admin, default);
        await f.Get<PublishService>().PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);

        var records = f.Get<RecordService>();
        var p1 = await records.CreateAsync(f.As(t, "agent"), Ticket, new JsonObject { ["title"] = "Upgrade during an outage", ["requester"] = t.Users["dana"].ToString(), ["priority"] = "p1", ["impact"] = "high", ["tenant__vendor"] = "Contoso" }, null, null, default);
        var db = f.Get<Database>();
        async Task<List<(Guid Id, DateTimeOffset? Due, int? Revision)>> TimersAsync() =>
            (await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.SlaTimers.Where(s => s.RecordId == p1.Id).OrderBy(s => s.TargetId).Select(s => new { s.Id, s.DueAt, s.ConfigRevision }).ToListAsync(), default))
            .Select(s => (s.Id, s.DueAt, s.ConfigRevision)).ToList();
        var before = await TimersAsync();
        Assert.NotEmpty(before);

        var upgrades = f.Get<UpgradeService>();
        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.6.0", default);
        Assert.True(plan.Compatible, string.Join("; ", plan.Errors.Select(e => e.Message)));
        Assert.All(plan.Overlay, o => Assert.Equal(OpStatus.Applied, o.Status));
        Assert.Contains(plan.BaseChanges, c => c.Id == "core.tickets.dashboard" && c.Change == "added");
        var started = await upgrades.StartAsync(admin, "core.tickets", "1.6.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        Assert.Equal("activated", (await upgrades.GetAsync(admin, started.Id, default)).Status);

        // Running clocks keep their snapshot; the customization is still there.
        Assert.Equal(before, await TimersAsync());
        var kept = await records.GetAsync(admin, Ticket, p1.Id, default);
        Assert.Equal("Contoso", kept.Data["tenant__vendor"]!.GetValue<string>());

        var app = await f.Get<ExperienceService>().AppAsync(f.As(t, "agent"), default);
        Assert.Contains(app.Dashboards!, d => d.View == "core.tickets.dashboard");
        var open = await f.Get<Kernel.Infrastructure.Metrics.MetricService>().EvaluateAsync(f.As(t, "agent"), "core.tickets.metric.open", null, default);
        var listed = await records.QueryAsync(f.As(t, "agent"), new RecordQuery { Entity = Ticket, Filters = open.Drill!.Filters, PageSize = 1 }, default);
        Assert.Equal(listed.Total, open.Count);
        Assert.True(open.Count >= 1);
    }

    [Fact]
    public async Task Upgrading_to_a_changed_sla_policy_and_calendar_keeps_running_clocks_on_their_snapshot()
    {
        // REQ-MIG-004 (ADR 0018): 1.10.0 tightens P2 resolution from 600 to 480 service desk minutes and adds holidays.
        // A clock that started before the upgrade keeps its goal, due time and calendar; clocks started after it use the
        // new policy; the customization survives; and the new figures evaluate.
        var t = await f.NewTenantAsync("upsla", "1.9.0");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var v = (await drafts.OpenAsync(admin, default)).Version;
        foreach (var op in ConfigLifecycleTests.Customization())
        {
            v = (await drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var state = await drafts.CurrentAsync(admin, default);
        await f.Get<PublishService>().PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);

        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var dana = t.Users["dana"].ToString();
        var before = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Laptop slow", ["requester"] = dana, ["priority"] = "p2", ["tenant__vendor"] = "Contoso" }, null, null, default);
        var db = f.Get<Database>();
        Task<SlaTimerRow> ResolutionAsync(Guid record) =>
            db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.SlaTimers.AsNoTracking().SingleAsync(s => s.RecordId == record && s.TargetId == "resolution"), default);
        var running = await ResolutionAsync(before.Id);
        Assert.Equal(600 * 60, running.GoalSeconds);

        // A breach recorded before the upgrade stays recorded and keeps counting.
        var breachedTicket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Already late", ["requester"] = dana, ["priority"] = "p2" }, null, null, default);
        var breachedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.SlaTimers.Where(s => s.RecordId == breachedTicket.Id && s.TargetId == "response").ExecuteUpdateAsync(s => s.SetProperty(r => r.BreachedAt, breachedAt)), default);
        var metricService = f.Get<Kernel.Infrastructure.Metrics.MetricService>();
        var breachesBefore = (await metricService.EvaluateAsync(agent, "core.tickets.metric.sla_breached", null, default)).Count;
        Assert.True(breachesBefore >= 1);

        var upgrades = f.Get<UpgradeService>();
        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.10.0", default);
        Assert.True(plan.Compatible, string.Join("; ", plan.Errors.Select(e => e.Message)));
        Assert.All(plan.Overlay, o => Assert.Equal(OpStatus.Applied, o.Status));
        Assert.Contains(plan.BaseChanges, c => c.Id == "core.tickets.sla.ticket");
        Assert.Contains(plan.BaseChanges, c => c.Id == "core.tickets.calendar.business_hours");
        var started = await upgrades.StartAsync(admin, "core.tickets", "1.10.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        Assert.Equal("activated", (await upgrades.GetAsync(admin, started.Id, default)).Status);

        // The running clock is untouched: same goal, due time, configuration revision and calendar snapshot.
        var kept = await ResolutionAsync(before.Id);
        Assert.Equal((running.Id, running.GoalSeconds, running.DueAt, running.ConfigRevision, running.Snapshot), (kept.Id, kept.GoalSeconds, kept.DueAt, kept.ConfigRevision, kept.Snapshot));
        Assert.DoesNotContain("2026-11-27", kept.Snapshot, StringComparison.Ordinal);

        // Changing the running ticket does not move it onto the new goal either.
        var touched = await records.UpdateAsync(agent, Ticket, before.Id, before.Revision, new JsonObject { ["title"] = "Laptop very slow" }, null, null, default);
        Assert.Equal(600 * 60, (await ResolutionAsync(touched.Id)).GoalSeconds);

        // A ticket opened after the upgrade runs on the new policy and calendar.
        var after = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Dock flickers", ["requester"] = dana, ["priority"] = "p2" }, null, null, default);
        var fresh = await ResolutionAsync(after.Id);
        Assert.Equal(480 * 60, fresh.GoalSeconds);
        Assert.Contains("2026-11-27", fresh.Snapshot, StringComparison.Ordinal);
        Assert.True(fresh.ConfigRevision > running.ConfigRevision);

        // The customization is still there, and the new Overview figures evaluate for agents.
        Assert.Equal("Contoso", (await records.GetAsync(admin, Ticket, before.Id, default)).Data["tenant__vendor"]!.GetValue<string>());
        var metrics = f.Get<Kernel.Infrastructure.Metrics.MetricService>();
        var dashboard = await metrics.DashboardAsync(agent, "core.tickets.dashboard", "UTC", default);
        Assert.Contains(dashboard.Metrics, m => m.Id == "core.tickets.metric.sla_at_risk" && m.Status == "ok");
        Assert.Contains(dashboard.Metrics, m => m.Id == "core.tickets.metric.alert_mtta" && m.Source == Kernel.Infrastructure.Metrics.MetricSources.Alerts);
        Assert.Contains(dashboard.Metrics, m => m.Id == "core.tickets.metric.alert_mttr" && m.Status == "ok");
        var breachedTimer = await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.SlaTimers.AsNoTracking().SingleAsync(s => s.RecordId == breachedTicket.Id && s.TargetId == "response"), default);
        Assert.Equal(breachedAt.ToUnixTimeMilliseconds(), breachedTimer.BreachedAt!.Value.ToUnixTimeMilliseconds());
        var breachesAfter = await metricService.EvaluateAsync(agent, "core.tickets.metric.sla_breached", null, default);
        Assert.Equal(breachesBefore, breachesAfter.Count);
        Assert.Contains((await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Filters = breachesAfter.Drill!.Filters, Sla = [.. breachesAfter.Drill.Sla.Select(SlaFilter.Parse)] }, default)).Items, i => i.Id == breachedTicket.Id);
    }

    [Fact]
    public async Task Upgrade_preserves_customizations_and_recovers_from_a_partial_migration_failure()
    {
        var t = await f.NewTenantAsync("tu");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var v = draft.Version;
        foreach (var op in ConfigLifecycleTests.Customization())
        {
            v = (await drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var state = await drafts.CurrentAsync(admin, default);
        await f.Get<PublishService>().PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);

        // 450 more tickets with free-text effort, some not convertible, so the conversion needs several batches.
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, async db =>
        {
            var artifact = await f.Get<ConfigStore>().GetActiveAsync(db, t.TenantId, t.Environments["prod"], default);
            var entity = artifact.Config.FindEntity(Ticket)!;
            for (var i = 0; i < 450; i++)
            {
                var effort = i % 50 == 0 ? $"about {i % 7}h" : (i % 9 * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await records.SystemCreateAsync(db, agent, artifact, entity, new JsonObject { ["title"] = $"Bulk ticket {i}", ["priority"] = i % 3 == 0 ? "p1" : "p3", ["requester"] = t.Users["dana"].ToString(), ["estimated_effort"] = effort }, default);
            }
        }, default);

        var upgrades = new UpgradeService(f.Get<Database>(), f.Get<ConfigStore>(), f.Get<ConfigActivator>(), f.Get<ConfigCompiler>(), f.Get<ModuleCatalog>(), f.Get<Kernel.Infrastructure.Identity.AccessService>(),
            new MigrationExecutor(f.Get<Database>(), f.Get<Kernel.Records.FieldNormalizer>(), new FailOnce("convert_effort", 1)));

        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.1.0", default);
        Assert.True(plan.Compatible);
        Assert.All(plan.Overlay, o => Assert.Equal(OpStatus.Applied, o.Status));
        Assert.Contains(plan.BaseChanges, c => c.Id == $"{Ticket}/impact" && c.Change == "added");
        Assert.Equal(["backfill_impact", "convert_effort"], plan.Migrations.Select(m => m.Id));

        var started = await upgrades.StartAsync(admin, "core.tickets", "1.1.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        var failed = await upgrades.GetAsync(admin, started.Id, default);
        Assert.Equal("failed", failed.Status);
        var convert = failed.Runs.Single(r => r.MigrationId == "convert_effort");
        Assert.Equal("failed", convert.Status);
        Assert.Equal(200, convert.Processed);
        Assert.Equal("completed", failed.Runs.Single(r => r.MigrationId == "backfill_impact").Status);

        // The old configuration is still active and fully usable while the upgrade waits.
        var app = await f.Get<ExperienceService>().EntityAsync(admin, Ticket, default);
        Assert.DoesNotContain(app.Entity.Fields, x => x.Key == "impact");
        await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Still works mid-upgrade", ["requester"] = t.Users["dana"].ToString() }, null, null, default);

        await upgrades.ResumeAsync(admin, started.Id, default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        var done = await upgrades.GetAsync(admin, started.Id, default);
        Assert.Equal("activated", done.Status);
        var resumed = done.Runs.Single(r => r.MigrationId == "convert_effort");
        Assert.Equal("completed", resumed.Status);
        Assert.True(resumed.Processed >= 459);
        Assert.True(resumed.Invalid >= 9);
        Assert.Contains(resumed.Issues, i => i.Value!.StartsWith("about", StringComparison.Ordinal));

        var after = await f.Get<ExperienceService>().EntityAsync(admin, Ticket, default);
        Assert.Contains(after.Entity.Fields, x => x.Key == "impact");
        Assert.Contains(after.Entity.Fields, x => x.Key == "tenant__vendor");
        var view = await f.Get<ExperienceService>().ViewAsync(admin, "core.tickets.record", default);
        Assert.NotNull(view.View.Layout.Find("tenant__n_vendor_card"));

        var bulk = await records.QueryAsync(admin, new RecordQuery { Entity = Ticket, Filters = ["record.title.startsWith('Bulk ticket 50')"] }, default);
        var invalidTicket = bulk.Items.Single(r => r.Data["title"]!.GetValue<string>() == "Bulk ticket 50");
        Assert.False(invalidTicket.Data.ContainsKey("effort_hours"));
        var p1 = await records.QueryAsync(admin, new RecordQuery { Entity = Ticket, Filters = ["record.priority == 'p1'", "record.title.startsWith('Bulk')"], PageSize = 5 }, default);
        Assert.All(p1.Items, r => Assert.Equal("high", r.Data["impact"]!.GetValue<string>()));

        var history = await f.Get<PublishService>().RevisionsAsync(admin, default);
        var preUpgrade = history.First(h => h.Modules.Any(m => m.Id == "core.tickets" && m.Version == "1.0.0"));
        Assert.False(preUpgrade.CanRollback);
        var unsafeRollback = await Assert.ThrowsAsync<KernelException>(() => f.Get<PublishService>().RollbackAsync(admin, preUpgrade.Revision, history[0].Revision, default));
        Assert.Equal("rollback_unsafe", unsafeRollback.Code);
    }

    [Fact]
    public async Task Conflicting_customization_blocks_the_upgrade_with_an_impact_report()
    {
        var t = await f.NewTenantAsync("tv");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyAsync(admin, new UpdateField
        {
            Entity = Ticket,
            Field = "estimated_effort",
            Set = new FieldPatch { Help = "Whole hours only" },
            Expected = new JsonObject { ["help"] = "Hours, e.g. 4 or 2.5" },
        }, draft.Version, "ui", default);
        await f.Get<PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);

        var upgrades = f.Get<UpgradeService>();
        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.1.0", default);
        Assert.False(plan.Compatible);
        var conflict = Assert.Single(plan.Overlay, o => o.Status == OpStatus.Conflict);
        Assert.Contains("help", conflict.Diagnostics[0].Message);

        var blocked = await Assert.ThrowsAsync<KernelException>(() => upgrades.StartAsync(admin, "core.tickets", "1.1.0", default));
        Assert.Equal("upgrade_blocked", blocked.Code);
        var revisions = await f.Get<PublishService>().RevisionsAsync(admin, default);
        Assert.All(revisions, r => Assert.Contains(r.Modules, m => m.Id == "core.tickets" && m.Version == "1.0.0"));
    }

    [Fact]
    public async Task Requesters_and_agents_cannot_manage_module_versions()
    {
        var t = await f.NewTenantAsync("tw");
        var ex = await Assert.ThrowsAsync<KernelException>(() => f.Get<UpgradeService>().PlanAsync(f.As(t, "builder"), "core.tickets", "1.1.0", default));
        Assert.Equal(403, ex.Status);
    }
}
