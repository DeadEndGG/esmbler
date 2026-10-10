using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Registry;

public static class ElementCategories
{
    public const string SpecObject = "spec_object";
    public const string Layout = "layout";
    public const string FieldType = "field_type";
    public const string Field = "field";
    public const string Display = "display";
    public const string Collection = "collection";
    public const string Activity = "activity";
    public const string Action = "action";
    public const string Feedback = "feedback";
}

/// <summary>
/// Self-describing registry entry (master spec "Element catalog"). Written for both the renderer and an LLM:
/// description, usage, and constraints are what the AI builder reads through MCP.
/// Schema: packages/Esmbler.Sdk/Schemas/element.schema.json.
/// </summary>
public sealed class ElementDefinition
{
    public required string Id { get; set; }

    public required string Version { get; set; }

    public required string Category { get; set; }

    /// <summary>Delivery gate that introduced the element.</summary>
    public int Gate { get; set; } = 1;

    public required string Label { get; set; }

    public required string Description { get; set; }

    public List<string> Usage { get; set; } = [];

    public List<string> Constraints { get; set; } = [];

    /// <summary>Whether the element can appear as a node in a page layout tree.</summary>
    public bool Placeable { get; set; }

    /// <summary>JSON Schema 2020-12 for node props (or for the spec object itself).</summary>
    public JsonObject PropsSchema { get; set; } = new() { ["type"] = "object" };

    /// <summary>Field types only: JSON Schema of the stored value.</summary>
    public JsonObject? ValueSchema { get; set; }

    /// <summary>Element IDs (or "category.*" patterns) this element may be placed in.</summary>
    public List<string> AllowedParents { get; set; } = [];

    /// <summary>Element IDs (or "category.*" patterns) allowed as children. Empty = leaf.</summary>
    public List<string> AllowedChildren { get; set; } = [];

    public int? MaxChildren { get; set; }

    public ElementBinding Binding { get; set; } = new();

    public List<string> Events { get; set; } = [];

    /// <summary>Rule actions that affect this element: required, visible, read_only, options.</summary>
    public List<string> RuleHooks { get; set; } = [];

    public List<string> Tokens { get; set; } = [];

    public ElementAccessibility Accessibility { get; set; } = new();

    public string? Responsive { get; set; }

    public List<ElementExample> Examples { get; set; } = [];

    public bool AllowsChild(string childType) => Matches(AllowedChildren, childType);

    public bool AllowsParent(string parentType) => Matches(AllowedParents, parentType);

    private static bool Matches(List<string> patterns, string type) =>
        patterns.Exists(p => p == type || p == "*" || (p.EndsWith(".*", StringComparison.Ordinal) && type.StartsWith(p[..^1], StringComparison.Ordinal)));
}

public sealed class ElementBinding
{
    /// <summary>none, field, record, query, relationship, workflow.</summary>
    public string Kind { get; set; } = "none";

    public string? Description { get; set; }
}

public sealed class ElementAccessibility
{
    public string? Role { get; set; }

    public string? Keyboard { get; set; }

    public string? Notes { get; set; }
}

public sealed class ElementExample
{
    public required string Description { get; set; }

    public JsonNode? Spec { get; set; }
}
