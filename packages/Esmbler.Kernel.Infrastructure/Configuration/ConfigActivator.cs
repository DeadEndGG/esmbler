using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <summary>
/// Stores a compiled artifact immutably and atomically switches the environment's active pointer, guarded by the
/// expected current revision (optimistic concurrency, REQ-CFG-007). The previous artifact stays for rollback.
/// </summary>
public sealed class ConfigActivator(ConfigCompiler compiler, ModuleCatalog catalog, IActivationHealthCheck health, WorkflowVersions? workflows = null)
{
    public CompileResult Compile(IReadOnlyList<(string ModuleId, string Version)> modules, IReadOnlyList<OverlayOp> overlay, EffectiveConfig? previous = null) =>
        compiler.Compile(modules.Select(m => catalog.Release(m.ModuleId, m.Version)).ToList(), overlay, previous);

    public async Task<int> ActivateAsync(
        KernelDbContext db,
        Guid tenantId,
        Guid environmentId,
        CompileResult result,
        IReadOnlyList<OverlayOp> overlay,
        string source,
        int? expectedRevision,
        Guid? createdBy,
        Guid? approvedBy,
        CancellationToken ct)
    {
        if (!result.Succeeded)
        {
            throw new KernelException("config_invalid", "The configuration has errors and cannot be activated.", 422, details: result.Errors.ToList());
        }

        var active = await db.ActiveConfig.AsTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.EnvironmentId == environmentId, ct);
        if ((active?.Revision) != expectedRevision)
        {
            throw KernelException.Conflict(
                "publish_conflict",
                $"Someone published revision {active?.Revision} after you started (you expected {expectedRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}). Review the newer changes and publish again.",
                new { expectedRevision, currentRevision = active?.Revision });
        }

        var maxRevision = await db.ConfigArtifacts.Where(a => a.TenantId == tenantId && a.EnvironmentId == environmentId).MaxAsync(a => (int?)a.Revision, ct) ?? 0;
        var revision = maxRevision + 1;
        db.ConfigArtifacts.Add(new ConfigArtifactRow
        {
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Revision = revision,
            ContentHash = result.Manifest!.ContentHash,
            Manifest = JsonSerializer.Serialize(result.Manifest, SpecJson.Options),
            Config = SpecJson.Serialize(result.Config),
            Overlay = SpecJson.Serialize(overlay.ToList()),
            Status = "active",
            Source = source,
            CreatedBy = createdBy,
            ApprovedBy = approvedBy,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        if (active is null)
        {
            db.ActiveConfig.Add(new ActiveConfigRow { TenantId = tenantId, EnvironmentId = environmentId, Revision = revision, ActivatedAt = DateTimeOffset.UtcNow, ActivatedBy = createdBy });
        }
        else
        {
            await db.ConfigArtifacts.Where(a => a.TenantId == tenantId && a.EnvironmentId == environmentId && a.Revision == active.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "superseded"), ct);
            active.PreviousRevision = active.Revision;
            active.Revision = revision;
            active.ActivatedAt = DateTimeOffset.UtcNow;
            active.ActivatedBy = approvedBy ?? createdBy;
        }

        await RegisterWorkflowsAsync(db, tenantId, environmentId, active?.PreviousRevision, result.Config, revision, approvedBy ?? createdBy, ct);

        db.Outbox.Add(new OutboxRow
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Type = EventTypes.ConfigPublished,
            Event = JsonSerializer.Serialize(new { type = EventTypes.ConfigPublished, revision, source, contentHash = result.Manifest.ContentHash }, SpecJson.Options),
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        // Health check inside the activation transaction: throwing here rolls everything back, so the previous
        // revision stays live and nothing half-activated is ever visible.
        var problems = await health.CheckAsync(db, tenantId, environmentId, revision, ct);
        if (problems.Count > 0)
        {
            throw new KernelException("activation_unhealthy", $"Revision {revision} failed its health check and was not activated; the previous revision stays live. {problems[0]}", 422, details: problems);
        }

        return revision;
    }

    /// <summary>
    /// REQ-MIG-003: registers the new revision's workflow versions inside the activation transaction. Records that were
    /// never pinned are first pinned to the definition of the revision they ran on, so a change never silently applies
    /// to them. When a workflow gets a new version and its policy is migrate or cancel, a durable job moves running
    /// instances after commit.
    /// </summary>
    private async Task RegisterWorkflowsAsync(KernelDbContext db, Guid tenantId, Guid environmentId, int? previousRevision, EffectiveConfig config, int revision, Guid? actor, CancellationToken ct)
    {
        if (workflows is null)
        {
            return;
        }

        IReadOnlyDictionary<string, int> before = new Dictionary<string, int>();
        if (previousRevision is { } prev)
        {
            var previous = await db.ConfigArtifacts.AsNoTracking().Where(a => a.Revision == prev).Select(a => a.Config).FirstOrDefaultAsync(ct);
            if (previous is not null)
            {
                var previousConfig = SpecJson.Deserialize<EffectiveConfig>(previous);
                before = await workflows.RegisterAsync(db, tenantId, environmentId, previousConfig, prev, ct);
                foreach (var wf in previousConfig.Workflows)
                {
                    var version = before[wf.Id];
                    await db.Records.Where(r => r.EntityId == wf.Entity && r.WorkflowVersion == null && r.DeletedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.WorkflowVersion, version), ct);
                }
            }
        }

        var after = await workflows.RegisterAsync(db, tenantId, environmentId, config, revision, ct);
        foreach (var wf in config.Workflows)
        {
            var changed = before.TryGetValue(wf.Id, out var old) && old != after[wf.Id];
            if (changed && wf.ActiveInstances.Policy is ActiveInstancePolicies.Migrate or ActiveInstancePolicies.Cancel)
            {
                await JobScheduler.ScheduleAsync(db, tenantId, environmentId, new JobRequest(WorkflowMigrationJobHandler.JobKind, new JsonObject
                {
                    ["workflow"] = wf.Id,
                    ["toVersion"] = after[wf.Id],
                    ["revision"] = revision,
                })
                {
                    DedupKey = $"workflow.migrate:{wf.Id}",
                    ReplaceExisting = true,
                    CreatedBy = actor,
                }, ct);
            }
        }
    }
}
