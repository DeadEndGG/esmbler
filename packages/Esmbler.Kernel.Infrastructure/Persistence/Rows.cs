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
