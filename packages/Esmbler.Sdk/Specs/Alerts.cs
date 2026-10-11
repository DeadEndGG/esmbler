namespace Esmbler.Sdk.Specs;

/// <summary>
/// Turns matching events into alerts with a lifecycle (ADR 0022): deduplicated by a key, owned by a group or person,
/// acknowledged by people, escalated on timers, resolved by people or by a resolving event, and silenced by maintenance
/// windows. Alerts never send messages: they publish <c>platform.alert.*</c> events that notification routes subscribe to.
/// </summary>
public sealed class AlertRuleSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Subscription (target <c>alert</c>) whose events raise the alert.</summary>
    public required string Trigger { get; set; }

    /// <summary>Only events at least this severe raise the alert (info, low, medium, high, critical).</summary>
    public string? MinSeverity { get; set; }

    /// <summary>The alert's severity; null takes the event's.</summary>
    public string? Severity { get; set; }

    /// <summary>
    /// Expression over <c>event</c> and <c>record</c> giving the deduplication key: one active alert per rule and key,
    /// later matches count as occurrences. Null keys by the event's record (or the event when it has none).
    /// </summary>
    public string? DedupKey { get; set; }

    /// <summary>Plain text with {{record.&lt;field&gt;}}, {{record.number}} and {{event.&lt;field&gt;}}; at most 200 characters.</summary>
    public required string Title { get; set; }

    /// <summary>Who responds: a group, a user, group or principals field of the record, or a person.</summary>
    public AlertOwnerSpec? Owner { get; set; }

    /// <summary>Escalation steps in order; each raises the alert's level and publishes <c>platform.alert.escalated</c>.</summary>
    public List<AlertEscalationStepSpec> Escalation { get; set; } = [];

    /// <summary>Subscriptions (target <c>alert</c>) whose events resolve the rule's active alerts about the same record or key.</summary>
    public List<string> ResolveOn { get; set; } = [];

    /// <summary>A new match within this many minutes of resolution reopens the alert instead of opening a new one.</summary>
    public int ReopenWithinMinutes { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class AlertOwnerSpec
{
    /// <summary><c>group</c> (group key), <c>field</c> (user, group or principals field of the record) or <c>user</c> (principal ID).</summary>
    public required string Kind { get; set; }

    public required string Ref { get; set; }
}

public sealed class AlertEscalationStepSpec
{
    /// <summary>Minutes after the alert opened (1 to 10,080), increasing from step to step.</summary>
    public int AfterMinutes { get; set; }

    /// <summary>Skip the step once someone acknowledged the alert (default).</summary>
    public bool UnlessAcknowledged { get; set; } = true;

    /// <summary>Last step only: repeat every this many minutes (at most 20 repeats).</summary>
    public int? RepeatEveryMinutes { get; set; }
}

public static class AlertOwnerKinds
{
    public const string Group = "group";
    public const string Field = "field";
    public const string User = "user";

    public static IReadOnlyList<string> All { get; } = [Group, Field, User];
}

public static class AlertStatuses
{
    public const string Open = "open";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";
    public const string Suppressed = "suppressed";

    public static IReadOnlyList<string> All { get; } = [Open, Acknowledged, Resolved, Suppressed];

    /// <summary>Statuses that still deduplicate new matches.</summary>
    public static bool IsActive(string status) => status is Open or Acknowledged or Suppressed;
}
