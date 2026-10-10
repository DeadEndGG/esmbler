using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Esmbler.Sdk.Specs;

/// <summary>Canonical JSON settings for every spec, overlay operation, and artifact.</summary>
public static class SpecJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            RespectNullableAnnotations = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Could not read {typeof(T).Name}.");

    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>Canonical form: object keys sorted ordinally, no whitespace. Used for content hashes.</summary>
    public static string Canonical(JsonNode? node)
    {
        return Canon(node)?.ToJsonString() ?? "null";

        static JsonNode? Canon(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canon(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Canon).ToArray()),
            null => null,
            _ => n.DeepClone(),
        };
    }
}
