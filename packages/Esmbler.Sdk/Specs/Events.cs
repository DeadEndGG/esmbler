namespace Esmbler.Sdk.Specs;

/// <summary>
/// A published event type (ADR 0017, REQ-EVT-008): its ID, version, payload fields, default severity and sensitivity.
/// Modules, packages and platform services declare the events they publish; subscribers (notifications, alerts,
/// automations, integrations) select them by ID without the publisher knowing who listens.
/// </summary>
public sealed class EventTypeSpec
{
    /// <summary>e.g. core.tickets.ticket.escalated, platform.approval.requested; tenant types start with tenant__.</summary>
    public required string Id { get; set; }

    /// <summary>Payload schema version. Additive changes keep it; a breaking change publishes version + 1.</summary>
    public int Version { get; set; } = 1;

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Entity of the record the event is about, if any.</summary>
    public string? SubjectEntity { get; set; }

    /// <summary>Payload fields by key (snake_case).</summary>
    public Dictionary<string, EventFieldSpec> Fields { get; set; } = new(StringComparer.Ordinal);

    /// <summary>info, low, medium, high or critical; an emitter may compute a different one.</summary>
    public string Severity { get; set; } = EventSeverities.Info;

    /// <summary>public, internal or restricted: who may receive payload values (external recipients get public only).</summary>
    public string Sensitivity { get; set; } = EventSensitivities.Internal;

    /// <summary>operational, security, audit or informational (for routing and filtering).</summary>
    public string Category { get; set; } = "operational";
}

/// <summary>A payload field: a small, non-secret value (IDs, choice IDs, numbers, flags, short text).</summary>
public sealed class EventFieldSpec
{
    /// <summary>string, number, boolean or list.</summary>
    public required string Type { get; set; }

    public bool Required { get; set; }

    public string? Description { get; set; }
}

public static class EventFieldTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string List = "list";

    public static IReadOnlyList<string> All { get; } = [String, Number, Boolean, List];
}

public static class EventSeverities
{
    public const string Info = "info";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static IReadOnlyList<string> All { get; } = [Info, Low, Medium, High, Critical];

    /// <summary>Order for comparisons (min-severity filters).</summary>
    public static int Rank(string? severity) => severity is null ? -1 : All.ToList().IndexOf(severity);
}

public static class EventSensitivities
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Restricted = "restricted";

    public static IReadOnlyList<string> All { get; } = [Public, Internal, Restricted];
}

/// <summary>
/// Declarative emission of a domain event (on a workflow transition, or by an emit_event rule action): the event
/// type, payload values as expressions over <c>record</c>, <c>prior</c>, <c>user</c> and <c>now</c>, and an optional
/// severity expression.
/// </summary>
public sealed class EventEmitSpec
{
    public required string Type { get; set; }

    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Expression producing a severity, e.g. <c>record.priority == 'p1' ? 'critical' : 'high'</c>; null uses the type's.</summary>
    public string? Severity { get; set; }
}
