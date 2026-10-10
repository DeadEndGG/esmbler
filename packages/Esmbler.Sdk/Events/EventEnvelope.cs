using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Events;

/// <summary>
/// Domain event envelope (master spec "Transactions, events, and durable execution"). Delivered at least once;
/// consumers deduplicate by <see cref="EventId"/> and order by <see cref="RecordRevision"/> per record.
/// </summary>
public sealed record EventEnvelope
{
    public required Guid EventId { get; init; }

    /// <summary>e.g. record.created, record.updated, record.transitioned, config.published.</summary>
    public required string Type { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required Guid TenantId { get; init; }

    public required Guid EnvironmentId { get; init; }

    public string? Entity { get; init; }

    public Guid? RecordId { get; init; }

    public int? RecordRevision { get; init; }

    /// <summary>Configuration revision the change was made under.</summary>
    public int? ConfigRevision { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    /// <summary>Principal that caused the event: user:&lt;id&gt;, agent:&lt;id&gt;, module:&lt;id&gt;, system.</summary>
    public required string Actor { get; init; }

    /// <summary>Minimal payload: changed field keys and, for transitions, from/to states. Never secrets.</summary>
    public JsonObject Payload { get; init; } = [];

    /// <summary>Causation depth, used to stop automation cycles.</summary>
    public int Depth { get; init; }
}

public static class EventTypes
{
    public const string RecordCreated = "record.created";
    public const string RecordUpdated = "record.updated";
    public const string RecordTransitioned = "record.transitioned";
    public const string RecordDeleted = "record.deleted";
    public const string ActivityAdded = "activity.added";
    public const string ConfigPublished = "config.published";
    public const string DraftChanged = "draft.changed";
}
