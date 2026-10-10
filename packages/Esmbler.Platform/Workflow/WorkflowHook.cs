using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Specs;

namespace Esmbler.Platform.Workflow;

/// <summary>
/// Declarative state machine. Creates start in the initial state; the state field changes only through
/// transitions valid from the current state, allowed for the caller's roles, passing guards and required fields.
/// </summary>
public sealed class WorkflowHook : IRecordWriteHook
{
    public int Order => 100;

    public ValueTask ApplyAsync(RecordWriteContext ctx, CancellationToken cancellationToken)
    {
        var wf = ctx.EffectiveWorkflow;
        if (wf is null)
        {
            if (ctx.Operation == WriteOperations.Transition)
            {
                ctx.Errors.Add(new FieldError("_record", "no_workflow", $"{ctx.Entity.PluralLabel} have no workflow."));
            }

            return ValueTask.CompletedTask;
        }

        switch (ctx.Operation)
        {
            case WriteOperations.Create:
                ctx.SystemSet(wf.StateField, JsonValue.Create(wf.InitialState));
                break;
            case WriteOperations.Transition:
                Transition(ctx, wf);
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Transitions the caller may fire from the record's current state (drives transition buttons).</summary>
    public static IEnumerable<WorkflowTransition> Available(WorkflowSpec wf, JsonObject data, IReadOnlyCollection<string> roles)
    {
        var state = data[wf.StateField]?.GetValue<string>() ?? wf.InitialState;
        return wf.Transitions.Where(t => t.Enabled && !t.System && (t.From.Contains(state) || t.From.Contains("*")) && (t.Roles.Count == 0 || t.Roles.Exists(roles.Contains)));
    }

    private static void Transition(RecordWriteContext ctx, WorkflowSpec wf)
    {
        var transition = wf.Transitions.Find(t => t.Id == ctx.TransitionId && (!t.System || ctx.SystemActor));
        var current = ctx.Prior?[wf.StateField]?.GetValue<string>() ?? wf.InitialState;
        if (transition is { Enabled: false })
        {
            ctx.Errors.Add(new FieldError(wf.StateField, "transition_disabled", $"'{transition.Label}' is turned off for this workspace."));
            return;
        }

        if (transition is null)
        {
            var available = Available(wf, ctx.Prior ?? [], ctx.Permissions.Roles).Select(t => t.Id).ToList();
            ctx.Errors.Add(new FieldError(wf.StateField, "unknown_transition", $"There is no transition '{ctx.TransitionId}'.", available.Count > 0 ? $"Available now: {string.Join(", ", available)}." : null));
            return;
        }

        if (!transition.From.Contains(current) && !transition.From.Contains("*"))
        {
            var label = ctx.Entity.FindField(wf.StateField)?.ChoiceList is { } listId ? ctx.Config.FindChoiceList(listId)?.Find(current)?.Label ?? current : current;
            ctx.Errors.Add(new FieldError(wf.StateField, "invalid_transition", $"'{transition.Label}' is not available while the {ctx.Entity.Label.ToLowerInvariant()} is {label}."));
            return;
        }

        if (!ctx.SystemActor && transition.Roles.Count > 0 && !transition.Roles.Exists(ctx.Permissions.Roles.Contains))
        {
            throw KernelException.Forbidden($"You cannot '{transition.Label}' this {ctx.Entity.Label.ToLowerInvariant()}.");
        }

        var vars = ctx.Variables();
        if (transition.Guard is { } guard)
        {
            bool ok;
            try
            {
                ok = Expression.Compile(guard, EntitySchema.For(ctx.Entity)).EvaluateBool(vars);
            }
            catch (ExpressionException)
            {
                ok = false;
            }

            if (!ok)
            {
                ctx.Errors.Add(new FieldError(wf.StateField, "guard_failed", transition.GuardMessage ?? $"'{transition.Label}' is not allowed for this record right now."));
                return;
            }
        }

        foreach (var (field, expr) in transition.Set)
        {
            var value = Expression.Compile(expr, EntitySchema.For(ctx.Entity)).Evaluate(vars);
            ctx.SystemSet(field, RecordWriteEngine.ToJson(value));
        }

        ctx.SystemSet(wf.StateField, JsonValue.Create(transition.To));
        foreach (var field in transition.RequiredFields)
        {
            ctx.RequiredByRules.Add(field);
        }

        ctx.FromState = current;
        ctx.ToState = transition.To;
        if (transition.Emits.Count > 0)
        {
            var emitVars = ctx.Variables();
            foreach (var emit in transition.Emits)
            {
                if (EventEmission.Evaluate(ctx.Config, ctx.Entity, emit, emitVars, $"{wf.Id}/{transition.Id}") is { } emitted)
                {
                    ctx.Emitted.Add(emitted);
                }
            }
        }
    }
}
