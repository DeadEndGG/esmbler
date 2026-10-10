using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Specs;

namespace Esmbler.Platform.Requests;

/// <summary>
/// Catalog requests (ADR 0015, REQ-BIZ-004): on every write path, a request's catalog item must exist, be active
/// (on create) and target this entity, and never changes afterwards; its variables are normalized like fields of
/// their types, defaults applied, unknown keys and missing required answers rejected. Errors target
/// <c>variables.&lt;key&gt;</c> so forms can show them next to the question. Answers can change only while the
/// request is still in its workflow's initial state: once it moves on (triage, approval, fulfillment), task conditions
/// and reviews depend on them. Fulfillment task links (request and task key) are written only by fulfillment, so
/// nobody can fake or move a finished prerequisite. Requests whose item names an approval policy cannot be moved into
/// fulfillment by people; the approval engine does that once every stage is approved (ADR 0016).
/// </summary>
public sealed class RequestHook(FieldNormalizer normalizer) : IRecordWriteHook
{
    public int Order => 150;

    public ValueTask ApplyAsync(RecordWriteContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.Entity.Request is not { } binding)
        {
            if (!ctx.SystemActor && ctx.Config.RequestEntityForTask(ctx.Entity.Id)?.Request is { } owner)
            {
                ProtectTaskLinks(ctx, owner);
            }

            return ValueTask.CompletedTask;
        }

        var itemId = ctx.Data[binding.CatalogItemField]?.GetValue<string>();
        var priorItem = ctx.Prior?[binding.CatalogItemField]?.GetValue<string>();
        if (ctx.Prior is not null && priorItem != itemId)
        {
            ctx.Errors.Add(new FieldError(binding.CatalogItemField, "read_only", "The catalog item of a request cannot change.", "Cancel this request and raise a new one."));
            return ValueTask.CompletedTask;
        }

        if (itemId is null)
        {
            return ValueTask.CompletedTask;
        }

        var item = ctx.Config.FindCatalogItem(itemId);
        if (item is null || item.RequestEntity != ctx.Entity.Id)
        {
            ctx.Errors.Add(new FieldError(binding.CatalogItemField, "unknown_catalog_item", $"'{itemId}' is not a catalog item for {ctx.Entity.PluralLabel.ToLowerInvariant()}."));
            return ValueTask.CompletedTask;
        }

        if (ctx.Prior is null && !item.Active)
        {
            ctx.Errors.Add(new FieldError(binding.CatalogItemField, "inactive_catalog_item", $"'{item.Label}' can no longer be requested.", "Choose another item from the catalog."));
            return ValueTask.CompletedTask;
        }

        // Approvals drive the workflow in and out of the approval state; people cannot step around them (ADR 0016).
        if (ctx.Operation == WriteOperations.Transition && !ctx.SystemActor && binding.Approval is { } approval && ctx.EffectiveWorkflow is { } flow
            && ctx.FromState is { } from && ctx.ToState is { } to && ApprovalError(item, binding, approval, flow, from, to) is { } refusal)
        {
            ctx.Errors.Add(refusal);
            return ValueTask.CompletedTask;
        }

        var titleField = ctx.Entity.TitleField;
        if (ctx.Prior is null && ctx.Data[titleField] is null)
        {
            ctx.SystemSet(titleField, JsonValue.Create(item.Label));
        }

        // What approvers and fulfillers relied on cannot change once the request leaves intake.
        if (ctx.Prior is not null && !ctx.SystemActor && ctx.EffectiveWorkflow is { } wf && (ctx.Prior[wf.StateField]?.GetValue<string>() ?? wf.InitialState) != wf.InitialState)
        {
            if (ctx.Changed.Contains(binding.VariablesField))
            {
                ctx.Errors.Add(new FieldError(binding.VariablesField, "read_only", "Answers cannot change once the request is being worked on.", "Add a comment with the change, or cancel this request and raise a new one."));
                return ValueTask.CompletedTask;
            }

            foreach (var key in binding.LockedFields.Where(ctx.Changed.Contains))
            {
                var label = ctx.Entity.FindField(key)?.Label ?? key;
                ctx.Errors.Add(new FieldError(key, "read_only", $"{label} cannot change once the request is being worked on.", "Add a comment with the change, or cancel this request and raise a new one."));
            }

            if (ctx.Errors.Count > 0)
            {
                return ValueTask.CompletedTask;
            }
        }

        if (ctx.Prior is null || ctx.Changed.Contains(binding.VariablesField))
        {
            NormalizeVariables(ctx, binding, item);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// People may not move a request that needs approval out of intake (except to reject or cancel it), out of the
    /// approval state (except to cancel it), or into the approval state. A record pinned to a workflow version without the approval state predates
    /// approvals and is grandfathered (ASM-046).
    /// </summary>
    private static FieldError? ApprovalError(CatalogItemSpec item, RequestBindingSpec binding, ApprovalBindingSpec approval, WorkflowSpec flow, string from, string to)
    {
        if (flow.FindState(approval.State) is null || flow.FindTransition(approval.RequestTransition) is null)
        {
            return null;
        }

        // From intake, a request that needs approval goes nowhere but approval (by the engine) or a cancelled state:
        // any other path (straight to fulfillment, or via a tenant state such as triage) would skip the approvers.
        if (item.ApprovalPolicy is not null && from == flow.InitialState && flow.FindState(to)?.Category != StateCategories.Cancelled)
        {
            return new FieldError(flow.StateField, "approval_required", $"'{item.Label}' needs approval before anyone works on it.", "It moves on by itself once the approvers sign off; you can still reject or cancel it.");
        }

        if (from == approval.State && flow.FindState(to)?.Category != StateCategories.Cancelled)
        {
            return new FieldError(flow.StateField, "approval_pending", "This request is waiting for approval; only the approvers can move it on.", "Cancel it instead, or ask the approvers to decide.");
        }

        if (to == approval.State && from != approval.State)
        {
            return new FieldError(flow.StateField, "approval_pending", "Only the approval engine sends requests for approval.");
        }

        return null;
    }

    private static void ProtectTaskLinks(RecordWriteContext ctx, RequestBindingSpec owner)
    {
        if (ctx.Prior is null ? ctx.Data[owner.TaskKeyField] is not null : ctx.Changed.Contains(owner.TaskKeyField))
        {
            ctx.Errors.Add(new FieldError(owner.TaskKeyField, "read_only", "The task template is set by fulfillment.", "Leave it empty; fulfillment fills it for the tasks it creates."));
        }

        if (ctx.Prior is not null && ctx.Changed.Contains(owner.TaskRequestField))
        {
            ctx.Errors.Add(new FieldError(owner.TaskRequestField, "read_only", "A task cannot move to another request.", "Create a new task on the other request instead."));
        }
    }

    private void NormalizeVariables(RecordWriteContext ctx, RequestBindingSpec binding, CatalogItemSpec item)
    {
        var input = ctx.Data[binding.VariablesField] as JsonObject ?? [];
        var output = new JsonObject();
        foreach (var (key, value) in input)
        {
            if (item.FindVariable(key) is null)
            {
                var suggestion = Expression.Suggest(key, item.Variables.Select(v => v.Key));
                ctx.Errors.Add(new FieldError($"{binding.VariablesField}.{key}", "unknown_variable", $"'{item.Label}' has no question '{key}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), suggestion));
            }
        }

        foreach (var variable in item.Variables)
        {
            var field = variable.AsField();
            var target = $"{binding.VariablesField}.{variable.Key}";
            var raw = input[variable.Key];
            if (raw is null && ctx.Prior is null && variable.Default is not null)
            {
                raw = variable.Default.DeepClone();
            }

            var (normalized, error) = normalizer.Normalize(field, raw, ctx.Config);
            if (error is not null)
            {
                ctx.Errors.Add(error with { Field = target });
                continue;
            }

            if (normalized is null || (normalized is JsonArray a && a.Count == 0))
            {
                if (variable.Required)
                {
                    ctx.Errors.Add(new FieldError(target, "required", $"{variable.Label} is required."));
                }

                continue;
            }

            output[variable.Key] = normalized;
        }

        ctx.SystemSet(binding.VariablesField, output);
    }
}
