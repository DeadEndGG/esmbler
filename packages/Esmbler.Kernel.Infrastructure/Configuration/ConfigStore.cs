using System.Collections.Concurrent;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <summary>An immutable, activated configuration artifact.</summary>
public sealed record ActiveArtifact(Guid TenantId, Guid EnvironmentId, int Revision, string ContentHash, EffectiveConfig Config, IReadOnlyList<OverlayOp> Overlay, IReadOnlyList<LockedModule> Lockfile);

/// <summary>
/// Reads activated artifacts. Artifacts are immutable, so the cache key includes the revision (REQ-CFG-008):
/// a request pins the revision it read first and never observes a half-propagated change.
/// </summary>
public sealed class ConfigStore
{
    private readonly ConcurrentDictionary<(Guid, Guid, int), ActiveArtifact> _artifacts = new();

    public async Task<int> ActiveRevisionAsync(KernelDbContext db, Guid tenantId, Guid environmentId, CancellationToken cancellationToken) =>
        await db.ActiveConfig.Where(a => a.TenantId == tenantId && a.EnvironmentId == environmentId).Select(a => (int?)a.Revision).FirstOrDefaultAsync(cancellationToken)
        ?? throw new KernelException("not_provisioned", "This environment has no active configuration yet.", 409);

    public async Task<ActiveArtifact> GetActiveAsync(KernelDbContext db, Guid tenantId, Guid environmentId, CancellationToken cancellationToken)
    {
        var revision = await ActiveRevisionAsync(db, tenantId, environmentId, cancellationToken);
        return await GetAsync(db, tenantId, environmentId, revision, cancellationToken);
    }

    public async Task<ActiveArtifact> GetAsync(KernelDbContext db, Guid tenantId, Guid environmentId, int revision, CancellationToken cancellationToken)
    {
        if (_artifacts.TryGetValue((tenantId, environmentId, revision), out var cached))
        {
            return cached;
        }

        var row = await db.ConfigArtifacts.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.EnvironmentId == environmentId && a.Revision == revision, cancellationToken)
            ?? throw KernelException.NotFound($"Configuration revision {revision}");
        var config = SpecJson.Deserialize<EffectiveConfig>(row.Config);
        var artifact = new ActiveArtifact(tenantId, environmentId, revision, row.ContentHash, config, SpecJson.Deserialize<List<OverlayOp>>(row.Overlay), config.Modules);
        _artifacts[(tenantId, environmentId, revision)] = artifact;
        return artifact;
    }
}
