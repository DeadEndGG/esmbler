using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Sdk.Registry;
using Json.Schema;

namespace Esmbler.Kernel.Configuration;

/// <summary>Validates node props against element props schemas (JSON Schema 2020-12). Schemas are cached per element version.</summary>
public sealed class JsonSchemaPropsValidator : IPropsValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _cache = new(StringComparer.Ordinal);

    public IEnumerable<string> Validate(ElementDefinition element, JsonObject props)
    {
        var schema = _cache.GetOrAdd($"{element.Id}@{element.Version}", _ => JsonSchema.FromText(element.PropsSchema.ToJsonString()));
        var instance = JsonSerializer.SerializeToElement(props);
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        foreach (var detail in (result.Details ?? []).Prepend(result))
        {
            if (detail.Errors is null)
            {
                continue;
            }

            var location = detail.InstanceLocation.ToString();
            foreach (var (keyword, message) in detail.Errors)
            {
                var where = string.IsNullOrEmpty(location) || location == "/" ? "props" : $"props{location.Replace('/', '.')}";
                if (keyword == "additionalProperties")
                {
                    continue; // reported per property below, via the "false" subschema
                }

                errors.Add(keyword == "false" || message.Contains("false schema", StringComparison.Ordinal)
                    ? $"'{where[(where.LastIndexOf('.') + 1)..]}' is not a supported property. Allowed: {string.Join(", ", AllowedProps(element))}."
                    : $"{where}: {message}");
            }
        }

        return errors.Count == 0 ? ["props are invalid."] : errors.Distinct();
    }

    private static IEnumerable<string> AllowedProps(ElementDefinition element) =>
        element.PropsSchema["properties"] is JsonObject properties ? properties.Select(p => p.Key) : [];
}
