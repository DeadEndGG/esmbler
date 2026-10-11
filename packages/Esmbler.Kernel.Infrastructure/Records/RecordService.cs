using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Platform.Rules;
using Esmbler.Platform.Workflow;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Database = Esmbler.Kernel.Infrastructure.Persistence.Database;

namespace Esmbler.Kernel.Infrastructure.Records;

/// <summary>
/// Record engine (REQ-REC-*, REQ-EVT-001): every command runs in one transaction under the caller's tenant context,
/// pins one configuration revision, enforces entity/record/field permissions, runs the data-level write pipeline,
/// and commits the record change, its audit entry, and its outbox event together.
/// </summary>
public sealed partial class RecordService(Database database, ConfigStore configs, AccessService access, RecordWriteEngine engine, IClock clock, WorkflowVersions workflows, Sla.SlaService sla)
{
    private const int AuditValueLimit = 500;

    public Task<RecordView> CreateAsync(RequestContext ctx, string entityId, JsonObject data, int? clientConfigRevision, string? idempotencyKey, CancellationToken ct) =>
        Idempotent(ctx, idempotencyKey, new { op = "create", entityId, data }, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, entityId, clientConfigRevision, ct);
            if (!perms.CanCreate(entity))
            {
                throw KernelException.Forbidden($"You cannot create {entity.PluralLabel.ToLowerInvariant()}.");
            }

            var now = clock.UtcNow;
            var write = await engine.PrepareAsync(artifact.Config, entity, perms, WriteOperations.Create, null, data, FieldNormalizer.FormatInstant(now), null, new Lookups(db), ct);
            await ValidateReferencesAsync(db, artifact.Config, entity, write, perms, ct);
            await CheckUniqueAsync(db, ctx, entity, write, null, ct);
            var id = Guid.NewGuid();
            long? number = null;
            if (entity.Fields.Find(f => f.Type == FieldTypes.AutoNumber) is { } numberField)
            {
                number = await NextNumberAsync(db, ctx, entity.Id, ct);
                write.Data[numberField.Key] = $"{numberField.Prefix}-{number:D6}";
            }

            StampSystemFields(entity, write.Data, now, created: true);
            var (_, workflowVersion) = await workflows.ForRecordAsync(db, artifact, entity, null, ct);
            var row = new RecordRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = id,
                EntityId = entity.Id,
                Number = number,
                Revision = 1,
                Data = write.Data.ToJsonString(),
                ConfigRevision = artifact.Revision,
                WorkflowVersion = workflowVersion,
                CreatedAt = now,
                CreatedBy = ctx.PrincipalId,
                UpdatedAt = now,
                UpdatedBy = ctx.PrincipalId,
            };
            db.Records.Add(row);
            await db.SaveChangesAsync(ct);
            await UpdateSearchAsync(db, artifact.Config, entity, row, write.Data, ct);
            var changes = entity.Fields.Where(f => write.Data.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => (From: (JsonNode?)null, To: write.Data[f.Key]));
            AuditWriter.Add(db, ctx, "record.create", entity.Id, id.ToString(), artifact.Revision, AuditChanges(changes));
            var created = AddEvent(db, ctx, artifact.Config, EventTypes.RecordCreated, entity.Id, id, 1, artifact.Revision, new JsonObject { ["changed"] = Keys(changes.Keys) });
            EventWriter.AddEmitted(db, ctx, artifact.Config, entity.Id, id, 1, artifact.Revision, write.Emitted, created);
            await sla.ApplyAsync(db, ctx, artifact.Config, artifact.Revision, entity, id, 1, write.Variables(), isCreate: true, created, ct);
            await db.SaveChangesAsync(ct);
            return await ViewAsync(db, artifact.Config, entity, perms, row, write.Data, write.Messages, ct);
        }, ct);

    public Task<RecordView> UpdateAsync(RequestContext ctx, string entityId, Guid id, int expectedRevision, JsonObject changes, int? clientConfigRevision, string? idempotencyKey, CancellationToken ct) =>
        Idempotent(ctx, idempotencyKey, new { op = "update", entityId, id, expectedRevision, changes }, db =>
            WriteExistingAsync(db, ctx, entityId, id, expectedRevision, changes, clientConfigRevision, WriteOperations.Update, null, ct), ct);

    public Task<RecordView> TransitionAsync(RequestContext ctx, string entityId, Guid id, int expectedRevision, string transitionId, JsonObject changes, int? clientConfigRevision, string? idempotencyKey, CancellationToken ct) =>
        Idempotent(ctx, idempotencyKey, new { op = "transition", entityId, id, expectedRevision, transitionId, changes }, db =>
            WriteExistingAsync(db, ctx, entityId, id, expectedRevision, changes, clientConfigRevision, WriteOperations.Transition, transitionId, ct), ct);

    public Task DeleteAsync(RequestContext ctx, string entityId, Guid id, int expectedRevision, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, entityId, null, ct);
            var row = await LockAsync(db, ctx, entity, id, ct);
            var data = JsonNode.Parse(row.Data)!.AsObject();
            if (!perms.CanRead(entity, data))
            {
                throw KernelException.NotFound(entity.Label);
            }

            if (!perms.Entity(entity.Id).Delete)
            {
                throw KernelException.Forbidden($"You cannot delete {entity.PluralLabel.ToLowerInvariant()}.");
            }

            CheckRevision(row, expectedRevision, entity);
            foreach (var rel in artifact.Config.Relationships.Where(r => r.Target == entity.Id && r.OnTargetDelete == "restrict"))
            {
                var children = await db.Records.CountAsync(r => r.EntityId == rel.Entity && r.DeletedAt == null && EF.Functions.JsonContains(r.Data, $"{{\"{rel.Field}\":\"{id}\"}}"), ct);
                if (children > 0)
                {
                    // No count: linked records the caller cannot read must not be revealed.
                    throw KernelException.Conflict("has_children", $"This {entity.Label.ToLowerInvariant()} still has linked {rel.InverseLabel.ToLowerInvariant()}. Unlink or delete them first.");
                }
            }

            var now = clock.UtcNow;
            await db.Records.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedAt, now).SetProperty(r => r.Revision, row.Revision + 1).SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.UpdatedBy, ctx.PrincipalId), ct);
            AuditWriter.Add(db, ctx, "record.delete", entity.Id, id.ToString(), artifact.Revision);
            AddEvent(db, ctx, artifact.Config, EventTypes.RecordDeleted, entity.Id, id, row.Revision + 1, artifact.Revision, []);
            await sla.CancelAsync(db, id, ct);
            await db.SaveChangesAsync(ct);
        }, ct);

    /// <summary>Soft-deleted records can be restored for 30 days by anyone allowed to delete them (REQ-REC-006, ASM-027).</summary>
    public static readonly TimeSpan RestoreWindow = TimeSpan.FromDays(30);

    public async Task<RecordView> RestoreAsync(RequestContext ctx, string entityId, Guid id, CancellationToken ct)
    {
        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, entityId, null, ct);
            var rows = await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE tenant_id = {ctx.TenantId} AND environment_id = {ctx.EnvironmentId} AND id = {id} FOR UPDATE").ToListAsync(ct);
            var row = rows.FirstOrDefault(r => r.EntityId == entity.Id && r.DeletedAt is not null) ?? throw KernelException.NotFound($"Deleted {entity.Label.ToLowerInvariant()}");
            if (!perms.CanRead(entity, JsonNode.Parse(row.Data)!.AsObject()))
            {
                throw KernelException.NotFound($"Deleted {entity.Label.ToLowerInvariant()}");
            }

            if (!perms.Entity(entity.Id).Delete)
            {
                throw KernelException.Forbidden($"You cannot restore {entity.PluralLabel.ToLowerInvariant()}.");
            }

            var now = clock.UtcNow;
            if (now - row.DeletedAt!.Value > RestoreWindow)
            {
                throw new KernelException("restore_expired", $"This {entity.Label.ToLowerInvariant()} was deleted more than 30 days ago and can no longer be restored.", 410);
            }

            await db.Records.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedAt, (DateTimeOffset?)null).SetProperty(r => r.Revision, row.Revision + 1).SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.UpdatedBy, ctx.PrincipalId), ct);
            AuditWriter.Add(db, ctx, "record.restore", entity.Id, id.ToString(), artifact.Revision);
            AddEvent(db, ctx, artifact.Config, EventTypes.RecordRestored, entity.Id, id, row.Revision + 1, artifact.Revision, []);
            await sla.ApplyDataAsync(db, ctx, artifact.Config, artifact.Revision, entity, id, row.Revision + 1, JsonNode.Parse(row.Data)!.AsObject(), null, startAsCreate: true, ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
        return await GetAsync(ctx, entityId, id, ct);
    }

    public Task<RecordView> GetAsync(RequestContext ctx, string entityId, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, entityId, null, ct);
            var row = await db.Records.FirstOrDefaultAsync(r => r.Id == id && r.EntityId == entity.Id && r.DeletedAt == null, ct) ?? throw KernelException.NotFound(entity.Label);
            var data = JsonNode.Parse(row.Data)!.AsObject();
            if (!perms.CanRead(entity, data))
            {
                throw KernelException.NotFound(entity.Label);
            }

            var (pinned, _) = await workflows.ForRecordAsync(db, artifact, entity, row.WorkflowVersion, ct);
            return await ViewAsync(db, artifact.Config, entity, perms, row, data, [], ct, pinned);
        }, ct);

    public Task<RecordPage> QueryAsync(RequestContext ctx, RecordQuery query, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, query.Entity, null, ct);
            var size = Math.Clamp(query.PageSize, 1, QueryCompiler.MaxPageSize);
            var offset = Math.Clamp(query.Offset, 0, QueryCompiler.MaxOffset);
            var compiled = QueryCompiler.Compile(query, artifact.Config, entity, perms);
            var conn = db.Database.GetDbConnection();
            var tx = db.Database.CurrentTransaction!.GetDbTransaction();
            await Exec(conn, tx, "SET LOCAL statement_timeout = '5s'", [], ct);

            var rows = new List<RecordRow>();
            await using (var cmd = Command(conn, tx, $"SELECT id, number, revision, data::text, config_revision, created_at, updated_at FROM kernel.records r WHERE {compiled.Where} ORDER BY {compiled.OrderBy} LIMIT {size} OFFSET {offset}", compiled.Parameters))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new RecordRow
                    {
                        Id = reader.GetGuid(0),
                        EntityId = entity.Id,
                        Number = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        Revision = reader.GetInt32(2),
                        Data = reader.GetString(3),
                        ConfigRevision = reader.GetInt32(4),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                    });
                }
            }

            var countParams = QueryCompiler.Compile(query, artifact.Config, entity, perms);
            long total;
            await using (var cmd = Command(conn, tx, $"SELECT count(*) FROM (SELECT 1 FROM kernel.records r WHERE {countParams.Where} LIMIT {QueryCompiler.MaxCount + 1}) capped", countParams.Parameters))
            {
                total = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            var parsed = rows.Select(r => (Row: r, Data: JsonNode.Parse(r.Data)!.AsObject())).ToList();
            var display = await DisplayAsync(db, artifact.Config, entity, perms, parsed.Select(p => p.Data), ct);
            var fieldAccess = perms.FieldAccessMap(entity);
            var items = parsed.Select(p => new RecordView
            {
                Id = p.Row.Id,
                Entity = entity.Id,
                Number = NumberText(entity, p.Data),
                Revision = p.Row.Revision,
                ConfigRevision = artifact.Revision,
                Data = Filter(p.Data, fieldAccess),
                Display = DisplayFor(artifact.Config, entity, p.Data, display, fieldAccess),
                CreatedAt = p.Row.CreatedAt,
                UpdatedAt = p.Row.UpdatedAt,
            }).ToList();
            return new RecordPage(items, (int)Math.Min(total, QueryCompiler.MaxCount), total > QueryCompiler.MaxCount, offset, size, artifact.Revision);
        }, ct);

    public Task<ActivityView> AddActivityAsync(RequestContext ctx, string entityId, Guid id, string kind, string body, IRichTextSanitizer sanitizer, CancellationToken ct) =>
        AddActivityAsync(ctx, entityId, id, kind, body, sanitizer, null, null, ct);

    /// <summary>
    /// Adds a reply or note as <paramref name="ctx"/>'s person. <paramref name="source"/> marks replies that came by email;
    /// <paramref name="alongside"/> runs in the same transaction with the new activity's ID (email intake marks its
    /// message processed there, so a retry never adds the reply twice).
    /// </summary>
    public Task<ActivityView> AddActivityAsync(RequestContext ctx, string entityId, Guid id, string kind, string body, IRichTextSanitizer sanitizer, string? source, Func<KernelDbContext, Guid, Task>? alongside, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, entityId, null, ct);
            var row = await db.Records.FirstOrDefaultAsync(r => r.Id == id && r.EntityId == entity.Id && r.DeletedAt == null, ct) ?? throw KernelException.NotFound(entity.Label);
            var data = JsonNode.Parse(row.Data)!.AsObject();
            if (!perms.CanRead(entity, data))
            {
                throw KernelException.NotFound(entity.Label);
            }

            if (kind is not ("public_reply" or "work_note"))
            {
                throw new KernelException("invalid_kind", "Activity must be a public reply or an internal work note.", 400);
            }

            // Internal work notes need edit rights (agents); requesters may only post public replies on related records.
            var canUpdate = perms.CanUpdate(entity, data);
            if (kind == "work_note" && (!canUpdate || perms.Entity(entity.Id).Read != RecordScopes.All))
            {
                throw KernelException.Forbidden("Only agents can add internal work notes.");
            }

            if (kind == "public_reply" && !canUpdate && !perms.Relation(entity, data).Related)
            {
                throw KernelException.Forbidden("You cannot reply on this record.");
            }

            var clean = sanitizer.Sanitize(body).Trim();
            if (clean.Length == 0 || clean.Length > 20000)
            {
                throw new ValidationFailedException([new FieldError("body", "invalid_length", "Write a message of 1 to 20,000 characters.")]);
            }

            var activity = new ActivityRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = Guid.NewGuid(),
                RecordId = id,
                Kind = kind,
                Body = clean,
                Author = ctx.PrincipalId,
                Agent = ctx.AgentId,
                CreatedAt = clock.UtcNow,
                Source = source,
            };
            db.Activity.Add(activity);
            AuditWriter.Add(db, ctx, "activity.add", entity.Id, id.ToString(), artifact.Revision, source is null ? new { kind } : new { kind, source });
            if (alongside is not null)
            {
                await alongside(db, activity.Id);
            }

            AddEvent(db, ctx, artifact.Config, EventTypes.ActivityAdded, entity.Id, id, row.Revision, artifact.Revision, new JsonObject { ["kind"] = kind, ["activityId"] = activity.Id.ToString() });
            await db.SaveChangesAsync(ct);
            var name = await db.Principals.Where(p => p.Id == ctx.PrincipalId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
            return new ActivityView(activity.Id, kind, clean, ctx.PrincipalId, name, ctx.AgentId is not null, activity.CreatedAt, source);
        }, ct);

    public Task<IReadOnlyList<ActivityView>> ActivityAsync(RequestContext ctx, string entityId, Guid id, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<ActivityView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (_, entity, perms) = await PrepareAsync(db, ctx, entityId, null, ct);
            var row = await db.Records.FirstOrDefaultAsync(r => r.Id == id && r.EntityId == entity.Id && r.DeletedAt == null, ct) ?? throw KernelException.NotFound(entity.Label);
            var data = JsonNode.Parse(row.Data)!.AsObject();
            if (!perms.CanRead(entity, data))
            {
                throw KernelException.NotFound(entity.Label);
            }

            // Requesters and portal users never see internal work notes (REQ-BIZ-002).
            var seesInternal = perms.Entity(entity.Id).Read == RecordScopes.All;
            var items = await db.Activity.Where(a => a.RecordId == id && (seesInternal || a.Kind != "work_note")).OrderBy(a => a.CreatedAt).ToListAsync(ct);
            var authors = items.Select(a => a.Author).OfType<Guid>().Distinct().ToList();
            var names = await db.Principals.Where(p => authors.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            return items.Select(a => new ActivityView(a.Id, a.Kind, a.RedactedAt is null ? a.Body : "(redacted)", a.Author, a.Author is { } au ? names.GetValueOrDefault(au) : null, a.Agent is not null, a.CreatedAt, a.Source)).ToList();
        }, ct);

    public Task<IReadOnlyList<HistoryEntryView>> HistoryAsync(RequestContext ctx, string entityId, Guid id, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<HistoryEntryView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (_, entity, perms) = await PrepareAsync(db, ctx, entityId, null, ct);
            var row = await db.Records.FirstOrDefaultAsync(r => r.Id == id && r.EntityId == entity.Id, ct) ?? throw KernelException.NotFound(entity.Label);
            if (!perms.CanRead(entity, JsonNode.Parse(row.Data)!.AsObject()))
            {
                throw KernelException.NotFound(entity.Label);
            }

            var target = id.ToString();
            var entries = await db.AuditLog.Where(a => a.TargetType == entity.Id && a.TargetId == target && a.Operation.StartsWith("record.")).OrderByDescending(a => a.Id).Take(200).ToListAsync(ct);
            var actors = entries.Select(e => e.Actor).OfType<Guid>().Distinct().ToList();
            var names = await db.Principals.Where(p => actors.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            var fieldAccess = perms.FieldAccessMap(entity);
            return entries.Select(e =>
            {
                var details = JsonNode.Parse(e.Details) as JsonObject;
                var changes = (details?["changes"] as JsonObject ?? [])
                    .Where(c => fieldAccess.GetValueOrDefault(c.Key, FieldAccessLevels.Hidden) != FieldAccessLevels.Hidden)
                    .Select(c => new FieldChangeView(c.Key, entity.FindField(c.Key)?.Label ?? c.Key, c.Value?["from"]?.DeepClone(), c.Value?["to"]?.DeepClone()))
                    .ToList();
                return new HistoryEntryView(e.Id, e.At, e.Operation, e.Actor is { } a ? names.GetValueOrDefault(a) : null, e.Agent is not null, e.ConfigRevision, changes);
            }).ToList();
        }, ct);

    /// <summary>Platform-trusted write used by provisioning, sample data, and migrations (operation "system").</summary>
    public async Task<Guid> SystemCreateAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, JsonObject data, CancellationToken ct)
    {
        var perms = SystemPermissions(artifact, ctx);
        var now = clock.UtcNow;
        var write = await engine.PrepareAsync(artifact.Config, entity, perms, WriteOperations.System, null, data, FieldNormalizer.FormatInstant(now), null, new Lookups(db), ct, systemActor: true);
        var id = Guid.NewGuid();
        long? number = null;
        if (entity.Fields.Find(f => f.Type == FieldTypes.AutoNumber) is { } numberField)
        {
            number = await NextNumberAsync(db, ctx, entity.Id, ct);
            write.Data[numberField.Key] = $"{numberField.Prefix}-{number:D6}";
        }

        foreach (var field in entity.Fields.Where(f => f.Default is not null && !write.Data.ContainsKey(f.Key)))
        {
            write.Data[field.Key] = field.Default!.DeepClone();
        }

        StampSystemFields(entity, write.Data, now, created: true);
        var (_, workflowVersion) = await workflows.ForRecordAsync(db, artifact, entity, null, ct);
        var row = new RecordRow
        {
            TenantId = ctx.TenantId,
            EnvironmentId = ctx.EnvironmentId,
            Id = id,
            EntityId = entity.Id,
            Number = number,
            Revision = 1,
            Data = write.Data.ToJsonString(),
            ConfigRevision = artifact.Revision,
            WorkflowVersion = workflowVersion,
            CreatedAt = now,
            CreatedBy = ctx.PrincipalId,
            UpdatedAt = now,
            UpdatedBy = ctx.PrincipalId,
        };
        db.Records.Add(row);
        await db.SaveChangesAsync(ct);
        await UpdateSearchAsync(db, artifact.Config, entity, row, write.Data, ct);
        AuditWriter.Add(db, ctx, "record.create", entity.Id, id.ToString(), artifact.Revision, new { source = "system" });
        var systemCreated = AddEvent(db, ctx, artifact.Config, EventTypes.RecordCreated, entity.Id, id, 1, artifact.Revision, new JsonObject { ["changed"] = Keys(write.Data.Select(p => p.Key)), ["source"] = "system" });
        EventWriter.AddEmitted(db, ctx, artifact.Config, entity.Id, id, 1, artifact.Revision, write.Emitted, systemCreated);
        await sla.ApplyAsync(db, ctx, artifact.Config, artifact.Revision, entity, id, 1, write.Variables(), isCreate: true, systemCreated, ct);
        await db.SaveChangesAsync(ct);
        return id;
    }

    private async Task<RecordView> WriteExistingAsync(KernelDbContext db, RequestContext ctx, string entityId, Guid id, int expectedRevision, JsonObject changes, int? clientConfigRevision, string operation, string? transitionId, CancellationToken ct)
    {
        var (artifact, entity, perms) = await PrepareAsync(db, ctx, entityId, clientConfigRevision, ct);
        var row = await LockAsync(db, ctx, entity, id, ct);
        var prior = JsonNode.Parse(row.Data)!.AsObject();
        if (!perms.CanRead(entity, prior))
        {
            throw KernelException.NotFound(entity.Label);
        }

        if (!perms.CanUpdate(entity, prior))
        {
            throw KernelException.Forbidden($"You cannot edit this {entity.Label.ToLowerInvariant()}.");
        }

        CheckRevision(row, expectedRevision, entity, changes, prior, perms);
        var (write, written) = await WriteRowAsync(db, ctx, artifact, entity, perms, row, prior, changes, operation, transitionId, systemActor: false, ct);
        return await ViewAsync(db, artifact.Config, entity, perms, row, written ? write.Data : prior, write.Messages, ct, write.Workflow);
    }

    /// <summary>
    /// A platform write (fulfillment) in the caller's transaction: the full write pipeline runs
    /// under the active configuration with platform rights, may fire system transitions, and takes the row lock, so
    /// concurrent system writers serialize. Returns false when the transition is no longer valid from the record's
    /// current state (another writer got there first), which callers treat as already done.
    /// </summary>
    public async Task<bool> SystemWriteAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, Guid id, string? transitionId, JsonObject changes, CancellationToken ct)
    {
        var perms = SystemPermissions(artifact, ctx);
        var row = await LockAsync(db, ctx, entity, id, ct);
        var prior = JsonNode.Parse(row.Data)!.AsObject();
        try
        {
            await WriteRowAsync(db, ctx, artifact, entity, perms, row, prior, changes, transitionId is null ? WriteOperations.System : WriteOperations.Transition, transitionId, systemActor: true, ct);
            return true;
        }
        catch (ValidationFailedException ex) when (transitionId is not null && ex.Errors.All(e => e.Code is "invalid_transition" or "unknown_transition" or "transition_disabled"))
        {
            return false;
        }
    }

    private static EffectivePermissions SystemPermissions(ActiveArtifact artifact, RequestContext ctx) =>
        new(artifact.Config, AuthorizationFacts.None(ctx.PrincipalId) with { Roles = new Dictionary<string, IReadOnlyList<string>> { [PlatformModule.TenantAdminRole] = ["system"] } }, ctx);

    private async Task<(RecordWriteContext Write, bool Written)> WriteRowAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, EffectivePermissions perms, RecordRow row, JsonObject prior, JsonObject changes, string operation, string? transitionId, bool systemActor, CancellationToken ct)
    {
        var id = row.Id;
        var now = clock.UtcNow;

        // REQ-MIG-003: transitions run on the definition version the record is pinned to; rules, fields and
        // permissions use the current configuration. Records created before versioning are pinned on their first write.
        var (pinned, workflowVersion) = await workflows.ForRecordAsync(db, artifact, entity, row.WorkflowVersion, ct);
        var write = await engine.PrepareAsync(artifact.Config, entity, perms, operation, prior, changes, FieldNormalizer.FormatInstant(now), transitionId, new Lookups(db), ct, pinned, systemActor);
        if (write.Changed.Count == 0 && operation is WriteOperations.Update or WriteOperations.System)
        {
            return (write, false);
        }

        await ValidateReferencesAsync(db, artifact.Config, entity, write, perms, ct);
        await CheckUniqueAsync(db, ctx, entity, write, id, ct);
        StampSystemFields(entity, write.Data, now, created: false);
        var revision = row.Revision + 1;
        var json = write.Data.ToJsonString();
        await db.Records.Where(r => r.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(r => r.Data, json).SetProperty(r => r.Revision, revision).SetProperty(r => r.ConfigRevision, artifact.Revision)
                .SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.UpdatedBy, ctx.PrincipalId).SetProperty(r => r.WorkflowVersion, workflowVersion),
            ct);
        row.Revision = revision;
        row.UpdatedAt = now;
        row.ConfigRevision = artifact.Revision;
        row.WorkflowVersion = workflowVersion;
        await UpdateSearchAsync(db, artifact.Config, entity, row, write.Data, ct);
        var diff = write.Changed.Where(k => !JsonNode.DeepEquals(prior[k], write.Data[k])).ToDictionary(k => k, k => (From: prior[k], To: write.Data[k]));
        var payload = new JsonObject { ["changed"] = Keys(diff.Keys) };
        if (systemActor)
        {
            payload["source"] = "system";
        }

        string type;
        if (operation == WriteOperations.Transition)
        {
            type = EventTypes.RecordTransitioned;
            payload["transition"] = transitionId;
            payload["from"] = write.FromState;
            payload["to"] = write.ToState;
        }
        else
        {
            type = EventTypes.RecordUpdated;
        }

        AuditWriter.Add(db, ctx, operation == WriteOperations.Transition ? "record.transition" : "record.update", entity.Id, id.ToString(), artifact.Revision, AuditChanges(diff, transitionId, systemActor));
        var recordEvent = AddEvent(db, ctx, artifact.Config, type, entity.Id, id, revision, artifact.Revision, payload);
        EventWriter.AddEmitted(db, ctx, artifact.Config, entity.Id, id, revision, artifact.Revision, write.Emitted, recordEvent);
        await sla.ApplyAsync(db, ctx, artifact.Config, artifact.Revision, entity, id, revision, write.Variables(), isCreate: false, recordEvent, ct);
        await db.SaveChangesAsync(ct);
        return (write, true);
    }

    private async Task<(ActiveArtifact Artifact, EntitySpec Entity, EffectivePermissions Perms)> PrepareAsync(KernelDbContext db, RequestContext ctx, string entityId, int? clientConfigRevision, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var entity = artifact.Config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
        if (clientConfigRevision is { } clientRevision && clientRevision != artifact.Revision)
        {
            var old = await configs.GetAsync(db, ctx.TenantId, ctx.EnvironmentId, clientRevision, ct);
            var oldEntity = old.Config.FindEntity(entityId);
            if (oldEntity is null || EntityHash(oldEntity, old.Config) != EntityHash(entity, artifact.Config))
            {
                // REQ-CFG-008: a form submitted under an incompatible older revision gets an actionable conflict.
                throw KernelException.Conflict(
                    "config_conflict",
                    $"The {entity.Label.ToLowerInvariant()} form changed since you opened it (revision {clientRevision} → {artifact.Revision}). Reload to see the new fields and rules, then try again.",
                    new { clientRevision, currentRevision = artifact.Revision },
                    "Reload the page.");
            }
        }

        var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
        return (artifact, entity, perms);
    }

    private static async Task<RecordRow> LockAsync(KernelDbContext db, RequestContext ctx, EntitySpec entity, Guid id, CancellationToken ct)
    {
        var rows = await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE tenant_id = {ctx.TenantId} AND environment_id = {ctx.EnvironmentId} AND id = {id} FOR UPDATE").ToListAsync(ct);
        var row = rows.FirstOrDefault();
        if (row is null || row.DeletedAt is not null || row.EntityId != entity.Id)
        {
            throw KernelException.NotFound(entity.Label);
        }

        return row;
    }

    private static void CheckRevision(RecordRow row, int expectedRevision, EntitySpec entity, JsonObject? changes = null, JsonObject? current = null, EffectivePermissions? perms = null)
    {
        if (row.Revision == expectedRevision)
        {
            return;
        }

        // REQ-REC-004: stale write. Return the current values of the fields the caller tried to change (visible ones only).
        var currentValues = new JsonObject();
        if (changes is not null && current is not null && perms is not null)
        {
            foreach (var key in changes.Select(c => c.Key))
            {
                if (perms.FieldAccess(entity, key) != FieldAccessLevels.Hidden)
                {
                    currentValues[key] = current[key]?.DeepClone();
                }
            }
        }

        throw KernelException.Conflict(
            "record_conflict",
            $"Someone else changed this {entity.Label.ToLowerInvariant()} (revision {expectedRevision} → {row.Revision}). Review their changes and apply yours again.",
            new { expectedRevision, currentRevision = row.Revision, current = currentValues },
            "Reload the record.");
    }

    private static async Task ValidateReferencesAsync(KernelDbContext db, EffectiveConfig config, EntitySpec entity, RecordWriteContext write, EffectivePermissions perms, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (entity.Request is { } binding && write.Changed.Contains(binding.VariablesField) && write.Data[binding.VariablesField] is JsonObject answers
            && config.FindCatalogItem(write.Data[binding.CatalogItemField]?.GetValue<string>() ?? string.Empty) is { } item)
        {
            // People and groups named in request answers must exist in this workspace, like user and group fields.
            foreach (var variable in item.Variables.Where(v => v.Type is FieldTypes.User or FieldTypes.Group))
            {
                if (answers[variable.Key] is not JsonValue av || !av.TryGetValue<string>(out var rawRef) || !Guid.TryParse(rawRef, out var refId))
                {
                    continue;
                }

                var exists = variable.Type == FieldTypes.User
                    ? await db.Memberships.AnyAsync(m => m.PrincipalId == refId && m.TenantId == write.Permissions.Context.TenantId, ct)
                    : await db.Groups.AnyAsync(g => g.Id == refId, ct);
                if (!exists)
                {
                    errors.Add(new FieldError($"{binding.VariablesField}.{variable.Key}", variable.Type == FieldTypes.User ? "unknown_user" : "unknown_group", $"{variable.Label} must be an existing {(variable.Type == FieldTypes.User ? "member of this workspace" : "group")}."));
                }
            }
        }

        foreach (var key in write.Changed)
        {
            var field = entity.FindField(key);
            if (field is null || write.Data[key] is not JsonValue v || !v.TryGetValue<string>(out var raw) || !Guid.TryParse(raw, out var refId))
            {
                continue;
            }

            switch (field.Type)
            {
                case FieldTypes.User:
                    if (!await db.Memberships.AnyAsync(m => m.PrincipalId == refId && m.TenantId == write.Permissions.Context.TenantId, ct))
                    {
                        errors.Add(new FieldError(key, "unknown_user", $"{field.Label} must be a member of this workspace."));
                    }

                    break;
                case FieldTypes.Group:
                    if (!await db.Groups.AnyAsync(g => g.Id == refId, ct))
                    {
                        errors.Add(new FieldError(key, "unknown_group", $"{field.Label} must be an existing group."));
                    }

                    break;
                case FieldTypes.Lookup:
                    var target = await db.Records.FirstOrDefaultAsync(r => r.Id == refId && r.EntityId == field.Target && r.DeletedAt == null, ct);
                    var targetEntity = config.FindEntity(field.Target!);
                    // REQ-AUTHZ-010: you can only link to records you can see, and linking grants nothing.
                    if (target is null || targetEntity is null || !perms.CanRead(targetEntity, JsonNode.Parse(target.Data)!.AsObject()))
                    {
                        errors.Add(new FieldError(key, "unknown_record", $"{field.Label} must reference an existing {targetEntity?.Label.ToLowerInvariant() ?? "record"} you can see."));
                    }

                    break;
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static async Task CheckUniqueAsync(KernelDbContext db, RequestContext ctx, EntitySpec entity, RecordWriteContext write, Guid? selfId, CancellationToken ct)
    {
        foreach (var field in entity.Fields.Where(f => f.Unique && write.Changed.Contains(f.Key) && write.Data[f.Key] is not null))
        {
            var probe = new JsonObject { [field.Key] = write.Data[field.Key]!.DeepClone() }.ToJsonString();
            var exists = await db.Records.AnyAsync(r => r.EntityId == entity.Id && r.DeletedAt == null && r.Id != selfId && EF.Functions.JsonContains(r.Data, probe), ct);
            if (exists)
            {
                throw new ValidationFailedException([new FieldError(field.Key, "not_unique", $"Another {entity.Label.ToLowerInvariant()} already has this {field.Label}.")]);
            }
        }
    }

    private static async Task<long> NextNumberAsync(KernelDbContext db, RequestContext ctx, string entityId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var tx = db.Database.CurrentTransaction!.GetDbTransaction();
        await using var cmd = Command(conn, tx, """
            INSERT INTO kernel.record_sequences (tenant_id, environment_id, entity_id, next_value) VALUES ($1, $2, $3, 2)
            ON CONFLICT (tenant_id, environment_id, entity_id) DO UPDATE SET next_value = kernel.record_sequences.next_value + 1
            RETURNING next_value - 1
            """, [new NpgsqlParameter { Value = ctx.TenantId }, new NpgsqlParameter { Value = ctx.EnvironmentId }, new NpgsqlParameter { Value = entityId }]);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void StampSystemFields(EntitySpec entity, JsonObject data, DateTimeOffset now, bool created)
    {
        var stamp = FieldNormalizer.FormatInstant(now);
        if (created && entity.FindField("created_at") is { System: true })
        {
            data["created_at"] = stamp;
        }

        if (entity.FindField("updated_at") is { System: true })
        {
            data["updated_at"] = stamp;
        }
    }

    /// <summary>Full-text vector from text fields that no permission set hides, so search never reveals hidden values.</summary>
    private static async Task UpdateSearchAsync(KernelDbContext db, EffectiveConfig config, EntitySpec entity, RecordRow row, JsonObject data, CancellationToken ct)
    {
        var hiddenSomewhere = config.PermissionSets.SelectMany(p => p.Fields).Where(f => f.Entity == entity.Id && f.Access == FieldAccessLevels.Hidden).Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var field in entity.Fields.Where(f => (FieldTypes.IsText(f.Type) || f.Type == FieldTypes.AutoNumber) && !hiddenSomewhere.Contains(f.Key) && !f.Hidden))
        {
            if (data[field.Key] is JsonValue v && v.TryGetValue<string>(out var s))
            {
                text.Append(field.Type == FieldTypes.RichText ? StripTags(s) : s).Append(' ');
            }
        }

        var content = text.ToString();
        if (content.Length > 100_000)
        {
            content = content[..100_000];
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE kernel.records SET search = to_tsvector('simple', {content}) WHERE tenant_id = {row.TenantId} AND environment_id = {row.EnvironmentId} AND id = {row.Id}", ct);
    }

    private static string StripTags(string html) => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private async Task<RecordView> ViewAsync(KernelDbContext db, EffectiveConfig config, EntitySpec entity, EffectivePermissions perms, RecordRow row, JsonObject data, IReadOnlyList<RuleMessage> messages, CancellationToken ct, WorkflowSpec? pinned = null)
    {
        var fieldAccess = perms.FieldAccessMap(entity);
        var display = await DisplayAsync(db, config, entity, perms, [data], ct);
        var states = RuleEvaluator.Evaluate(config, entity, perms.Variables(entity, data, null, FieldNormalizer.FormatInstant(clock.UtcNow)));
        var hiddenByPermission = fieldAccess.Where(f => f.Value == FieldAccessLevels.Hidden).Select(f => f.Key);
        var canUpdate = perms.CanUpdate(entity, data);
        var wf = pinned ?? config.WorkflowFor(entity.Id);
        var transitions = wf is null || !canUpdate
            ? []
            : WorkflowHook.Available(wf, data, perms.Roles).Select(t => new TransitionView(t.Id, t.Label, t.Tone, t.RequiredFields)).ToList();
        return new RecordView
        {
            Id = row.Id,
            Entity = entity.Id,
            Number = NumberText(entity, data),
            Revision = row.Revision,
            ConfigRevision = row.ConfigRevision,
            Data = Filter(data, fieldAccess),
            Display = DisplayFor(config, entity, data, display, fieldAccess),
            Access = new RecordAccessView(canUpdate, perms.Entity(entity.Id).Delete, fieldAccess.ToDictionary(f => f.Key, f => canUpdate ? f.Value : f.Value == FieldAccessLevels.Write ? FieldAccessLevels.Read : f.Value)),
            Transitions = transitions,
            FieldStates = new FieldStatesView(
                states.Required.Where(f => fieldAccess.GetValueOrDefault(f) != FieldAccessLevels.Hidden).ToList(),
                states.ReadOnly.ToList(),
                states.Hidden.Concat(hiddenByPermission).Distinct().ToList(),
                states.Options,
                states.Messages.Select(m => new RuleMessageView(m.RuleId, m.Tone, m.Text, m.Field)).ToList()),
            Messages = messages.Select(m => new RuleMessageView(m.RuleId, m.Tone, m.Text, m.Field)).ToList(),
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }

    private static string? NumberText(EntitySpec entity, JsonObject data) =>
        entity.Fields.Find(f => f.Type == FieldTypes.AutoNumber) is { } f && data[f.Key] is JsonValue v ? v.GetValue<string>() : null;

    private static JsonObject Filter(JsonObject data, IReadOnlyDictionary<string, string> fieldAccess)
    {
        var result = new JsonObject();
        foreach (var (key, value) in data)
        {
            if (fieldAccess.TryGetValue(key, out var level) && level != FieldAccessLevels.Hidden)
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>Display names for references and choice labels, resolved within the viewer's access.</summary>
    private static async Task<Dictionary<string, string>> DisplayAsync(KernelDbContext db, EffectiveConfig config, EntitySpec entity, EffectivePermissions perms, IEnumerable<JsonObject> records, CancellationToken ct)
    {
        var users = new HashSet<Guid>();
        var groups = new HashSet<Guid>();
        var lookups = new Dictionary<Guid, string>();
        foreach (var data in records)
        {
            foreach (var field in entity.Fields)
            {
                if (field.Type == FieldTypes.Principals && data[field.Key] is JsonArray list)
                {
                    // Users and groups share one list; resolve both and let whichever matches name it.
                    foreach (var item in list.OfType<JsonValue>())
                    {
                        if (item.TryGetValue<string>(out var s) && Guid.TryParse(s, out var pid))
                        {
                            users.Add(pid);
                            groups.Add(pid);
                        }
                    }

                    continue;
                }

                if (data[field.Key] is not JsonValue v || !v.TryGetValue<string>(out var raw) || !Guid.TryParse(raw, out var id))
                {
                    continue;
                }

                switch (field.Type)
                {
                    case FieldTypes.User:
                        users.Add(id);
                        break;
                    case FieldTypes.Group:
                        groups.Add(id);
                        break;
                    case FieldTypes.Lookup:
                        lookups[id] = field.Target!;
                        break;
                }
            }
        }

        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        if (users.Count > 0)
        {
            foreach (var p in await db.Principals.Where(p => users.Contains(p.Id)).Select(p => new { p.Id, p.DisplayName }).ToListAsync(ct))
            {
                display[p.Id.ToString()] = p.DisplayName;
            }
        }

        if (groups.Count > 0)
        {
            foreach (var g in await db.Groups.Where(g => groups.Contains(g.Id)).Select(g => new { g.Id, g.Name }).ToListAsync(ct))
            {
                display[g.Id.ToString()] = g.Name;
            }
        }

        if (lookups.Count > 0)
        {
            var ids = lookups.Keys.ToList();
            foreach (var target in await db.Records.Where(r => ids.Contains(r.Id) && r.DeletedAt == null).ToListAsync(ct))
            {
                var targetEntity = config.FindEntity(target.EntityId);
                var targetData = JsonNode.Parse(target.Data)!.AsObject();
                if (targetEntity is null || !perms.CanRead(targetEntity, targetData))
                {
                    display[target.Id.ToString()] = "Restricted record";
                    continue;
                }

                var title = targetData[targetEntity.TitleField]?.GetValue<string>() ?? "Untitled";
                var number = NumberText(targetEntity, targetData);
                display[target.Id.ToString()] = number is null ? title : $"{number} · {title}";
            }
        }

        return display;
    }

    private static IReadOnlyDictionary<string, string> DisplayFor(EffectiveConfig config, EntitySpec entity, JsonObject data, Dictionary<string, string> display, IReadOnlyDictionary<string, string> fieldAccess)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in entity.Fields.Where(f => f.Type == FieldTypes.CatalogItem))
        {
            if (fieldAccess.GetValueOrDefault(field.Key) != FieldAccessLevels.Hidden && data[field.Key] is JsonValue item && item.TryGetValue<string>(out var itemId))
            {
                result[field.Key] = config.FindCatalogItem(itemId)?.Label ?? itemId;
            }
        }

        foreach (var field in entity.Fields.Where(f => f.Type == FieldTypes.Principals))
        {
            if (fieldAccess.GetValueOrDefault(field.Key) != FieldAccessLevels.Hidden && data[field.Key] is JsonArray list)
            {
                result[field.Key] = string.Join(", ", list.OfType<JsonValue>().Select(v => display.GetValueOrDefault(v.GetValue<string>(), "Unknown")));
            }
        }

        foreach (var field in entity.Fields.Where(f => f.Type is FieldTypes.User or FieldTypes.Group or FieldTypes.Lookup))
        {
            if (fieldAccess.GetValueOrDefault(field.Key) == FieldAccessLevels.Hidden || data[field.Key] is not JsonValue v || !v.TryGetValue<string>(out var raw))
            {
                continue;
            }

            result[field.Key] = display.GetValueOrDefault(raw, field.Type == FieldTypes.Lookup ? "Restricted record" : "Unknown");
        }

        return result;
    }

    /// <summary>Audit details; platform writes are marked <c>source: system</c> so they are not read as the person's own act.</summary>
    private static object AuditChanges(Dictionary<string, (JsonNode? From, JsonNode? To)> changes, string? transition = null, bool system = false)
    {
        var obj = new JsonObject();
        foreach (var (key, (from, to)) in changes)
        {
            obj[key] = new JsonObject { ["from"] = Truncate(from), ["to"] = Truncate(to) };
        }

        var details = new JsonObject { ["changes"] = obj };
        if (transition is not null)
        {
            details["transition"] = transition;
        }

        if (system)
        {
            details["source"] = "system";
        }

        return details;
    }

    private static JsonNode? Truncate(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > AuditValueLimit ? JsonValue.Create(s[..AuditValueLimit] + "…") : value?.DeepClone();

    private static JsonArray Keys(IEnumerable<string> keys) => new(keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());

    private static Guid AddEvent(KernelDbContext db, RequestContext ctx, EffectiveConfig config, string type, string entityId, Guid recordId, int revision, int configRevision, JsonObject payload) =>
        EventWriter.Add(db, new EventEnvelope
        {
            EventId = Guid.NewGuid(),
            Type = type,
            Source = EventWriter.SourceOf(config, entityId),
            TenantId = ctx.TenantId,
            EnvironmentId = ctx.EnvironmentId,
            Entity = entityId,
            RecordId = recordId,
            RecordRevision = revision,
            ConfigRevision = configRevision,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = ctx.CorrelationId,
            Actor = ctx.ActorRef,
            Severity = EventSeverities.Info,
            Sensitivity = EventSensitivities.Internal,
            Payload = payload,
        });

    private async Task<RecordView> Idempotent(RequestContext ctx, string? key, object request, Func<KernelDbContext, Task<RecordView>> work, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, work, ct);
        }

        if (key.Length > 200)
        {
            throw new KernelException("invalid_idempotency_key", "Idempotency keys can be at most 200 characters.", 400);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, SpecJson.Options))));
        return await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            // Serialize concurrent retries of the same key.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ctx.TenantId.ToString() + ctx.EnvironmentId + key}, 0))", ct);
            var existing = await db.IdempotencyKeys.FirstOrDefaultAsync(k => k.Key == key, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != hash || existing.PrincipalId != ctx.PrincipalId)
                {
                    throw new KernelException("idempotency_key_reused", "This idempotency key was already used for a different request.", 422);
                }

                return JsonSerializer.Deserialize<RecordView>(existing.Response, SpecJson.Options)!;
            }

            var result = await work(db);
            db.IdempotencyKeys.Add(new IdempotencyRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Key = key,
                PrincipalId = ctx.PrincipalId,
                RequestHash = hash,
                Response = JsonSerializer.Serialize(result, SpecJson.Options),
                StatusCode = 200,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return result;
        }, ct);
    }

    /// <summary>The form contract of an entity: its spec, plus for request entities the catalog items (questions) it receives.</summary>
    private static string EntityHash(EntitySpec entity, EffectiveConfig config)
    {
        var node = new JsonObject { ["entity"] = SpecJson.ToNode(entity) };
        if (entity.Request is not null)
        {
            node["catalogItems"] = SpecJson.ToNode(config.CatalogItems.Where(i => i.RequestEntity == entity.Id).OrderBy(i => i.Id, StringComparer.Ordinal).ToList());
        }

        return Composer.Hash(SpecJson.Canonical(node));
    }

    private static DbCommand Command(DbConnection conn, DbTransaction tx, string sql, IEnumerable<NpgsqlParameter> parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p.Clone());
        }

        return cmd;
    }

    private static async Task Exec(DbConnection conn, DbTransaction tx, string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class Lookups(KernelDbContext db) : IRecordLookups
    {
        public async Task<Guid?> FindGroupByKeyAsync(string key, CancellationToken cancellationToken) =>
            await db.Groups.Where(g => g.Key == key).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken);
    }
}
