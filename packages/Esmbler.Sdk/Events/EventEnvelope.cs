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

    /// <summary>Envelope version: 2 adds source, type version, severity, sensitivity, trace context (ADR 0017). Consumers read 1 and 2.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>Module or platform service that published the event (core.tickets, platform.approvals, …).</summary>
    public string? Source { get; init; }

    /// <summary>Payload schema version of <see cref="Type"/> (the event type registry).</summary>
    public int TypeVersion { get; init; } = 1;

    /// <summary>info, low, medium, high or critical; declared by the event type, possibly computed by the emitter.</summary>
    public string? Severity { get; init; }

    /// <summary>public, internal or restricted: limits who may receive payload values.</summary>
    public string? Sensitivity { get; init; }

    /// <summary>Rule or transition (<c>workflow/transition</c>) that published a declared domain event; null for record and platform events.</summary>
    public string? Emitter { get; init; }

    /// <summary>W3C trace context of the request that caused the event, when there was one.</summary>
    public string? Traceparent { get; init; }

    public required Guid TenantId { get; init; }

    public required Guid EnvironmentId { get; init; }

    public string? Entity { get; init; }

    public Guid? RecordId { get; init; }

    public int? RecordRevision { get; init; }

    /// <summary>Configuration revision the change was made under.</summary>
    public int? ConfigRevision { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>When the platform recorded the event (outbox write); equals <see cref="OccurredAt"/> unless the source was late.</summary>
    public DateTimeOffset? RecordedAt { get; init; }

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
    public const string RecordRestored = "record.restored";
    public const string ActivityAdded = "activity.added";
    public const string ConfigPublished = "config.published";
    public const string DraftChanged = "draft.changed";

    // Platform service events (ADR 0016, ADR 0017).
    public const string SlaWarning = "platform.sla.warning";
    public const string SlaBreached = "platform.sla.breached";
    public const string ApprovalRequested = "platform.approval.requested";
    public const string ApprovalApproved = "platform.approval.approved";
    public const string ApprovalRejected = "platform.approval.rejected";
    public const string ApprovalCancelled = "platform.approval.cancelled";

    // Alerts (ADR 0022): the alert service publishes; notification routes subscribe.
    public const string AlertOpened = "platform.alert.opened";
    public const string AlertAcknowledged = "platform.alert.acknowledged";
    public const string AlertEscalated = "platform.alert.escalated";
    public const string AlertAssigned = "platform.alert.assigned";
    public const string AlertResolved = "platform.alert.resolved";

    /// <summary>Prefix of every alert event type.</summary>
    public const string AlertPrefix = "platform.alert.";

    /// <summary>Whether an event type is an alert event (alert events never raise alerts).</summary>
    public static bool IsAlert(string type) => type.StartsWith(AlertPrefix, StringComparison.Ordinal);
}
