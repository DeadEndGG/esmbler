using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <summary>Test and demo hook: makes a migration fail after N committed batches to prove resumability (REQ-ACC-04).</summary>
public interface IMigrationFaultInjector
{
    void AfterBatch(string migrationId, int batchesCommitted);
}

public sealed class NoFaults : IMigrationFaultInjector
{
    public void AfterBatch(string migrationId, int batchesCommitted)
    {
    }
}

/// <summary>Fails a named migration once after a number of batches (configured via Esmbler:FaultInjection:Migration = "id:batches").</summary>
public sealed class ConfiguredFaultInjector(string? setting) : IMigrationFaultInjector
{
    private readonly string? _migration = setting?.Split(':')[0];
    private readonly int _batches = setting?.Split(':') is [_, var n] && int.TryParse(n, out var b) ? b : 1;
    private bool _fired;

    public void AfterBatch(string migrationId, int batchesCommitted)
    {
        if (!_fired && migrationId == _migration && batchesCommitted >= _batches)
        {
            _fired = true;
            throw new InvalidOperationException($"Injected failure in migration '{migrationId}' after {batchesCommitted} batch(es).");
        }
    }
}

public sealed record MigrationIssue(Guid RecordId, string? Number, string? Value, string Reason);

/// <summary>
/// Executes declarative data migrations in checkpointed batches (REQ-MIG-001/002). Each batch commits its record
/// updates, audit entries, events, and the checkpoint together, so a crash resumes after the last committed batch.
/// Backfills only fill empty values; conversions never delete the source value; values that fail conversion are kept
/// and listed in the report.
/// </summary>
public sealed class MigrationExecutor(Database database, FieldNormalizer normalizer, IMigrationFaultInjector faults)
{
    public async Task RunAsync(Guid tenantId, Guid environmentId, Guid runId, MigrationSpec migration, EffectiveConfig candidate, int candidateRevisionHint, CancellationToken ct)
    {
        var entity = candidate.FindEntity(migration.Entity) ?? throw new InvalidOperationException($"Migration {migration.Id} targets unknown entity {migration.Entity}.");
        var target = entity.FindField(migration.Field) ?? throw new InvalidOperationException($"Migration {migration.Id} targets unknown field {migration.Field}.");
        var batchSize = Math.Clamp(migration.BatchSize, 1, 2000);
        var batches = 0;
        while (true)
        {
            var done = await database.InTenantAsync(tenantId, environmentId, null, async db =>
            {
                var run = await db.MigrationRuns.AsTracking().FirstAsync(r => r.Id == runId, ct);
                if (run.Status == "completed")
                {
                    return true;
                }

                run.Status = "running";
                var after = run.Checkpoint ?? Guid.Empty;
                var rows = await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE entity_id = {entity.Id} AND deleted_at IS NULL AND id > {after} ORDER BY id LIMIT {batchSize} FOR UPDATE").AsTracking().ToListAsync(ct);
                if (rows.Count == 0)
                {
                    run.Status = "completed";
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return true;
                }

                var report = JsonSerializer.Deserialize<List<MigrationIssue>>(run.Report) ?? [];
                foreach (var row in rows)
                {
                    var data = JsonNode.Parse(row.Data)!.AsObject();
                    var (changed, issue) = Apply(migration, entity, target, data, candidate);
                    if (issue is not null)
                    {
                        run.Invalid++;
                        if (report.Count < 1000)
                        {
                            report.Add(new MigrationIssue(row.Id, data[entity.Fields.Find(f => f.Type == FieldTypes.AutoNumber)?.Key ?? "number"]?.ToString(), issue.Value.Value, issue.Value.Reason));
                        }
                    }

                    if (changed)
                    {
                        run.Converted++;
                        var before = row.Data;
                        row.Data = data.ToJsonString();
                        row.Revision++;
                        row.UpdatedAt = DateTimeOffset.UtcNow;
                        db.AuditLog.Add(new AuditRow
                        {
                            TenantId = tenantId,
                            EnvironmentId = environmentId,
                            At = DateTimeOffset.UtcNow,
                            Channel = "worker",
                            Operation = "record.migrate",
                            TargetType = entity.Id,
                            TargetId = row.Id.ToString(),
                            ConfigRevision = candidateRevisionHint,
                            Outcome = "success",
                            Details = JsonSerializer.Serialize(new { migration = migration.Id, changes = new Dictionary<string, object?> { [target.Key] = new { from = JsonNode.Parse(before)![target.Key]?.ToJsonString(), to = data[target.Key]?.ToJsonString() } } }),
                        });
                        var envelope = new EventEnvelope
                        {
                            EventId = Guid.NewGuid(),
                            Type = EventTypes.RecordUpdated,
                            TenantId = tenantId,
                            EnvironmentId = environmentId,
                            Entity = entity.Id,
                            RecordId = row.Id,
                            RecordRevision = row.Revision,
                            OccurredAt = DateTimeOffset.UtcNow,
                            CorrelationId = runId,
                            Actor = "system",
                            Payload = new JsonObject { ["changed"] = new JsonArray(target.Key), ["source"] = "migration:" + migration.Id },
                        };
                        db.Outbox.Add(new OutboxRow { Id = envelope.EventId, TenantId = tenantId, EnvironmentId = environmentId, Type = envelope.Type, RecordId = row.Id, RecordRevision = row.Revision, Event = JsonSerializer.Serialize(envelope, SpecJson.Options), AvailableAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
                    }
                }

                run.Processed += rows.Count;
                run.Checkpoint = rows[^1].Id;
                run.Report = JsonSerializer.Serialize(report);
                await db.SaveChangesAsync(ct);
                return false;
            }, ct);

            if (done)
            {
                return;
            }

            batches++;
            faults.AfterBatch(migration.Id, batches);
        }
    }

    private (bool Changed, (string? Value, string Reason)? Issue) Apply(MigrationSpec migration, EntitySpec entity, FieldSpec target, JsonObject data, EffectiveConfig config)
    {
        if (data[target.Key] is not null)
        {
            return (false, null); // never overwrite: makes reruns idempotent
        }

        JsonNode? input;
        if (migration.Kind == "backfill")
        {
            try
            {
                var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["record"] = Values.MapFromJson(data),
                    ["prior"] = Values.MapFromJson(data),
                    ["user"] = new Dictionary<string, object?> { ["id"] = "system", ["roles"] = new List<object?>(), ["groups"] = new List<object?>() },
                    ["now"] = FieldNormalizer.FormatInstant(DateTimeOffset.UtcNow),
                };
                input = RecordWriteEngine.ToJson(Expression.Compile(migration.Value!, EntitySchema.For(entity)).Evaluate(vars));
            }
            catch (ExpressionException ex)
            {
                return (false, (null, ex.Message));
            }
        }
        else
        {
            input = data[migration.Source!]?.DeepClone();
            if (input is null)
            {
                return (false, null);
            }
        }

        if (input is null)
        {
            return (false, null);
        }

        var (value, error) = normalizer.Normalize(target, input, config);
        if (error is not null)
        {
            return (false, (input is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : input.ToJsonString(), error.Message));
        }

        if (value is null)
        {
            return (false, null);
        }

        data[target.Key] = value;
        return (true, null);
    }
}
