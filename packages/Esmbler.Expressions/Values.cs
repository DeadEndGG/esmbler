using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esmbler.Expressions;

/// <summary>Converts JSON into expression values (null, bool, double, string, list, map). All numbers are doubles.</summary>
public static class Values
{
    public static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        _ => null,
    };

    public static object? FromJson(JsonNode? node) => node is null ? null : FromJson(JsonSerializer.SerializeToElement(node));

    public static IReadOnlyDictionary<string, object?> MapFromJson(JsonObject? obj) =>
        obj is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : obj.ToDictionary(p => p.Key, p => FromJson(p.Value), StringComparer.Ordinal);
}
