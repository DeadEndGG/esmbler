using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Authorization;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Modules.Tickets;
using Esmbler.Platform.Assignment;
using Esmbler.Platform.Rules;
using Esmbler.Platform.Workflow;
using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Esmbler.Hosting;

public static class ServiceRegistration
{
    /// <summary>The OAuth authorization server for external MCP clients (ADR 0025): the API host only.</summary>
    public static IServiceCollection AddEsmblerOAuthServer(this IServiceCollection services)
    {
        services.AddSingleton<Auth.IClientMetadataSource, Auth.HttpClientMetadataSource>();
        services.AddSingleton<Auth.OAuthService>();
        return services;
    }

    /// <summary>Registers the kernel, platform services, and installed modules for any host.</summary>
    public static IServiceCollection AddEsmblerPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.Section));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PlatformOptions>>().Value);
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<PlatformOptions>();
            if (string.IsNullOrWhiteSpace(options.Database.App))
            {
                throw new InvalidOperationException("Esmbler:Database:App (connection string for esmbler_app) is not configured.");
            }

            return new NpgsqlDataSourceBuilder(options.Database.App).Build();
        });
        services.AddSingleton<Database>();
        services.AddSingleton<MigrationRunner>();

        services.AddSingleton<IModuleDefinition, TicketsModule>();
        services.AddSingleton<ModuleCatalog>();
        services.AddSingleton<IElementRegistry>(_ => ElementRegistry.LoadBuiltIn());
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<IPropsValidator, JsonSchemaPropsValidator>();
        services.AddSingleton<ConfigValidator>();
        services.AddSingleton<ConfigCompiler>();
        services.AddSingleton<ConfigActivator>();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRichTextSanitizer, RichTextSanitizer>();
        services.AddSingleton<FieldNormalizer>();
        services.AddSingleton<IRecordWriteHook, WorkflowHook>();
        services.AddSingleton<IRecordWriteHook, Platform.Requests.RequestHook>();
        services.AddSingleton<IRecordWriteHook, AssignmentHook>();
        services.AddSingleton<IRecordWriteHook, RulesHook>();
        services.AddSingleton<RecordWriteEngine>();
        services.AddSingleton<RecordService>();
        services.AddSingleton<IdentityService>();
        services.AddSingleton<ProvisioningService>();
        services.AddSingleton(sp => { var o = sp.GetRequiredService<PlatformOptions>(); return new WorkspaceCreationPolicy(o.AllowWorkspaceCreation, o.MaxWorkspacesPerPerson); });
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<DemoTenants>();
        services.AddSingleton<ExperienceService>();
        services.AddSingleton<AccessExplainer>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Events.RecordStateProjection>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Events.NotificationConsumer>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Events.FulfillmentConsumer>();
        services.AddSingleton<Kernel.Infrastructure.Approvals.ApprovalService>();
        services.AddSingleton<Kernel.Infrastructure.Sla.SlaService>();
        services.AddSingleton<Kernel.Infrastructure.Metrics.MetricService>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Approvals.ApprovalConsumer>();
        services.AddSingleton<Kernel.Infrastructure.Events.OutboxDispatcher>();
        services.AddSingleton<DraftService>();
        services.TryAddSingleton<IActivationHealthCheck, ActivationHealthCheck>();
        services.AddSingleton<PublishService>();
        services.AddSingleton<PreviewService>();
        services.AddSingleton<IMigrationFaultInjector>(_ => new ConfiguredFaultInjector(configuration["Esmbler:FaultInjection:Migration"]));
        services.AddSingleton<MigrationExecutor>();
        services.AddSingleton<UpgradeService>();
        services.AddSingleton<Kernel.Infrastructure.Configuration.WorkflowVersions>();
        services.AddSingleton<Kernel.Infrastructure.Events.JobRunner>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Events.UpgradeJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Events.WorkflowMigrationJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Approvals.ApprovalExpiryJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Approvals.ApprovalSweepJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Approvals.ApprovalDelegationJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Sla.SlaCheckJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.OperationsService>();

        // Notifications (ADR 0021): subscription router, resolve and delivery jobs, channel adapters, secret references.
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<PlatformOptions>().Notifications;
            return new Kernel.Infrastructure.Notifications.NotificationOptions
            {
                AllowedEmailHosts = o.AllowedEmailHosts,
                AllowedSmsHosts = o.AllowedSmsHosts,
                AllowedImapHosts = o.AllowedImapHosts,
                MaxRecipientsPerEvent = o.MaxRecipientsPerEvent,
                WebUrl = configuration["Esmbler:WebUrl"] ?? "http://localhost:8080",
            };
        });
        services.AddSingleton<Kernel.Infrastructure.Notifications.ISecretStore, Kernel.Infrastructure.Notifications.EnvironmentSecretStore>();

        // AI providers (ADR 0024): the operator's model catalog and named credentials, per-workspace settings and metering.
        services.AddSingleton(_ => AiCatalogFactory.From(configuration));
        services.AddSingleton<Kernel.Infrastructure.Assistant.AiSettingsService>();

        // OAuth for external MCP clients (ADR 0025): every host may check a connection; only the API runs the
        // authorization server (AddEsmblerOAuthServer).
        services.AddSingleton<Auth.McpConnectionStatus>();

        // Email intake (ADR 0023): mailboxes, inbound messages, the processing job and the IMAP poller (worker only).
        services.AddSingleton<Kernel.Infrastructure.Mail.MailIntakeService>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Mail.MailProcessJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Mail.ImapPoller>();
        // No redirects: the allow-listed host is the only one the SMS adapter ever talks to (T-54, T-73).
        services.AddHttpClient("notifications.sms").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<Sdk.Notifications.INotificationChannel, Kernel.Infrastructure.Notifications.Channels.SmtpEmailChannel>();
        services.AddSingleton<Sdk.Notifications.INotificationChannel, Kernel.Infrastructure.Notifications.Channels.HttpSmsChannel>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Notifications.SubscriptionRouter>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Notifications.NotifyResolveJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Notifications.NotifyDeliverJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Notifications.NotificationService>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Alerts.AlertRouter>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Alerts.AlertEvaluateJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Alerts.AlertEscalateJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Events.IJobHandler, Kernel.Infrastructure.Alerts.AlertUnsuppressJobHandler>();
        services.AddSingleton<Kernel.Infrastructure.Alerts.AlertService>();
        services.AddSingleton<Kernel.Infrastructure.Services.AuditService>();
        services.AddSingleton<Kernel.Infrastructure.Services.AuditMaintenance>();

        services.AddSingleton<IAuthzStoreRegistry, AuthzStoreRegistry>();
        services.AddHttpClient(nameof(OpenFgaRelationshipGraph));
        services.AddSingleton<IRelationshipGraph>(sp =>
        {
            var o = sp.GetRequiredService<PlatformOptions>().OpenFga;
            return new OpenFgaRelationshipGraph(
                new OpenFgaOptions { ApiUrl = o.ApiUrl, TimeoutMs = o.TimeoutMs },
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenFgaRelationshipGraph)),
                sp.GetRequiredService<IAuthzStoreRegistry>(),
                sp.GetRequiredService<ILogger<OpenFgaRelationshipGraph>>());
        });
        services.AddSingleton<AccessService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuthzFactsCache>();
        return services;
    }
}
