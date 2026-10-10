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
