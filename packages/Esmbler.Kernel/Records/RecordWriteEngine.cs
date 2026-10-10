using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Records;

public static class WriteOperations
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Transition = "transition";
    public const string System = "system";
}

public sealed record RuleMessage(string RuleId, string Tone, string Text, string? Field = null);

/// <summary>Mutable state flowing through the write pipeline for one command.</summary>
public sealed class RecordWriteContext
{
    public required EffectiveConfig Config { get; init; }

    public required EntitySpec Entity { get; init; }

    public required EffectivePermissions Permissions { get; init; }

    public required string Operation { get; init; }

    public required string Now { get; init; }

    public JsonObject? Prior { get; init; }

    public required JsonObject Data { get; init; }

    public HashSet<string> Changed { get; } = new(StringComparer.Ordinal);

    public List<FieldError> Errors { get; } = [];

    public List<RuleMessage> Messages { get; } = [];

    public HashSet<string> RequiredByRules { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<string>> RestrictedOptions { get; } = new(StringComparer.Ordinal);

    public string? TransitionId { get; init; }

    /// <summary>Workflow state before and after a transition.</summary>
    public string? FromState { get; set; }

    public string? ToState { get; set; }

    public Dictionary<string, object?> Variables() => Permissions.Variables(Entity, Data, Prior, Now);

    public Dictionary<string, object?> PriorVariables() => Permissions.Variables(Entity, Prior ?? [], Prior, Now);

    /// <summary>Sets a field as a trusted platform write (workflow, rule set_value, assignment), bypassing field permissions.</summary>
    public void SystemSet(string field, JsonNode? value)
    {
        if (value is null)
        {
            Data.Remove(field);
        }
        else
        {
            Data[field] = value.DeepClone();
        }

        Changed.Add(field);
    }
}

/// <summary>Pipeline hook (Registry + Extension Point): rules, workflow, assignment plug in here.</summary>
public interface IRecordWriteHook
{
    /// <summary>Lower runs first. Workflow 100, assignment 200, rules 300.</summary>
    int Order { get; }

    ValueTask ApplyAsync(RecordWriteContext ctx, CancellationToken cancellationToken);
}

/// <summary>
/// Data-level write pipeline enforced on every write path (REQ-RULE-004): field permissions → normalization →
/// defaults → hooks (workflow, assignment, rules) → required and option checks. Throws ValidationFailedException
/// listing all problems.
/// </summary>
public sealed class RecordWriteEngine(FieldNormalizer normalizer, IEnumerable<IRecordWriteHook> hooks)
{
    private readonly IReadOnlyList<IRecordWriteHook> _hooks = hooks.OrderBy(h => h.Order).ToList();

    public async Task<RecordWriteContext> PrepareAsync(
        EffectiveConfig config,
        EntitySpec entity,
        EffectivePermissions permissions,
        string operation,
        JsonObject? prior,
        JsonObject changes,
        string now,
        string? transitionId,
        CancellationToken cancellationToken)
    {
        var data = prior?.DeepClone().AsObject() ?? new JsonObject();
        var ctx = new RecordWriteContext
        {
            Config = config,
            Entity = entity,
            Permissions = permissions,
            Operation = operation,
            Now = now,
            Prior = prior,
            Data = data,
            TransitionId = transitionId,
        };

        foreach (var (key, value) in changes)
        {
            var field = entity.FindField(key);
            if (field is null)
            {
                var suggestion = Expression.Suggest(key, entity.Fields.Where(f => !f.Hidden).Select(f => f.Key));
                ctx.Errors.Add(new FieldError(key, "unknown_field", $"{entity.Label} has no field '{key}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), suggestion));
                continue;
            }

            if (operation != WriteOperations.System)
            {
                if (field.System || field.Type == FieldTypes.AutoNumber)
                {
                    ctx.Errors.Add(new FieldError(key, "read_only", $"{field.Label} is maintained by the platform."));
                    continue;
                }

                if (field.ReadOnly)
                {
                    var hint = config.WorkflowFor(entity.Id)?.StateField == key ? "Use a workflow transition instead." : null;
                    ctx.Errors.Add(new FieldError(key, "read_only", $"{field.Label} is read-only.", hint));
                    continue;
                }

                if (field.Deprecated)
                {
                    ctx.Errors.Add(new FieldError(key, "deprecated", $"{field.Label} is retired and no longer accepts changes."));
                    continue;
                }

                if (permissions.FieldAccess(entity, key) != FieldAccessLevels.Write)
                {
                    ctx.Errors.Add(new FieldError(key, "forbidden_field", $"You cannot change {field.Label}."));
                    continue;
                }
            }

            var (normalized, error) = normalizer.Normalize(field, value, config);
            if (error is not null)
            {
                ctx.Errors.Add(error);
                continue;
            }

            var before = prior?[key];
            if (!JsonNode.DeepEquals(before, normalized))
            {
                if (normalized is null)
                {
                    data.Remove(key);
                }
                else
                {
                    data[key] = normalized;
                }

                ctx.Changed.Add(key);
            }
        }

        if (operation == WriteOperations.Create)
        {
            ApplyDefaults(ctx);
        }

        if (ctx.Errors.Count == 0)
        {
            foreach (var hook in _hooks)
            {
                await hook.ApplyAsync(ctx, cancellationToken);
                if (ctx.Errors.Count > 0)
                {
                    break;
                }
            }
        }

        if (ctx.Errors.All(e => e.Code is not ("unknown_field" or "invalid_type" or "forbidden_field")))
        {
            CheckRequired(ctx);
            CheckOptions(ctx);
        }

        if (ctx.Errors.Count > 0)
        {
            throw new ValidationFailedException(ctx.Errors);
        }

        return ctx;
    }

    private static void ApplyDefaults(RecordWriteContext ctx)
    {
        foreach (var field in ctx.Entity.Fields)
        {
            if (ctx.Data.ContainsKey(field.Key) || field.System)
            {
                continue;
            }

            if (field.Default is not null)
            {
                ctx.Data[field.Key] = field.Default.DeepClone();
            }
            else if (field.DefaultExpression is { } expr)
            {
                var value = Expression.Compile(expr, EntitySchema.For(ctx.Entity)).Evaluate(ctx.Variables());
                if (value is not null)
                {
                    ctx.Data[field.Key] = ToJson(value);
                }
            }
            else if (field.Type == FieldTypes.Toggle)
            {
                ctx.Data[field.Key] = false;
            }
        }
    }

    private static void CheckRequired(RecordWriteContext ctx)
    {
        foreach (var field in ctx.Entity.Fields)
        {
            var required = field.Required || ctx.RequiredByRules.Contains(field.Key);
            if (!required || field.System || field.Type == FieldTypes.AutoNumber)
            {
                continue;
            }

            var value = ctx.Data[field.Key];
            if (value is null || (value is JsonArray a && a.Count == 0))
            {
                ctx.Errors.Add(new FieldError(field.Key, "required", $"{field.Label} is required."));
            }
        }
    }

    private static void CheckOptions(RecordWriteContext ctx)
    {
        foreach (var (key, allowed) in ctx.RestrictedOptions)
        {
            if (!ctx.Changed.Contains(key) || ctx.Data[key] is not { } value)
            {
                continue;
            }

            var chosen = value is JsonArray arr ? arr.Select(v => v!.GetValue<string>()) : [value.GetValue<string>()];
            foreach (var c in chosen.Where(c => !allowed.Contains(c)))
            {
                var field = ctx.Entity.FindField(key)!;
                ctx.Errors.Add(new FieldError(key, "option_not_allowed", $"'{c}' is not allowed for {field.Label} here.", $"Choose one of: {string.Join(", ", allowed)}."));
            }
        }
    }

    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        double d when d == Math.Floor(d) && Math.Abs(d) < 9e15 => JsonValue.Create((long)d),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        IReadOnlyList<object?> list => new JsonArray(list.Select(ToJson).ToArray()),
        IReadOnlyDictionary<string, object?> map => new JsonObject(map.Select(kv => KeyValuePair.Create(kv.Key, ToJson(kv.Value)))),
        _ => JsonValue.Create(value.ToString()),
    };
}
