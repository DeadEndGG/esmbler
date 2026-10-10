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
        services.AddSingleton<IRecordWriteHook, AssignmentHook>();
        services.AddSingleton<IRecordWriteHook, RulesHook>();
        services.AddSingleton<RecordWriteEngine>();
        services.AddSingleton<RecordService>();
        services.AddSingleton<IdentityService>();
        services.AddSingleton<ProvisioningService>();
        services.AddSingleton<ExperienceService>();
        services.AddSingleton<AccessExplainer>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Events.RecordStateProjection>();
        services.AddSingleton<Kernel.Infrastructure.Events.IEventConsumer, Kernel.Infrastructure.Events.NotificationConsumer>();
        services.AddSingleton<Kernel.Infrastructure.Events.OutboxDispatcher>();
        services.AddSingleton<DraftService>();
        services.AddSingleton<PublishService>();
        services.AddSingleton<PreviewService>();
        services.AddSingleton<IMigrationFaultInjector>(_ => new ConfiguredFaultInjector(configuration["Esmbler:FaultInjection:Migration"]));
        services.AddSingleton<MigrationExecutor>();
        services.AddSingleton<UpgradeService>();
        services.AddSingleton<Kernel.Infrastructure.Events.JobRunner>();

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
