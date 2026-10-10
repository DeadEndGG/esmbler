using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Request fulfillment (ADR 0015, REQ-BIZ-004). Reacts to request and task events by reconciling the request's tasks
/// with its <em>current</em> state, never with the event payload, so duplicate, replayed or out-of-order events lead to
/// the same result:
/// <list type="bullet">
/// <item>request in a start state: create the applicable task templates that do not exist yet (keyed by request and
/// task key), release those whose dependencies are finished, and complete the request when every task is finished;</item>
/// <item>request in a cancelled-category state (rejected, cancelled): cancel its unfinished tasks;</item>
/// <item>task event: reconcile its request.</item>
/// </list>
/// The request row is locked first, so concurrent reconciliations of one request serialize. Writes run through the
/// record engine as system writes in the dispatcher's tenant transaction, together with their audit and events.
/// </summary>
public sealed class FulfillmentConsumer(ConfigStore configs, RecordService records, IClock clock, ILogger<FulfillmentConsumer> logger) : IEventConsumer
{
    public string Name => "fulfillment";

    public bool Handles(EventEnvelope envelope) =>
        envelope.RecordId is not null && envelope.Type is EventTypes.RecordCreated or EventTypes.RecordTransitioned or EventTypes.RecordRestored;

    public async Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, envelope.TenantId, envelope.EnvironmentId, ct);
        var config = artifact.Config;
        if (config.FindEntity(envelope.Entity ?? string.Empty) is not { } entity)
        {
            return;
        }

        Guid requestId;
        EntitySpec requestEntity;
        if (entity.Request is not null)
        {
            requestEntity = entity;
            requestId = envelope.RecordId!.Value;
        }
        else if (config.RequestEntityForTask(entity.Id) is { } owner)
        {
            var task = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == envelope.RecordId && r.DeletedAt == null, ct);
            if (task is null || JsonNode.Parse(task.Data)?[owner.Request!.TaskRequestField]?.GetValue<string>() is not { } link || !Guid.TryParse(link, out requestId))
            {
                return;
            }

            requestEntity = owner;
        }
        else
        {
            return;
        }

        var ctx = new RequestContext
        {
            TenantId = envelope.TenantId,
            EnvironmentId = envelope.EnvironmentId,
            PrincipalId = Principal(envelope),
            Channel = "worker",
            CorrelationId = envelope.CorrelationId,
        };
        await ReconcileAsync(db, ctx, artifact, requestEntity, requestId, ct);
    }

    private async Task ReconcileAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec requestEntity, Guid requestId, CancellationToken ct)
    {
        var config = artifact.Config;
        var binding = requestEntity.Request!;
        var taskEntity = config.FindEntity(binding.TaskEntity);
        var requestFlow = config.WorkflowFor(requestEntity.Id);
        var taskFlow = config.WorkflowFor(binding.TaskEntity);
        if (taskEntity is null || requestFlow is null || taskFlow is null)
        {
            return;
        }

        var locked = await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE id = {requestId} AND deleted_at IS NULL FOR UPDATE").AsNoTracking().ToListAsync(ct);
        if (locked.FirstOrDefault() is not { } request || request.EntityId != requestEntity.Id)
        {
            return;
        }

        var data = JsonNode.Parse(request.Data)!.AsObject();
        var state = data[requestFlow.StateField]?.GetValue<string>() ?? requestFlow.InitialState;
        var category = requestFlow.FindState(state)?.Category;
        var tasks = await LoadTasksAsync(db, binding, taskEntity, taskFlow, requestId, ct);

        if (category == StateCategories.Cancelled)
        {
            foreach (var t in tasks.Where(t => !t.Finished))
            {
                await records.SystemWriteAsync(db, ctx, artifact, taskEntity, t.Id, binding.TaskCancelTransition, [], ct);
            }

            return;
        }

        if (!binding.StartStates.Contains(state))
        {
            return;
        }

        if (config.FindCatalogItem(data[binding.CatalogItemField]?.GetValue<string>() ?? string.Empty) is not { } item)
        {
            logger.LogWarning("Request {Request} references a catalog item that no longer exists; fulfillment skipped", requestId);
            return;
        }

        var applicable = Applicable(item, requestEntity, data, ctx);
        foreach (var template in applicable.Where(a => tasks.All(t => t.Key != a.Id)))
        {
            var groupId = template.AssignmentGroup is { } key ? await db.Groups.Where(g => g.Key == key).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct) : null;
            var taskData = new JsonObject
            {
                [binding.TaskTitleField] = template.Title,
                [binding.TaskRequestField] = requestId.ToString(),
                [binding.TaskKeyField] = template.Id,
            };
            if (binding.TaskDescriptionField is { } descriptionField && template.Description is { } description)
            {
                taskData[descriptionField] = description;
            }

            if (binding.TaskGroupField is { } groupField && groupId is { } gid)
            {
                taskData[groupField] = gid.ToString();
            }

            await records.SystemCreateAsync(db, ctx, artifact, taskEntity, taskData, ct);
        }

        tasks = await LoadTasksAsync(db, binding, taskEntity, taskFlow, requestId, ct);
        var applicableIds = applicable.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var finished = tasks.Where(t => t.Finished).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var blockedCategory = taskFlow.FindState(taskFlow.InitialState)?.Category;
        foreach (var t in tasks.Where(t => !t.Finished && t.Category == blockedCategory && t.State == taskFlow.InitialState))
        {
            var template = item.Tasks.Find(x => x.Id == t.Key);
            var ready = template is null || template.DependsOn.All(dep => !applicableIds.Contains(dep) || finished.Contains(dep));
            if (ready)
            {
                await records.SystemWriteAsync(db, ctx, artifact, taskEntity, t.Id, binding.TaskReleaseTransition, [], ct);
            }
        }

        if (tasks.All(t => t.Finished))
        {
            var changes = new JsonObject();
            if (binding.OutcomeField is { } outcomeField)
            {
                changes[outcomeField] = tasks.Any(t => t.Category == StateCategories.Cancelled) ? FulfillmentOutcomes.Partial : FulfillmentOutcomes.Complete;
            }

            await records.SystemWriteAsync(db, ctx, artifact, requestEntity, requestId, binding.CompleteTransition, changes, ct);
        }
    }

    private List<FulfillmentTaskSpec> Applicable(CatalogItemSpec item, EntitySpec requestEntity, JsonObject data, RequestContext ctx)
    {
        var binding = requestEntity.Request!;
        var schema = EntitySchema.ForRequest(requestEntity, item);
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["record"] = Values.MapFromJson(data),
            ["prior"] = Values.MapFromJson(data),
            ["user"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = ctx.PrincipalId.ToString(), ["roles"] = new List<object?>(), ["groups"] = new List<object?>() },
            ["now"] = Kernel.Records.FieldNormalizer.FormatInstant(clock.UtcNow),
            ["variables"] = Values.MapFromJson(data[binding.VariablesField] as JsonObject),
        };
        var result = new List<FulfillmentTaskSpec>();
        foreach (var t in item.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.When))
            {
                result.Add(t);
                continue;
            }

            try
            {
                if (Expression.Compile(t.When, schema).EvaluateBool(vars))
                {
                    result.Add(t);
                }
            }
            catch (ExpressionException ex)
            {
                logger.LogWarning("Condition of task {Task} on {Item} failed ({Message}); the task is created so no work is lost", t.Id, item.Id, ex.Message);
                result.Add(t);
            }
        }

        return result;
    }

    private static async Task<List<TaskState>> LoadTasksAsync(KernelDbContext db, RequestBindingSpec binding, EntitySpec taskEntity, WorkflowSpec taskFlow, Guid requestId, CancellationToken ct)
    {
        var probe = new JsonObject { [binding.TaskRequestField] = requestId.ToString() }.ToJsonString();
        var rows = await db.Records.AsNoTracking()
            .Where(r => r.EntityId == taskEntity.Id && r.DeletedAt == null && EF.Functions.JsonContains(r.Data, probe))
            .Select(r => new { r.Id, r.Data })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var d = JsonNode.Parse(r.Data)!.AsObject();
            var state = d[taskFlow.StateField]?.GetValue<string>() ?? taskFlow.InitialState;
            var category = taskFlow.FindState(state)?.Category ?? StateCategories.Active;
            return new TaskState(r.Id, d[binding.TaskKeyField]?.GetValue<string>() ?? string.Empty, state, category);
        }).ToList();
    }

    private static Guid Principal(EventEnvelope envelope) =>
        envelope.Actor.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(envelope.Actor[5..], out var id) ? id : Guid.Empty;

    private sealed record TaskState(Guid Id, string Key, string State, string Category)
    {
        public bool Finished => Category is StateCategories.Resolved or StateCategories.Closed or StateCategories.Cancelled;
    }
}
