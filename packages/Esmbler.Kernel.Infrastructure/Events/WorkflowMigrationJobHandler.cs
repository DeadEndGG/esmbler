using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Moves running workflow instances to a newly published definition version (REQ-MIG-003, ADR 0015) according to the
/// workflow's active-instance policy: <c>migrate</c> maps each state through the state map (or keeps a state that still
/// exists), <c>cancel</c> moves instances to the cancel state. Instances in resolved, closed or cancelled states, and
/// instances whose state has no target, stay pinned to the version they run on and are reported. Batches are
/// checkpointed and idempotent: a record already on the target version is never touched twice. Each move is audited
/// and emits a transition event, so consumers (fulfillment, notifications) see it like any other state change.
/// </summary>
public sealed class WorkflowMigrationJobHandler(ConfigStore configs, WorkflowVersions workflows) : IJobHandler
{
    public const string JobKind = "workflow.migrate";
    private const int BatchSize = 200;

    public string Kind => JobKind;

    /// <summary>
    /// The publish that set the policy is the approval (the review showed the instance counts); the runner re-checks that
    /// the publisher still may publish before each run, so revoked rights stop the move (REQ-ACC-07).
    /// </summary>
    public string? RequiredAction => PlatformActions.ConfigPublish;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var workflowId = job.Payload["workflow"]!.GetValue<string>();
        var toVersion = job.Payload["toVersion"]!.GetValue<int>();
        var checkpoint = job.Checkpoint as JsonObject ?? new JsonObject { ["after"] = Guid.Empty.ToString(), ["moved"] = 0, ["kept"] = new JsonArray() };
        while (!ct.IsCancellationRequested)
        {
            var done = await job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
            {
                var artifact = await configs.GetActiveAsync(db, job.TenantId, job.EnvironmentId, ct);
                var current = artifact.Config.Workflows.Find(w => w.Id == workflowId);
                if (current is null || await workflows.VersionOfAsync(db, job.TenantId, job.EnvironmentId, current, artifact.Revision, ct) != toVersion)
                {
                    // A later publish replaced this version; its own job (same dedup key) takes over.
                    return true;
                }

                var policy = current.ActiveInstances;
                var after = Guid.Parse(checkpoint["after"]!.GetValue<string>());
                var rows = await db.Records
                    .FromSqlInterpolated($"SELECT * FROM kernel.records WHERE entity_id = {current.Entity} AND deleted_at IS NULL AND (workflow_version IS NULL OR workflow_version <> {toVersion}) AND id > {after} ORDER BY id LIMIT {BatchSize} FOR UPDATE")
                    .AsTracking()
                    .ToListAsync(ct);
                if (rows.Count == 0)
                {
                    AuditWriter.AddSystem(db, job.TenantId, job.EnvironmentId, "workflow.migrate.completed", "workflow", workflowId, new { toVersion, policy = policy.Policy, moved = checkpoint["moved"]!.GetValue<int>(), kept = checkpoint["kept"]!.AsArray().Count }, outcome: "success");
                    await db.SaveChangesAsync(ct);
                    return true;
                }

                foreach (var row in rows)
                {
                    var data = JsonNode.Parse(row.Data)!.AsObject();
                    var state = data[current.StateField]?.GetValue<string>() ?? current.InitialState;
                    var old = row.WorkflowVersion is { } v ? await workflows.DefinitionAsync(db, job.TenantId, job.EnvironmentId, workflowId, v, ct) : null;
                    var category = (old ?? current).FindState(state)?.Category;
                    if (category is StateCategories.Resolved or StateCategories.Closed or StateCategories.Cancelled)
                    {
                        continue;
                    }

                    var target = policy.Policy == ActiveInstancePolicies.Cancel
                        ? policy.CancelState
                        : policy.StateMap.TryGetValue(state, out var mapped) ? mapped : current.FindState(state) is not null ? state : null;
                    if (target is null || current.FindState(target) is null)
                    {
                        var kept = checkpoint["kept"]!.AsArray();
                        if (kept.Count < 500)
                        {
                            kept.Add(new JsonObject { ["record"] = row.Id.ToString(), ["state"] = state, ["reason"] = "No state in the new version to move it to." });
                        }

                        continue;
                    }

                    var from = state;
                    var fromVersion = row.WorkflowVersion;
                    data[current.StateField] = target;
                    row.Data = data.ToJsonString();
                    row.Revision++;
                    row.WorkflowVersion = toVersion;
                    row.ConfigRevision = artifact.Revision;
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                    checkpoint["moved"] = checkpoint["moved"]!.GetValue<int>() + 1;
                    db.AuditLog.Add(new AuditRow
                    {
                        TenantId = job.TenantId,
                        EnvironmentId = job.EnvironmentId,
                        At = DateTimeOffset.UtcNow,
                        Actor = job.CreatedBy,
                        Channel = "worker",
                        Operation = "record.workflow_migrate",
                        TargetType = current.Entity,
                        TargetId = row.Id.ToString(),
                        ConfigRevision = artifact.Revision,
                        Outcome = "success",
                        Details = JsonSerializer.Serialize(new { workflow = workflowId, fromVersion, toVersion, policy = policy.Policy, changes = new Dictionary<string, object?> { [current.StateField] = new { from, to = target } } }),
                    });
                    EventWriter.Add(db, new EventEnvelope
                    {
                        EventId = Guid.NewGuid(),
                        Type = EventTypes.RecordTransitioned,
                        Source = EventWriter.SourceOf(artifact.Config, current.Entity),
                        Severity = EventSeverities.Info,
                        Sensitivity = EventSensitivities.Internal,
                        TenantId = job.TenantId,
                        EnvironmentId = job.EnvironmentId,
                        Entity = current.Entity,
                        RecordId = row.Id,
                        RecordRevision = row.Revision,
                        ConfigRevision = artifact.Revision,
                        OccurredAt = DateTimeOffset.UtcNow,
                        CorrelationId = job.Id,
                        Actor = job.CreatedBy is { } actor ? $"user:{actor}" : "system",
                        Payload = new JsonObject { ["changed"] = new JsonArray(current.StateField), ["from"] = from, ["to"] = target, ["transition"] = null, ["source"] = "workflow_migration" },
                    });
                }

                checkpoint["after"] = rows[^1].Id.ToString();
                await db.SaveChangesAsync(ct);
                return false;
            }, ct);

            await job.SaveCheckpointAsync(checkpoint.DeepClone(), ct);
            if (done)
            {
                return;
            }

            await job.RenewLeaseAsync(JobRunner.Lease, ct);
        }
    }
}
