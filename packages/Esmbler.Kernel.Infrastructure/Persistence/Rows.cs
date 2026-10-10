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

    public DateTimeOffset CreatedAt { get; set; }
}
