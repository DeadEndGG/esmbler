using System.Collections.Concurrent;
using Esmbler.Expressions;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Specs;

namespace Esmbler.Platform.Rules;

/// <summary>Effective state of each field after rules: drives rendering and server enforcement.</summary>
public sealed class FieldStates
{
    public HashSet<string> Required { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Shown { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ReadOnly { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<string>> Options { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, object?> SetValues { get; } = new(StringComparer.Ordinal);

    public List<RuleMessage> Messages { get; } = [];

    /// <summary>Rules that matched, in evaluation order (for simulation and explanations).</summary>
    public List<string> Matched { get; } = [];

    public List<(string RuleId, string Error)> Errors { get; } = [];
}

/// <summary>
/// Specification + Interpreter: evaluates rule specs with the safe expression engine. Evaluation contract:
/// enabled rules of the requested scopes run in (order, id) order; a condition that errors counts as not matched
/// and is reported; set_value actions apply in order so later rules see earlier values (cycles are rejected at compile time).
/// </summary>
public sealed class RuleEvaluator
{
    private static readonly ConcurrentDictionary<(string, string), CompiledExpression> Cache = new();

    public static FieldStates Evaluate(SpecCatalog config, EntitySpec entity, Dictionary<string, object?> variables, string? viewId = null, bool includeForm = false)
    {
        var states = new FieldStates();
        var rules = config.Rules
            .Where(r => r.Entity == entity.Id && r.Enabled && r.Kind == RuleKinds.Conditional && (r.Scope == RuleScopes.Data || (includeForm && r.View == viewId)))
            .OrderBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            bool matched;
            try
            {
                matched = string.IsNullOrWhiteSpace(rule.When) || Compile(entity, rule.When).EvaluateBool(variables);
            }
            catch (ExpressionException ex)
            {
                states.Errors.Add((rule.Id, ex.Message));
                continue;
            }

            if (!matched)
            {
                continue;
            }

            states.Matched.Add(rule.Id);
            foreach (var action in rule.Actions)
            {
                switch (action.Type)
                {
                    case RuleActionTypes.Require:
                        states.Required.Add(action.Field!);
                        break;
                    case RuleActionTypes.Hide:
                        states.Hidden.Add(action.Field!);
                        states.Shown.Remove(action.Field!);
                        break;
                    case RuleActionTypes.Show:
                        states.Shown.Add(action.Field!);
                        states.Hidden.Remove(action.Field!);
                        break;
                    case RuleActionTypes.ReadOnly:
                        states.ReadOnly.Add(action.Field!);
                        break;
                    case RuleActionTypes.RestrictOptions:
                        states.Options[action.Field!] = action.Options ?? [];
                        break;
                    case RuleActionTypes.SetValue:
                        try
                        {
                            var value = Compile(entity, action.Value!).Evaluate(variables);
                            states.SetValues[action.Field!] = value;
                            if (variables["record"] is IReadOnlyDictionary<string, object?> record)
                            {
                                var copy = new Dictionary<string, object?>(record, StringComparer.Ordinal) { [action.Field!] = value };
                                variables["record"] = copy;
                            }
                        }
                        catch (ExpressionException ex)
                        {
                            states.Errors.Add((rule.Id, ex.Message));
                        }

                        break;
                    case RuleActionTypes.Message:
                        states.Messages.Add(new RuleMessage(rule.Id, action.Tone ?? "info", action.Message!, action.Field));
                        break;
                }
            }
        }

        return states;
    }

    public static CompiledExpression Compile(EntitySpec entity, string expression) =>
        Cache.GetOrAdd((entity.Id, expression), key => Expression.Compile(key.Item2, EntitySchema.For(entity)));

    /// <summary>Evaluates validation rules against the resulting record. Errors (fail closed) count as violations.</summary>
    public static IEnumerable<FieldError> Validate(SpecCatalog config, EntitySpec entity, Dictionary<string, object?> variables)
    {
        foreach (var rule in config.Rules.Where(r => r.Entity == entity.Id && r.Enabled && r.Kind == RuleKinds.Validation && r.Scope == RuleScopes.Data).OrderBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            bool applies, ok;
            try
            {
                applies = string.IsNullOrWhiteSpace(rule.When) || Compile(entity, rule.When).EvaluateBool(variables);
                ok = !applies || Compile(entity, rule.Assert!).EvaluateBool(variables);
            }
            catch (ExpressionException)
            {
                ok = false;
            }

            if (!ok)
            {
                yield return new FieldError(rule.Field ?? "_record", "rule:" + rule.Id, rule.Message ?? "This record does not satisfy a validation rule.");
            }
        }
    }
}
