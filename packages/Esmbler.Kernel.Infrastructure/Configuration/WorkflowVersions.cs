using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <summary>
/// Registered workflow definition versions (ADR 0015, REQ-MIG-003). A version is identified by the content hash of the
/// parts that decide behavior (state field, initial state, states, transitions); a definition whose content returns to
/// an earlier hash reuses that version. Version numbers are always read from the database (a registration inside a
/// transaction that rolls back must not leak into a cache); parsed definitions are cached by content hash, which is
/// safe because equal content means an equal definition.
/// </summary>
public sealed class WorkflowVersions
{
    private const int CacheLimit = 5000;
    private readonly ConcurrentDictionary<string, WorkflowSpec> _definitions = new(StringComparer.Ordinal);

    /// <summary>Content hash of a workflow's behavior. Labels, tones and the active-instance policy are not part of it.</summary>
    public static string Hash(WorkflowSpec wf)
    {
        var node = new JsonObject
        {
            ["id"] = wf.Id,
            ["entity"] = wf.Entity,
            ["stateField"] = wf.StateField,
            ["initialState"] = wf.InitialState,
            ["states"] = SpecJson.ToNode(wf.States.Select(s => new { s.Id, s.Category }).ToList()),
            ["transitions"] = SpecJson.ToNode(wf.Transitions.Select(t => new { t.Id, t.From, t.To, t.Guard, t.RequiredFields, t.Roles, t.Set, t.Enabled, t.System }).ToList()),
        };
        return Composer.Hash(SpecJson.Canonical(node));
    }

    /// <summary>Registers every workflow of <paramref name="config"/> and returns workflow ID → version.</summary>
    public async Task<IReadOnlyDictionary<string, int>> RegisterAsync(KernelDbContext db, Guid tenantId, Guid environmentId, EffectiveConfig config, int revision, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var wf in config.Workflows)
        {
            result[wf.Id] = await VersionOfAsync(db, tenantId, environmentId, wf, revision, ct);
        }

        return result;
    }

    /// <summary>The registered version of <paramref name="wf"/> as it stands, registering it if new.</summary>
    public async Task<int> VersionOfAsync(KernelDbContext db, Guid tenantId, Guid environmentId, WorkflowSpec wf, int revision, CancellationToken ct)
    {
        var hash = Hash(wf);
        int? version = null;
        for (var attempt = 0; attempt < 5 && version is null; attempt++)
        {
            version = await db.WorkflowDefinitions.AsNoTracking()
                .Where(d => d.WorkflowId == wf.Id && d.ContentHash == hash)
                .Select(d => (int?)d.Version)
                .FirstOrDefaultAsync(ct);
            if (version is not null)
            {
                break;
            }

            var next = (await db.WorkflowDefinitions.Where(d => d.WorkflowId == wf.Id).MaxAsync(d => (int?)d.Version, ct) ?? 0) + 1;
            var definition = SpecJson.Serialize(wf);

            // ON CONFLICT DO NOTHING: a concurrent registration of the same content (or of the same version number) wins
            // without aborting this transaction; the loop then reads what won.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO kernel.workflow_definitions (tenant_id, environment_id, workflow_id, version, content_hash, definition, config_revision) VALUES ({tenantId}, {environmentId}, {wf.Id}, {next}, {hash}, {definition}::jsonb, {revision}) ON CONFLICT DO NOTHING",
                ct);
        }

        if (version is null)
        {
            throw new InvalidOperationException($"Could not register a version of workflow '{wf.Id}'.");
        }

        return version.Value;
    }

    /// <summary>The definition of <paramref name="workflowId"/> at <paramref name="version"/>, or null if unknown.</summary>
    public async Task<WorkflowSpec?> DefinitionAsync(KernelDbContext db, string workflowId, int version, CancellationToken ct)
    {
        var hash = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.WorkflowId == workflowId && d.Version == version)
            .Select(d => d.ContentHash)
            .FirstOrDefaultAsync(ct);
        if (hash is null)
        {
            return null;
        }

        if (_definitions.TryGetValue(hash, out var cached))
        {
            return cached;
        }

        var json = await db.WorkflowDefinitions.AsNoTracking().Where(d => d.WorkflowId == workflowId && d.Version == version).Select(d => d.Definition).FirstAsync(ct);
        var spec = SpecJson.Deserialize<WorkflowSpec>(json);
        if (_definitions.Count < CacheLimit)
        {
            _definitions[hash] = spec;
        }

        return spec;
    }

    /// <summary>
    /// The workflow a record runs on: its pinned definition when that differs from the current one, otherwise null
    /// (meaning the current configuration's workflow). Also returns the version to store on the record.
    /// </summary>
    public async Task<(WorkflowSpec? Pinned, int? Version)> ForRecordAsync(KernelDbContext db, ActiveArtifact artifact, EntitySpec entity, int? pinnedVersion, CancellationToken ct)
    {
        if (artifact.Config.WorkflowFor(entity.Id) is not { } current)
        {
            return (null, pinnedVersion);
        }

        var currentVersion = await VersionOfAsync(db, artifact.TenantId, artifact.EnvironmentId, current, artifact.Revision, ct);
        if (pinnedVersion is not { } pinned || pinned == currentVersion)
        {
            return (null, pinnedVersion ?? currentVersion);
        }

        return (await DefinitionAsync(db, current.Id, pinned, ct), pinned);
    }
}
