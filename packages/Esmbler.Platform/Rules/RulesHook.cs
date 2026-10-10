using Esmbler.Kernel;
using Esmbler.Kernel.Records;

namespace Esmbler.Platform.Rules;

/// <summary>
/// Data-level rules on every write path. Read-only actions are evaluated against the record as it was before the
/// change (a closed ticket stays read-only while being edited); required, options, set_value, and validation
/// against the resulting record.
/// </summary>
public sealed class RulesHook : IRecordWriteHook
{
    public int Order => 300;

    public ValueTask ApplyAsync(RecordWriteContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.Prior is not null && ctx.Operation != WriteOperations.System)
        {
            var before = RuleEvaluator.Evaluate(ctx.Config, ctx.Entity, ctx.PriorVariables());
            foreach (var field in ctx.Changed.Where(before.ReadOnly.Contains).ToList())
            {
                if (ctx.Operation == WriteOperations.Transition && ctx.Config.WorkflowFor(ctx.Entity.Id)?.StateField == field)
                {
                    continue;
                }

                var label = ctx.Entity.FindField(field)?.Label ?? field;
                ctx.Errors.Add(new FieldError(field, "read_only", $"{label} cannot be changed in this state."));
            }

            if (ctx.Errors.Count > 0)
            {
                return ValueTask.CompletedTask;
            }
        }

        var states = RuleEvaluator.Evaluate(ctx.Config, ctx.Entity, ctx.Variables());
        foreach (var (field, value) in states.SetValues)
        {
            ctx.SystemSet(field, RecordWriteEngine.ToJson(value));
        }

        foreach (var field in states.Required)
        {
            ctx.RequiredByRules.Add(field);
        }

        foreach (var (field, options) in states.Options)
        {
            ctx.RestrictedOptions[field] = options;
        }

        ctx.Messages.AddRange(states.Messages);

        // emit_event fires on the edge: the rule matches now and did not match before the change (or on create).
        var emitting = ctx.Config.Rules.Where(r => r.Entity == ctx.Entity.Id && states.Matched.Contains(r.Id) && r.Actions.Exists(a => a.Type == Sdk.Specs.RuleActionTypes.EmitEvent)).ToList();
        if (emitting.Count > 0)
        {
            var matchedBefore = ctx.Prior is null ? [] : RuleEvaluator.Evaluate(ctx.Config, ctx.Entity, ctx.PriorVariables()).Matched;
            var vars = ctx.Variables();
            foreach (var rule in emitting.Where(r => !matchedBefore.Contains(r.Id)))
            {
                foreach (var action in rule.Actions.Where(a => a.Type == Sdk.Specs.RuleActionTypes.EmitEvent && a.Emit is not null))
                {
                    if (EventEmission.Evaluate(ctx.Config, ctx.Entity, action.Emit!, vars, rule.Id) is { } emitted)
                    {
                        ctx.Emitted.Add(emitted);
                    }
                }
            }
        }
        ctx.Errors.AddRange(RuleEvaluator.Validate(ctx.Config, ctx.Entity, ctx.Variables()));
        return ValueTask.CompletedTask;
    }
}
