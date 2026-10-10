using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Records;

/// <summary>A domain event a write publishes alongside its record event (ADR 0017). <see cref="Emitter"/> is the rule or transition (<c>workflow/transition</c>) that published it.</summary>
public sealed record EmittedEvent(string Type, int TypeVersion, JsonObject Payload, string Severity, string Sensitivity, string Emitter);

/// <summary>
/// Evaluates a declarative emission (<see cref="EventEmitSpec"/>) against the record being written. Payload values are
/// expressions over <c>record</c>, <c>prior</c>, <c>user</c> and <c>now</c>. A value that fails to evaluate, or does not
/// match its declared payload type, is left out (the event still publishes, so subscribers are never silently skipped);
/// text is capped at <see cref="MaxText"/> characters, because payloads are small and subscribers read the record.
/// </summary>
public static class EventEmission
{
    public const int MaxText = 1000;

    public static EmittedEvent? Evaluate(SpecCatalog config, EntitySpec entity, EventEmitSpec emit, Dictionary<string, object?> variables, string emitter)
    {
        if (config.FindEventType(emit.Type) is not { } type)
        {
            return null;
        }

        var schema = EntitySchema.For(entity);
        var payload = new JsonObject();
        foreach (var (key, expression) in emit.Payload)
        {
            try
            {
                var value = RecordWriteEngine.ToJson(Expression.Compile(expression, schema).Evaluate(variables));
                if (type.Fields.TryGetValue(key, out var field) && Fit(value, field.Type) is { } fitted)
                {
                    payload[key] = fitted;
                }
            }
            catch (ExpressionException)
            {
                // Left out: the validator checks expressions; a runtime failure (e.g. a missing value) must not block the write.
            }
        }

        var severity = type.Severity;
        if (!string.IsNullOrWhiteSpace(emit.Severity))
        {
            try
            {
                if (Expression.Compile(emit.Severity, schema).Evaluate(variables) is string s && EventSeverities.All.Contains(s))
                {
                    severity = s;
                }
            }
            catch (ExpressionException)
            {
                // Falls back to the type's severity.
            }
        }

        return new EmittedEvent(type.Id, type.Version, payload, severity, type.Sensitivity, emitter);
    }

    /// <summary>The value if it matches the declared payload type (text capped), otherwise null.</summary>
    private static JsonNode? Fit(JsonNode? value, string type) => (type, value) switch
    {
        (_, null) => null,
        (EventFieldTypes.String, JsonValue v) when v.TryGetValue<string>(out var s) => JsonValue.Create(s.Length > MaxText ? s[..MaxText] : s),
        (EventFieldTypes.Number, JsonValue v) when v.TryGetValue<decimal>(out _) || v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _) => v,
        (EventFieldTypes.Boolean, JsonValue v) when v.TryGetValue<bool>(out _) => v,
        (EventFieldTypes.List, JsonArray a) when a.Count <= 100 => a,
        _ => null,
    };
}
