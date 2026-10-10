using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Specs;

/// <summary>Field type identifiers. Each maps to a registry element <c>field.&lt;type&gt;</c>.</summary>
public static class FieldTypes
{
    public const string ShortText = "short_text";
    public const string LongText = "long_text";
    public const string RichText = "rich_text";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Date = "date";
    public const string DateTime = "date_time";
    public const string SingleSelect = "single_select";
    public const string MultiSelect = "multi_select";
    public const string Toggle = "toggle";
    public const string Status = "status";
    public const string Priority = "priority";
    public const string Email = "email";
    public const string Lookup = "lookup";
    public const string User = "user";
    public const string Group = "group";
    public const string AutoNumber = "auto_number";

    /// <summary>ID of a catalog item (requests). Validated against the active configuration.</summary>
    public const string CatalogItem = "catalog_item";

    /// <summary>Typed answers to a catalog item's variables, as an object keyed by variable key (requests).</summary>
    public const string Variables = "variables";

    public static IReadOnlyList<string> All { get; } =
    [
        ShortText, LongText, RichText, Integer, Decimal, Date, DateTime, SingleSelect, MultiSelect, Toggle, Status, Priority,
        Email, Lookup, User, Group, AutoNumber, CatalogItem, Variables,
    ];

    public static bool UsesChoiceList(string type) => type is SingleSelect or MultiSelect or Status or Priority;

    public static bool IsText(string type) => type is ShortText or LongText or RichText or Email;

    public static bool IsNumber(string type) => type is Integer or Decimal;
}

public sealed class EntitySpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public required string PluralLabel { get; set; }

    public string? Description { get; set; }

    /// <summary>Field key whose value names a record in lookups, headers, and activity.</summary>
    public required string TitleField { get; set; }

    public List<FieldSpec> Fields { get; set; } = [];

    /// <summary>User/group fields that make a viewer "related" to a record (record-level access, ReBAC).</summary>
    public List<AccessFieldSpec> AccessFields { get; set; } = [];

    /// <summary>Set when the entity receives catalog requests (ADR 0015).</summary>
    public RequestBindingSpec? Request { get; set; }

    public FieldSpec? FindField(string key) => Fields.Find(f => f.Key == key);
}

public sealed class AccessFieldSpec
{
    /// <summary>A user or group field on the entity.</summary>
    public required string Field { get; set; }

    /// <summary>Relation name used in the authorization graph, e.g. requester, assignee, owner_group.</summary>
    public required string Relation { get; set; }

    /// <summary>Whether this relation grants edit (true) or only view (false).</summary>
    public bool CanEdit { get; set; }
}

public sealed class FieldSpec
{
    /// <summary>Storage/API key, unique within the entity. Base keys have no "__"; tenant keys start with "tenant__".</summary>
    public required string Key { get; set; }

    public required string Type { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    public string? Help { get; set; }

    public bool Required { get; set; }

    public bool ReadOnly { get; set; }

    /// <summary>Hidden by an overlay. Hidden fields keep their data and remain addressable by stable ID.</summary>
    public bool Hidden { get; set; }

    /// <summary>Retired fields keep their values but accept no new writes (contract phase of expand/backfill/contract).</summary>
    public bool Deprecated { get; set; }

    /// <summary>Platform-maintained (auto-number, timestamps). Never writable through the record API.</summary>
    public bool System { get; set; }

    public int? MaxLength { get; set; }

    public int? MinLength { get; set; }

    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    /// <summary>Digits after the decimal point for decimal fields (stored as JSON numbers, rounded half-away-from-zero).</summary>
    public int? Scale { get; set; }

    public string? Pattern { get; set; }

    public string? PatternMessage { get; set; }

    public string? ChoiceList { get; set; }

    /// <summary>Target entity for lookups.</summary>
    public string? Target { get; set; }

    public bool Unique { get; set; }

    public JsonNode? Default { get; set; }

    /// <summary>Expression evaluated on create when no value is given, e.g. "user.id".</summary>
    public string? DefaultExpression { get; set; }

    /// <summary>Multi-select only: maximum number of selected options.</summary>
    public int? MaxItems { get; set; }

    /// <summary>Auto-number prefix, e.g. "TCK".</summary>
    public string? Prefix { get; set; }
}

public sealed class ChoiceListSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public List<ChoiceOption> Options { get; set; } = [];

    public ChoiceOption? Find(string id) => Options.Find(o => o.Id == id);
}

public sealed class ChoiceOption
{
    /// <summary>Stable ID stored in records. Never reused; relabel instead of renaming.</summary>
    public required string Id { get; set; }

    public required string Label { get; set; }

    /// <summary>Design token for badges, e.g. "accent.red". Never a raw color.</summary>
    public string? Tone { get; set; }

    public bool Hidden { get; set; }
}

public sealed class RelationshipSpec
{
    public required string Id { get; set; }

    /// <summary>lookup (many-to-one, stored on <see cref="Entity"/>.<see cref="Field"/>); its inverse is the one-to-many side.</summary>
    public string Kind { get; set; } = "lookup";

    public required string Entity { get; set; }

    public required string Field { get; set; }

    public required string Target { get; set; }

    public required string Label { get; set; }

    public required string InverseLabel { get; set; }

    /// <summary>restrict (block deleting targets with children) or clear (null the lookup).</summary>
    public string OnTargetDelete { get; set; } = "restrict";
}
