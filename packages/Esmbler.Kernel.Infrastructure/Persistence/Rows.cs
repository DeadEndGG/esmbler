namespace Esmbler.Kernel.Infrastructure.Persistence;

#pragma warning disable SA1402, SA1649 // Row types are grouped: one file mirrors the kernel schema (Migrations/0001_kernel.sql).

public sealed class TenantRow
{
    public Guid Id { get; set; }

    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";

    public string Status { get; set; } = "active";

    public string? AuthzStoreId { get; set; }

    public string? AuthzModelId { get; set; }

    /// <summary>Bumped by triggers on every membership, group or role change (permission cache key).</summary>
    public long AuthzRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EnvironmentRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string Kind { get; set; } = "production";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PrincipalRow
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = "user";

    public string? Email { get; set; }

    public string DisplayName { get; set; } = "";

    public string? PasswordHash { get; set; }

    public Guid? ActsFor { get; set; }

    public Guid? AgentTenant { get; set; }

    public bool Disabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MembershipRow
{
    public Guid TenantId { get; set; }

    public Guid PrincipalId { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GroupRow
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GroupMemberRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    public Guid? PrincipalId { get; set; }

    public Guid? MemberGroup { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RoleAssignmentRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public string RoleId { get; set; } = "";

    public Guid? PrincipalId { get; set; }

    public Guid? GroupId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

public sealed class InstallationRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string ModuleId { get; set; } = "";

    public string Version { get; set; } = "";

    public string UpdatePolicy { get; set; } = "manual";

    public DateTimeOffset InstalledAt { get; set; }
}

public sealed class ConfigArtifactRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public int Revision { get; set; }

    public string ContentHash { get; set; } = "";

    public string Manifest { get; set; } = "{}";

    public string Config { get; set; } = "{}";

    public string Overlay { get; set; } = "[]";

    public string Status { get; set; } = "active";

    public string Source { get; set; } = "install";

    public Guid? CreatedBy { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ActiveConfigRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public int Revision { get; set; }

    public int? PreviousRevision { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    public Guid? ActivatedBy { get; set; }
}

public sealed class DraftRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public int BaseRevision { get; set; }

    public int Version { get; set; }

    public string Status { get; set; } = "open";

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DraftCommandRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid DraftId { get; set; }

    public int Seq { get; set; }

    public string Op { get; set; } = "{}";

    public string Summary { get; set; } = "";

    public string[] Targets { get; set; } = [];

    public Guid Actor { get; set; }

    public Guid? Agent { get; set; }

    public string Source { get; set; } = "ui";

    public bool Undone { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Steps applied together share a batch and are undone together (REQ-WS-005).</summary>
    public Guid? BatchId { get; set; }
}

public sealed class PublishRequestRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid DraftId { get; set; }

    public int DraftVersion { get; set; }

    public string ArtifactHash { get; set; } = "";

    public string Summary { get; set; } = "";

    public Guid RequestedBy { get; set; }

    public Guid? RequestedAgent { get; set; }

    public string Status { get; set; } = "pending";

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RecordRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string EntityId { get; set; } = "";

    public long? Number { get; set; }

    public int Revision { get; set; }

    public string Data { get; set; } = "{}";

    public int ConfigRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Workflow definition version the record runs on (REQ-MIG-003); null only before its first pin.</summary>
    public int? WorkflowVersion { get; set; }
}

/// <summary>An immutable, registered version of a workflow definition (ADR 0015).</summary>
public sealed class WorkflowDefinitionRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string WorkflowId { get; set; } = "";

    public int Version { get; set; }

    public string ContentHash { get; set; } = "";

    public string Definition { get; set; } = "{}";

    public int ConfigRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An approval of one record (ADR 0016). <see cref="PolicySnapshot"/> is the policy it started under.</summary>
public sealed class ApprovalRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid RecordId { get; set; }

    public string EntityId { get; set; } = "";

    public string PolicyId { get; set; } = "";

    public string PolicySnapshot { get; set; } = "{}";

    public string Status { get; set; } = "pending";

    public int StageIndex { get; set; }

    public Guid? Requester { get; set; }

    /// <summary>JSON array of the people the request was by or for when the approval started (separation of duties).</summary>
    public string Requesters { get; set; } = "[]";

    public string? OutcomeReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>One approver's step in one stage. Kind user, group or role; <see cref="ApproverRef"/> is the ID or role.</summary>
public sealed class ApprovalStepRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid ApprovalId { get; set; }

    public Guid RecordId { get; set; }

    public int StageIndex { get; set; }

    public string StageId { get; set; } = "";

    public string ApproverKind { get; set; } = "";

    public string ApproverRef { get; set; } = "";

    public string Status { get; set; } = "pending";

    public Guid? DecidedBy { get; set; }

    public Guid? OnBehalfOf { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public bool Escalation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An out-of-office delegation of approvals from one person to another for a period.</summary>
public sealed class ApprovalDelegationRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid Delegator { get; set; }

    public Guid Delegate { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RecordSequenceRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string EntityId { get; set; } = "";

    public long NextValue { get; set; }
}

public sealed class ActivityRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid RecordId { get; set; }

    public string Kind { get; set; } = "work_note";

    public string Body { get; set; } = "";

    public Guid? Author { get; set; }

    public Guid? Agent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RedactedAt { get; set; }

    /// <summary>"email" when the reply came by email (ADR 0023).</summary>
    public string? Source { get; set; }
}

public sealed class IdempotencyRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string Key { get; set; } = "";

    public Guid PrincipalId { get; set; }

    public string RequestHash { get; set; } = "";

    public string Response { get; set; } = "{}";

    public int StatusCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditRow
{
    public Guid TenantId { get; set; }

    public Guid? EnvironmentId { get; set; }

    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public Guid? Actor { get; set; }

    public Guid? Agent { get; set; }

    public string Channel { get; set; } = "api";

    public string Operation { get; set; } = "";

    public string TargetType { get; set; } = "";

    public string? TargetId { get; set; }

    public int? ConfigRevision { get; set; }

    public string Outcome { get; set; } = "success";

    public Guid? CorrelationId { get; set; }

    public string Details { get; set; } = "{}";
}

public sealed class OutboxRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string Type { get; set; } = "";

    public Guid? RecordId { get; set; }

    public int? RecordRevision { get; set; }

    public string Event { get; set; } = "{}";

    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LockedBy { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }
}

/// <summary>A consumer that failed on an event (ADR 0017): retried and dead-lettered on its own.</summary>
public sealed class ConsumerFailureRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string Consumer { get; set; } = "";

    public Guid EventId { get; set; }

    public string EventType { get; set; } = "";

    public int Attempts { get; set; }

    public bool Dead { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset FirstFailedAt { get; set; }

    public DateTimeOffset LastFailedAt { get; set; }
}

public sealed class InboxRow
{
    public string Consumer { get; set; } = "";

    public Guid EventId { get; set; }

    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class ProjectionRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string Consumer { get; set; } = "";

    public Guid RecordId { get; set; }

    public int RecordRevision { get; set; }

    public string State { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class JobRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string Kind { get; set; } = "";

    public string Payload { get; set; } = "{}";

    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 5;

    public DateTimeOffset AvailableAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LockedBy { get; set; }

    public string? Checkpoint { get; set; }

    public string? LastError { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>At most one live (pending or running) job per key, e.g. "sla:{recordId}:resolution".</summary>
    public string? DedupKey { get; set; }

    /// <summary>The event that caused this job (fan-out limit).</summary>
    public Guid? CausationId { get; set; }

    /// <summary>Automation depth inherited from the causing event (cycle limit).</summary>
    public int Depth { get; set; }
}

public sealed class MigrationRunRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid UpgradeId { get; set; }

    public string MigrationId { get; set; } = "";

    public string Status { get; set; } = "pending";

    public int Processed { get; set; }

    public int Converted { get; set; }

    public int Invalid { get; set; }

    public Guid? Checkpoint { get; set; }

    public string Report { get; set; } = "[]";

    public string? LastError { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class UpgradeRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string ModuleId { get; set; } = "";

    public string FromVersion { get; set; } = "";

    public string ToVersion { get; set; } = "";

    public string Status { get; set; } = "planned";

    public string Report { get; set; } = "{}";

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NotificationRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string TemplateId { get; set; } = "";

    public Guid? Recipient { get; set; }

    public string? RecipientEmail { get; set; }

    public string Subject { get; set; } = "";

    public string Body { get; set; } = "";

    public Guid EventId { get; set; }

    public string Channel { get; set; } = "in_app_fake";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the recipient read it (inbox, G2-M18).</summary>
    public DateTimeOffset? ReadAt { get; set; }

    public Guid? DeliveryId { get; set; }

    public string? Severity { get; set; }

    public string? EntityId { get; set; }

    public Guid? RecordId { get; set; }
}

/// <summary>A channel instance of an environment (ADR 0021): settings and secret references, never secret values.</summary>
public sealed class NotificationChannelRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string Key { get; set; } = "";

    public string Type { get; set; } = "";

    public string Label { get; set; } = "";

    public string Mode { get; set; } = "capture";

    public string Settings { get; set; } = "{}";

    public string SecretRefs { get; set; } = "{}";

    public int RatePerMinute { get; set; } = 60;

    public int Version { get; set; } = 1;

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One recipient and channel for an event and route (ADR 0021).</summary>
public sealed class NotificationDeliveryRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public string EventType { get; set; } = "";

    public string SubscriptionId { get; set; } = "";

    public string RouteId { get; set; } = "";

    public string TemplateId { get; set; } = "";

    public string ProfileId { get; set; } = "";

    public string ChannelType { get; set; } = "";

    public string? ChannelKey { get; set; }

    public Guid? RecipientPrincipal { get; set; }

    public string? RecipientAddress { get; set; }

    public bool External { get; set; }

    public bool Mandatory { get; set; }

    public string? Severity { get; set; }

    public string? EntityId { get; set; }

    public Guid? RecordId { get; set; }

    public string Status { get; set; } = "queued";

    public string? StatusReason { get; set; }

    public int Attempts { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public string? ProviderMessageId { get; set; }

    public string Subject { get; set; } = "";

    public string Body { get; set; } = "";

    public int ConfigRevision { get; set; }

    public Guid? ReplayOf { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The Message-ID an email delivery was sent with, for reply threading (ADR 0023).</summary>
    public string? MessageId { get; set; }
}

public sealed class NotificationDeliveryAttemptRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid DeliveryId { get; set; }

    public int Attempt { get; set; }

    public string Outcome { get; set; } = "";

    public string? Code { get; set; }

    public string? ProviderMessageId { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>A person's mute of a route ("*" channel) or of one channel of it; mandatory routes ignore it.</summary>
public sealed class NotificationPreferenceRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid PrincipalId { get; set; }

    public string RouteId { get; set; } = "";

    public string ChannelType { get; set; } = "*";

    public bool Muted { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An alert (ADR 0022): deduplicated by rule and key while active.</summary>
public sealed class AlertRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string RuleId { get; set; } = "";

    public string DedupKey { get; set; } = "";

    public string Status { get; set; } = "open";

    public string Severity { get; set; } = "high";

    public string Title { get; set; } = "";

    public string? EntityId { get; set; }

    public Guid? RecordId { get; set; }

    public Guid? OwnerGroup { get; set; }

    public string? OwnerGroupKey { get; set; }

    public Guid? OwnerPrincipal { get; set; }

    public Guid? Assignee { get; set; }

    public int Level { get; set; } = 1;

    public int Occurrences { get; set; } = 1;

    public int ReopenedCount { get; set; }

    public Guid FirstEventId { get; set; }

    public Guid LastEventId { get; set; }

    public Guid? SuppressionId { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset LastOccurredAt { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    public Guid? AcknowledgedBy { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public Guid? ResolvedBy { get; set; }

    public string? Resolution { get; set; }

    public int ConfigRevision { get; set; }

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AlertActivityRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid AlertId { get; set; }

    public string Kind { get; set; } = "";

    public Guid? Actor { get; set; }

    public Guid? Agent { get; set; }

    public int? Level { get; set; }

    public string? Details { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class AlertSuppressionRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string Reason { get; set; } = "";

    public string? RuleId { get; set; }

    public string? EntityId { get; set; }

    public Guid? RecordId { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }
}

/// <summary>The head of a workspace's audit chain (ADR 0022); written only by the definer functions.</summary>
public sealed class AuditChainHeadRow
{
    public Guid TenantId { get; set; }

    public long LastSeq { get; set; }

    public byte[] LastHash { get; set; } = [];

    public long AnchorSeq { get; set; }

    public byte[] AnchorHash { get; set; } = [];

    public DateTimeOffset? SealedAt { get; set; }

    public long? VerifyCursor { get; set; }

    public byte[]? VerifyCursorHash { get; set; }

    public long VerifiedThroughSeq { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public long? BrokenAtSeq { get; set; }

    public DateTimeOffset? BrokenDetectedAt { get; set; }
}

public sealed class AuditPolicyRow
{
    public Guid TenantId { get; set; }

    public int RetentionDays { get; set; } = 2555;

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LegalHoldRow
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Reason { get; set; } = "";

    public string Scope { get; set; } = "all";

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? ReleasedBy { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }
}

public sealed class RefreshTokenRow
{
    public Guid Id { get; set; }

    public Guid PrincipalId { get; set; }

    public string TokenHash { get; set; } = "";

    public Guid? TenantId { get; set; }

    public Guid? EnvironmentId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? ReplacedBy { get; set; }

    /// <summary>When the session (token family) began; rotation never extends past the absolute session lifetime.</summary>
    public DateTimeOffset SessionStartedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An SLA timer of a record (ADR 0018); <see cref="Snapshot"/> is the target, goal and calendar it started with.</summary>
public sealed class SlaTimerRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid RecordId { get; set; }

    public string EntityId { get; set; } = "";

    public string PolicyId { get; set; } = "";

    public string TargetId { get; set; } = "";

    public int Cycle { get; set; }

    public int? ConfigRevision { get; set; }

    public string Snapshot { get; set; } = "{}";

    public long GoalSeconds { get; set; }

    public string Status { get; set; } = "running";

    public DateTimeOffset StartedAt { get; set; }

    public long ElapsedSeconds { get; set; }

    public DateTimeOffset? SegmentStartedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public DateTimeOffset? WarnAt { get; set; }

    public DateTimeOffset? WarnedAt { get; set; }

    public DateTimeOffset? BreachedAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    public string? Outcome { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A workspace mailbox (environment setting) that feeds a mail intake (ADR 0023).</summary>
public sealed class MailInboxRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string Address { get; set; } = "";

    public string IntakeId { get; set; } = "";

    public string Mode { get; set; } = "disabled";

    public string Settings { get; set; } = "{}";

    public byte[]? TokenHash { get; set; }

    public long? ImapUidValidity { get; set; }

    public long? ImapLastUid { get; set; }

    public DateTimeOffset? PolledAt { get; set; }

    public string? PollError { get; set; }

    public int Version { get; set; } = 1;

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One inbound message per mailbox and Message-ID (ADR 0023).</summary>
public sealed class InboundMessageRow
{
    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    public Guid Id { get; set; }

    public Guid InboxId { get; set; }

    public string MessageKey { get; set; } = "";

    public string? MessageId { get; set; }

    public string? FromAddress { get; set; }

    public string? FromName { get; set; }

    public string? Subject { get; set; }

    public string Status { get; set; } = "received";

    public string? Reason { get; set; }

    public string? Outcome { get; set; }

    public Guid? RecordId { get; set; }

    public string? EntityId { get; set; }

    public Guid? ActivityId { get; set; }

    public byte[]? Raw { get; set; }

    /// <summary>Recipients, threading headers, verification and loop signals, attachments (names and sizes only).</summary>
    public string Details { get; set; } = "{}";

    public int SizeBytes { get; set; }

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>A workspace's AI provider settings (ADR 0024); names a catalog model, never a key.</summary>
public sealed class AiSettingsRow
{
    public Guid TenantId { get; set; }

    public string? ModelId { get; set; }

    public string? Effort { get; set; }

    public decimal DailyBudgetUsd { get; set; } = 20;

    public bool AllowRecordsRead { get; set; }

    public string ExternalAssistants { get; set; } = "off";

    public int Version { get; set; } = 1;

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One model request's usage and estimated cost (ADR 0024).</summary>
public sealed class AiUsageRow
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? EnvironmentId { get; set; }

    public Guid? PrincipalId { get; set; }

    public string? ConversationId { get; set; }

    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int CachedTokens { get; set; }

    public decimal CostUsd { get; set; }

    public DateTimeOffset At { get; set; }
}
