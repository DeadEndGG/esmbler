using System.Text.Json;
using Esmbler.Kernel.Configuration;
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
public sealed class ConfigActivator(ConfigCompiler compiler, ModuleCatalog catalog, IActivationHealthCheck health)
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
}
