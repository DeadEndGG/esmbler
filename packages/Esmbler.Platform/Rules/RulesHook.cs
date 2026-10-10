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
        ctx.Errors.AddRange(RuleEvaluator.Validate(ctx.Config, ctx.Entity, ctx.Variables()));
        return ValueTask.CompletedTask;
    }
}
