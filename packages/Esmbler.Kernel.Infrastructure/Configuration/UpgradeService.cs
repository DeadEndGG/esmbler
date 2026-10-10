using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

public sealed record MigrationPlan(string Id, string Kind, string Description, int Affected);

public sealed record UpgradeReport(
    string Module,
    string FromVersion,
    string ToVersion,
    bool Compatible,
    IReadOnlyList<OpOutcome> Overlay,
    IReadOnlyList<Diagnostic> Errors,
    IReadOnlyList<string> AffectedCustomizations,
    IReadOnlyList<ConfigChange> BaseChanges,
    IReadOnlyList<MigrationPlan> Migrations);

public sealed record MigrationRunView(string MigrationId, string Status, int Processed, int Converted, int Invalid, string? LastError, IReadOnlyList<MigrationIssue> Issues);

public sealed record UpgradeView(Guid Id, string Module, string FromVersion, string ToVersion, string Status, UpgradeReport Report, IReadOnlyList<MigrationRunView> Runs, int? ActivatedRevision);

/// <summary>
/// Module upgrades (REQ-CFG-009, REQ-MIG-001/002, REQ-ACC-03/04): compose the candidate base beneath the tenant
/// overlay, report compatible, redundant, and conflicting changes with their dependency impact, run data migrations
/// (expand/backfill) before switching the pointer, and never activate while a migration is incomplete.
/// </summary>
public sealed class UpgradeService(Database database, ConfigStore configs, ConfigActivator activator, ConfigCompiler compiler, ModuleCatalog catalog, AccessService access, MigrationExecutor executor)
{
    public const string JobKind = "upgrade.run";

    public Task<UpgradeReport> PlanAsync(RequestContext ctx, string moduleId, string toVersion, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var published = await RequireManageAsync(db, ctx, ct);
            return await PlanCoreAsync(db, published, moduleId, toVersion, ct);
        }, ct);

    public Task<UpgradeView> StartAsync(RequestContext ctx, string moduleId, string toVersion, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var published = await RequireManageAsync(db, ctx, ct);
            if (await db.Upgrades.AnyAsync(u => u.Status == "migrating" || u.Status == "failed", ct))
            {
                throw KernelException.Conflict("upgrade_in_progress", "Another upgrade is in progress or waiting to resume.", suggestion: "Resume or finish it first.");
            }

            if (await db.Drafts.AnyAsync(d => d.Status == "open", ct))
            {
                throw KernelException.Conflict("draft_open", "Publish or discard the open draft before upgrading.");
            }

            var report = await PlanCoreAsync(db, published, moduleId, toVersion, ct);
            var row = new UpgradeRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = Guid.NewGuid(),
                ModuleId = moduleId,
                FromVersion = report.FromVersion,
                ToVersion = toVersion,
                Status = report.Compatible ? "migrating" : "blocked",
                Report = JsonSerializer.Serialize(report, SpecJson.Options),
                CreatedBy = ctx.PrincipalId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Upgrades.Add(row);
            AuditWriter.Add(db, ctx, "module.upgrade.start", "module", moduleId, published.Revision, new { report.FromVersion, toVersion, report.Compatible });
            if (!report.Compatible)
            {
                await db.SaveChangesAsync(ct);
                throw KernelException.Conflict("upgrade_blocked", $"Upgrading {moduleId} to {toVersion} conflicts with your customizations. Nothing was changed.", report, "Resolve the listed conflicts in a draft, then plan the upgrade again.");
            }

            foreach (var m in report.Migrations)
            {
                db.MigrationRuns.Add(new MigrationRunRow { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, Id = Guid.NewGuid(), UpgradeId = row.Id, MigrationId = m.Id, Status = "pending", StartedAt = DateTimeOffset.UtcNow });
            }

            EnqueueJob(db, ctx, row.Id);
            await db.SaveChangesAsync(ct);
            return await ViewAsync(db, row.Id, ct);
        }, ct);

    public Task<UpgradeView> ResumeAsync(RequestContext ctx, Guid upgradeId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var row = await db.Upgrades.AsTracking().FirstOrDefaultAsync(u => u.Id == upgradeId, ct) ?? throw KernelException.NotFound("Upgrade");
            if (row.Status != "failed")
            {
                throw KernelException.Conflict("not_resumable", $"The upgrade is {row.Status}; only failed upgrades can be resumed.");
            }

            row.Status = "migrating";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.MigrationRuns.Where(r => r.UpgradeId == upgradeId && r.Status == "failed").ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "pending").SetProperty(r => r.LastError, (string?)null), ct);
            EnqueueJob(db, ctx, upgradeId);
            AuditWriter.Add(db, ctx, "module.upgrade.resume", "module", row.ModuleId, null, new { upgradeId });
            await db.SaveChangesAsync(ct);
            return await ViewAsync(db, upgradeId, ct);
        }, ct);

    public Task<UpgradeView> GetAsync(RequestContext ctx, Guid upgradeId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            return await ViewAsync(db, upgradeId, ct);
        }, ct);

    public Task<IReadOnlyList<object>> AvailableAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<object>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            return published.Lockfile.Where(m => m.Id != PlatformModule.ModuleId).Select(m =>
            {
                var latest = catalog.Latest(m.Id).Version;
                return (object)new { module = m.Id, installed = m.Version, latest, upgradeAvailable = SemVer.Parse(latest) > SemVer.Parse(m.Version) };
            }).ToList();
        }, ct);

    /// <summary>Marks an upgrade failed without running it (its job was denied on re-authorization). Nothing was changed.</summary>
    public Task AbandonAsync(Guid tenantId, Guid environmentId, Guid upgradeId, string reason, CancellationToken ct) =>
        database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            await db.Upgrades.Where(u => u.Id == upgradeId && u.Status != "activated").ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, "failed").SetProperty(u => u.UpdatedAt, DateTimeOffset.UtcNow), ct);
            await db.MigrationRuns.Where(r => r.UpgradeId == upgradeId && r.Status != "completed").ExecuteUpdateAsync(s => s.SetProperty(r => r.LastError, reason), ct);
            return 0;
        }, ct);

    /// <summary>Executes an upgrade's migrations then activates the new version. Called by the job runner (and tests).</summary>
    public async Task RunAsync(Guid tenantId, Guid environmentId, Guid upgradeId, CancellationToken ct)
    {
        var (row, runs, candidate, migrations) = await database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            var u = await db.Upgrades.FirstAsync(x => x.Id == upgradeId, ct);
            var published = await configs.GetActiveAsync(db, tenantId, environmentId, ct);
            var result = Candidate(published, u.ModuleId, u.ToVersion);
            var r = await db.MigrationRuns.Where(x => x.UpgradeId == upgradeId).ToListAsync(ct);
            return (u, r, result, catalog.Release(u.ModuleId, u.ToVersion).Migrations);
        }, ct);

        if (row.Status != "migrating")
        {
            return;
        }

        foreach (var spec in migrations)
        {
            var run = runs.First(r => r.MigrationId == spec.Id);
            try
            {
                await executor.RunAsync(tenantId, environmentId, run.Id, spec, candidate.Config, 0, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The active configuration is untouched: the old version keeps working; the new field values written
                // so far are invisible to it (expand phase). An operator resumes from the checkpoint.
                await database.InTenantAsync(tenantId, environmentId, null, async db =>
                {
                    await db.MigrationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "failed").SetProperty(r => r.LastError, ex.Message), ct);
                    await db.Upgrades.Where(u => u.Id == upgradeId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, "failed").SetProperty(u => u.UpdatedAt, DateTimeOffset.UtcNow), ct);
                }, ct);
                return;
            }
        }

        await database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            var published = await configs.GetActiveAsync(db, tenantId, environmentId, ct);
            var result = Candidate(published, row.ModuleId, row.ToVersion);
            if (!result.Succeeded)
            {
                await db.Upgrades.Where(u => u.Id == upgradeId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, "blocked").SetProperty(u => u.UpdatedAt, DateTimeOffset.UtcNow), ct);
                return;
            }

            var revision = await activator.ActivateAsync(db, tenantId, environmentId, result, published.Overlay, "upgrade", published.Revision, row.CreatedBy, row.CreatedBy, ct);
            await db.Installations.Where(i => i.ModuleId == row.ModuleId).ExecuteUpdateAsync(s => s.SetProperty(i => i.Version, row.ToVersion), ct);
            await db.Upgrades.Where(u => u.Id == upgradeId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, "activated").SetProperty(u => u.UpdatedAt, DateTimeOffset.UtcNow), ct);
            db.AuditLog.Add(new AuditRow { TenantId = tenantId, EnvironmentId = environmentId, At = DateTimeOffset.UtcNow, Actor = row.CreatedBy, Channel = "worker", Operation = "module.upgrade.activate", TargetType = "module", TargetId = row.ModuleId, ConfigRevision = revision, Outcome = "success", Details = JsonSerializer.Serialize(new { row.FromVersion, row.ToVersion }) });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    private async Task<UpgradeReport> PlanCoreAsync(KernelDbContext db, ActiveArtifact published, string moduleId, string toVersion, CancellationToken ct)
    {
        var from = published.Lockfile.FirstOrDefault(m => m.Id == moduleId) ?? throw KernelException.NotFound($"Installed module '{moduleId}'");
        var target = catalog.Release(moduleId, toVersion);
        if (SemVer.Parse(toVersion) <= SemVer.Parse(from.Version))
        {
            throw new KernelException("not_an_upgrade", $"{moduleId} {toVersion} is not newer than the installed {from.Version}.", 400);
        }

        var oldBase = Composer.ComposeBase(published.Lockfile.Select(m => catalog.Release(m.Id, m.Version)).ToList());
        var newBase = Composer.ComposeBase(published.Lockfile.Select(m => m.Id == moduleId ? target : catalog.Release(m.Id, m.Version)).ToList());
        var candidate = Candidate(published, moduleId, toVersion);
        var errors = candidate.Errors.ToList();
        var graph = DependencyGraph.Build(published.Config);
        var removed = ConfigDiff.Compare(oldBase, newBase).Where(c => c.Change is "removed" or "changed").Select(c => c.Id);
        var affected = removed.SelectMany(graph.ImpactOf).Concat(errors.Select(e => e.Target).OfType<string>())
            .Where(k => Sdk.Overlay.Namespaces.IsTenantOwned(k) || published.Config.Customized.Contains(k)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var migrations = new List<MigrationPlan>();
        foreach (var m in target.Migrations)
        {
            var affectedRecords = await db.Records.CountAsync(r => r.EntityId == m.Entity && r.DeletedAt == null, ct);
            migrations.Add(new MigrationPlan(m.Id, m.Kind, m.Description ?? m.Id, affectedRecords));
        }

        var overlay = candidate.Composition.Outcomes;
        return new UpgradeReport(moduleId, from.Version, toVersion, candidate.Succeeded && overlay.All(o => o.Status is OpStatus.Applied or OpStatus.Redundant or OpStatus.Reset), overlay, errors, affected, ConfigDiff.Compare(oldBase, newBase), migrations);
    }

    private CompileResult Candidate(ActiveArtifact published, string moduleId, string toVersion) =>
        compiler.Compile(published.Lockfile.Select(m => m.Id == moduleId ? catalog.Release(moduleId, toVersion) : catalog.Release(m.Id, m.Version)).ToList(), published.Overlay, published.Config);

    private async Task<ActiveArtifact> RequireManageAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, published.Config, ct, fresh: true);
        if (!perms.Can(PlatformActions.ModulesManage))
        {
            throw KernelException.Forbidden("Managing module versions requires the modules.manage right (tenant administrators).");
        }

        return published;
    }

    private static void EnqueueJob(KernelDbContext db, RequestContext ctx, Guid upgradeId) =>
        db.Jobs.Add(new JobRow
        {
            Id = Guid.NewGuid(),
            TenantId = ctx.TenantId,
            EnvironmentId = ctx.EnvironmentId,
            Kind = JobKind,
            Payload = new JsonObject { ["upgradeId"] = upgradeId.ToString() }.ToJsonString(),
            MaxAttempts = 1,
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedBy = ctx.PrincipalId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    private static async Task<UpgradeView> ViewAsync(KernelDbContext db, Guid upgradeId, CancellationToken ct)
    {
        var row = await db.Upgrades.FirstOrDefaultAsync(u => u.Id == upgradeId, ct) ?? throw KernelException.NotFound("Upgrade");
        var runs = await db.MigrationRuns.Where(r => r.UpgradeId == upgradeId).OrderBy(r => r.StartedAt).ToListAsync(ct);
        int? revision = row.Status == "activated" ? await db.ActiveConfig.Select(a => (int?)a.Revision).FirstOrDefaultAsync(ct) : null;
        return new UpgradeView(
            row.Id,
            row.ModuleId,
            row.FromVersion,
            row.ToVersion,
            row.Status,
            JsonSerializer.Deserialize<UpgradeReport>(row.Report, SpecJson.Options)!,
            runs.Select(r => new MigrationRunView(r.MigrationId, r.Status, r.Processed, r.Converted, r.Invalid, r.LastError, JsonSerializer.Deserialize<List<MigrationIssue>>(r.Report) ?? [])).ToList(),
            revision);
    }
}
