using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping of the kernel schema. Never used directly against a pooled connection: always through
/// <see cref="Database"/>, which sets the tenant context for the transaction first.
/// </summary>
public sealed class KernelDbContext(DbContextOptions<KernelDbContext> options) : DbContext(options)
{
    public DbSet<TenantRow> Tenants => Set<TenantRow>();

    public DbSet<EnvironmentRow> Environments => Set<EnvironmentRow>();

    public DbSet<PrincipalRow> Principals => Set<PrincipalRow>();

    public DbSet<MembershipRow> Memberships => Set<MembershipRow>();

    public DbSet<GroupRow> Groups => Set<GroupRow>();

    public DbSet<GroupMemberRow> GroupMembers => Set<GroupMemberRow>();

    public DbSet<RoleAssignmentRow> RoleAssignments => Set<RoleAssignmentRow>();

    public DbSet<InstallationRow> Installations => Set<InstallationRow>();

    public DbSet<ConfigArtifactRow> ConfigArtifacts => Set<ConfigArtifactRow>();

    public DbSet<ActiveConfigRow> ActiveConfig => Set<ActiveConfigRow>();

    public DbSet<DraftRow> Drafts => Set<DraftRow>();

    public DbSet<DraftCommandRow> DraftCommands => Set<DraftCommandRow>();

    public DbSet<PublishRequestRow> PublishRequests => Set<PublishRequestRow>();

    public DbSet<RecordRow> Records => Set<RecordRow>();

    public DbSet<RecordSequenceRow> RecordSequences => Set<RecordSequenceRow>();

    public DbSet<ActivityRow> Activity => Set<ActivityRow>();

    public DbSet<IdempotencyRow> IdempotencyKeys => Set<IdempotencyRow>();

    public DbSet<AuditRow> AuditLog => Set<AuditRow>();

    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    public DbSet<InboxRow> Inbox => Set<InboxRow>();

    public DbSet<ProjectionRow> Projections => Set<ProjectionRow>();

    public DbSet<JobRow> Jobs => Set<JobRow>();

    public DbSet<MigrationRunRow> MigrationRuns => Set<MigrationRunRow>();

    public DbSet<UpgradeRow> Upgrades => Set<UpgradeRow>();

    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();

    public DbSet<NotificationChannelRow> NotificationChannels => Set<NotificationChannelRow>();

    public DbSet<NotificationDeliveryRow> NotificationDeliveries => Set<NotificationDeliveryRow>();

    public DbSet<MailInboxRow> MailInboxes => Set<MailInboxRow>();

    public DbSet<InboundMessageRow> InboundMessages => Set<InboundMessageRow>();

    public DbSet<AiSettingsRow> AiSettings => Set<AiSettingsRow>();

    public DbSet<AiUsageRow> AiUsage => Set<AiUsageRow>();

    public DbSet<NotificationDeliveryAttemptRow> NotificationDeliveryAttempts => Set<NotificationDeliveryAttemptRow>();

    public DbSet<NotificationPreferenceRow> NotificationPreferences => Set<NotificationPreferenceRow>();

    public DbSet<AlertRow> Alerts => Set<AlertRow>();

    public DbSet<AlertActivityRow> AlertActivity => Set<AlertActivityRow>();

    public DbSet<AlertSuppressionRow> AlertSuppressions => Set<AlertSuppressionRow>();

    public DbSet<AuditChainHeadRow> AuditChainHeads => Set<AuditChainHeadRow>();

    public DbSet<AuditPolicyRow> AuditPolicies => Set<AuditPolicyRow>();

    public DbSet<LegalHoldRow> LegalHolds => Set<LegalHoldRow>();

    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();

    public DbSet<WorkflowDefinitionRow> WorkflowDefinitions => Set<WorkflowDefinitionRow>();

    public DbSet<ConsumerFailureRow> ConsumerFailures => Set<ConsumerFailureRow>();

    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();

    public DbSet<SlaTimerRow> SlaTimers => Set<SlaTimerRow>();

    public DbSet<ApprovalStepRow> ApprovalSteps => Set<ApprovalStepRow>();

    public DbSet<ApprovalDelegationRow> ApprovalDelegations => Set<ApprovalDelegationRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("kernel");
        b.Entity<TenantRow>().ToTable("tenants").HasKey(x => x.Id);
        b.Entity<EnvironmentRow>().ToTable("environments").HasKey(x => x.Id);
        b.Entity<PrincipalRow>().ToTable("principals").HasKey(x => x.Id);
        b.Entity<MembershipRow>().ToTable("memberships").HasKey(x => new { x.TenantId, x.PrincipalId });
        b.Entity<GroupRow>().ToTable("groups").HasKey(x => new { x.TenantId, x.Id });
        b.Entity<GroupMemberRow>().ToTable("group_members").HasKey(x => x.Id);
        b.Entity<RoleAssignmentRow>().ToTable("role_assignments").HasKey(x => x.Id);
        b.Entity<InstallationRow>().ToTable("installations").HasKey(x => new { x.TenantId, x.EnvironmentId, x.ModuleId });
        b.Entity<ConfigArtifactRow>().ToTable("config_artifacts").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Revision });
        b.Entity<ActiveConfigRow>().ToTable("active_config").HasKey(x => new { x.TenantId, x.EnvironmentId });
        b.Entity<DraftRow>().ToTable("drafts").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<DraftCommandRow>().ToTable("draft_commands").HasKey(x => new { x.TenantId, x.EnvironmentId, x.DraftId, x.Seq });
        b.Entity<PublishRequestRow>().ToTable("publish_requests").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<RecordRow>().ToTable("records").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<WorkflowDefinitionRow>().ToTable("workflow_definitions").HasKey(x => new { x.TenantId, x.EnvironmentId, x.WorkflowId, x.Version });
        b.Entity<ConsumerFailureRow>().ToTable("consumer_failures").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<ApprovalRow>().ToTable("approvals").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<SlaTimerRow>().ToTable("sla_timers").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<ApprovalStepRow>().ToTable("approval_steps").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<ApprovalDelegationRow>().ToTable("approval_delegations").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<RecordSequenceRow>().ToTable("record_sequences").HasKey(x => new { x.TenantId, x.EnvironmentId, x.EntityId });
        b.Entity<ActivityRow>().ToTable("activity").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<IdempotencyRow>().ToTable("idempotency_keys").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Key });
        b.Entity<AuditRow>().ToTable("audit_log").HasKey(x => new { x.TenantId, x.Id });
        b.Entity<AuditRow>().Property(x => x.Id).ValueGeneratedOnAdd();
        b.Entity<OutboxRow>().ToTable("outbox").HasKey(x => x.Id);
        b.Entity<InboxRow>().ToTable("inbox").HasKey(x => new { x.Consumer, x.EventId });
        b.Entity<ProjectionRow>().ToTable("record_projections").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Consumer, x.RecordId });
        b.Entity<JobRow>().ToTable("jobs").HasKey(x => x.Id);
        b.Entity<MigrationRunRow>().ToTable("migration_runs").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<UpgradeRow>().ToTable("upgrades").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<NotificationRow>().ToTable("notifications").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<NotificationChannelRow>().ToTable("notification_channels").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<NotificationDeliveryRow>().ToTable("notification_deliveries").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<NotificationDeliveryAttemptRow>().ToTable("notification_delivery_attempts").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<NotificationPreferenceRow>().ToTable("notification_preferences").HasKey(x => new { x.TenantId, x.EnvironmentId, x.PrincipalId, x.RouteId, x.ChannelType });
        b.Entity<AlertRow>().ToTable("alerts").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<AlertActivityRow>().ToTable("alert_activity").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<AlertSuppressionRow>().ToTable("alert_suppressions").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<AuditChainHeadRow>().ToTable("audit_chain_heads").HasKey(x => x.TenantId);
        b.Entity<AuditPolicyRow>().ToTable("audit_policies").HasKey(x => x.TenantId);
        b.Entity<LegalHoldRow>().ToTable("legal_holds").HasKey(x => new { x.TenantId, x.Id });
        b.Entity<RefreshTokenRow>().ToTable("refresh_tokens").HasKey(x => x.Id);
        b.Entity<MailInboxRow>().ToTable("mail_inboxes").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<InboundMessageRow>().ToTable("inbound_messages").HasKey(x => new { x.TenantId, x.EnvironmentId, x.Id });
        b.Entity<AiSettingsRow>().ToTable("ai_settings").HasKey(x => x.TenantId);
        b.Entity<AiUsageRow>().ToTable("ai_usage").HasKey(x => new { x.TenantId, x.Id });

        string[] jsonColumns = ["Manifest", "Config", "Overlay", "Op", "Data", "Response", "Details", "Event", "State", "Payload", "Checkpoint", "Report", "Definition", "PolicySnapshot", "Requesters", "Snapshot", "Settings", "SecretRefs"];
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
                if (property.ClrType == typeof(string) && jsonColumns.Contains(property.Name) && !(entity.ClrType == typeof(MigrationRunRow) && property.Name == "Checkpoint"))
                {
                    property.SetColumnType("jsonb");
                }
            }
        }
    }

    private static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }
}
