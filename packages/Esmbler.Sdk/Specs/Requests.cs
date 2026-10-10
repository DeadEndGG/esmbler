using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Specs;

/// <summary>
/// A service catalog item (ADR 0015): a requestable offering with typed variables and fulfillment task templates.
/// Configuration, not data: authored in drafts, published, upgraded and rolled back like any spec object.
/// </summary>
public sealed class CatalogItemSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Grouping shown in the catalog, e.g. "Hardware".</summary>
    public string? Category { get; set; }

    /// <summary>Icon name from the approved set (UI hint only).</summary>
    public string? Icon { get; set; }

    /// <summary>Entity that receives requests for this item; it must declare a request binding.</summary>
    public required string RequestEntity { get; set; }

    /// <summary>Inactive items stay addressable (existing requests keep working) but cannot be requested.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Lower comes first in the catalog. Ties break by label.</summary>
    public int Order { get; set; } = 100;

    public List<VariableSpec> Variables { get; set; } = [];

    public List<FulfillmentTaskSpec> Tasks { get; set; } = [];

    public VariableSpec? FindVariable(string key) => Variables.Find(v => v.Key == key);
}

/// <summary>A typed question on a catalog item. Values are normalized like fields of the same type.</summary>
public sealed class VariableSpec
{
    /// <summary>snake_case, unique within the item.</summary>
    public required string Key { get; set; }

    public required string Label { get; set; }

    public required string Type { get; set; }

    public string? Help { get; set; }

    public bool Required { get; set; }

    public string? ChoiceList { get; set; }

    public int? MaxLength { get; set; }

    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    public int? Scale { get; set; }

    public JsonNode? Default { get; set; }

    /// <summary>The field shape the record engine uses to normalize this variable's value.</summary>
    public FieldSpec AsField() => new()
    {
        Key = Key,
        Type = Type,
        Label = Label,
        Help = Help,
        Required = Required,
        ChoiceList = ChoiceList,
        MaxLength = MaxLength,
        Min = Min,
        Max = Max,
        Scale = Scale,
        Default = Default?.DeepClone(),
    };
}

public static class VariableTypes
{
    public static IReadOnlyList<string> All { get; } =
    [
        FieldTypes.ShortText, FieldTypes.LongText, FieldTypes.Integer, FieldTypes.Decimal, FieldTypes.Date, FieldTypes.DateTime,
        FieldTypes.SingleSelect, FieldTypes.MultiSelect, FieldTypes.Toggle, FieldTypes.Email, FieldTypes.User, FieldTypes.Group,
    ];
}

/// <summary>A fulfillment task template. Created when the request starts fulfillment, released when its dependencies finish.</summary>
public sealed class FulfillmentTaskSpec
{
    /// <summary>snake_case, unique within the item; stored on the task as its key (idempotency).</summary>
    public required string Id { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    /// <summary>Group key (resolved per tenant) that receives the task.</summary>
    public string? AssignmentGroup { get; set; }

    /// <summary>Task IDs of the same item that must finish first.</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>Condition over <c>variables</c> and <c>record</c> (the request). False: the task is not created.</summary>
    public string? When { get; set; }
}

/// <summary>Declares that an entity receives catalog requests and how fulfillment tasks attach to it.</summary>
public sealed class RequestBindingSpec
{
    public string CatalogItemField { get; set; } = "catalog_item";

    public string VariablesField { get; set; } = "variables";

    /// <summary>Entity of the fulfillment tasks.</summary>
    public required string TaskEntity { get; set; }

    /// <summary>Lookup field on the task that points at the request.</summary>
    public string TaskRequestField { get; set; } = "request";

    /// <summary>Text field on the task that stores the template ID.</summary>
    public string TaskKeyField { get; set; } = "task_key";

    public string TaskTitleField { get; set; } = "title";

    public string? TaskDescriptionField { get; set; } = "description";

    public string? TaskGroupField { get; set; } = "assignment_group";

    /// <summary>Request states whose entry starts fulfillment (task creation).</summary>
    public List<string> StartStates { get; set; } = [];

    /// <summary>System transition fired on the request when every task has finished.</summary>
    public required string CompleteTransition { get; set; }

    /// <summary>Optional single_select field set to "complete" or "partial" when fulfillment finishes.</summary>
    public string? OutcomeField { get; set; }

    /// <summary>System transition that moves a blocked task to ready.</summary>
    public required string TaskReleaseTransition { get; set; }

    /// <summary>System transition that cancels an unfinished task when its request is rejected or cancelled.</summary>
    public required string TaskCancelTransition { get; set; }
}

public static class FulfillmentOutcomes
{
    public const string Complete = "complete";
    public const string Partial = "partial";
}
