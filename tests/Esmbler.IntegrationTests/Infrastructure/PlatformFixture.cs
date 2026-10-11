using Esmbler.Hosting;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Esmbler.IntegrationTests.Infrastructure;

/// <summary>Kernel services over real PostgreSQL with an in-memory relationship graph and the two demo tenants provisioned.</summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    private readonly PostgresFixture _pg = new();

    public IServiceProvider Services { get; private set; } = null!;

    public InMemoryRelationshipGraph Graph { get; } = new();

    public ProvisionedTenant Acme { get; private set; } = null!;

    public ProvisionedTenant Globex { get; private set; } = null!;

    public PostgresFixture Postgres => _pg;

    /// <summary>Slug of each tenant created with <see cref="NewTenantAsync"/>.</summary>
    public static Dictionary<Guid, string> Slugs { get; } = [];

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public RequestContext As(ProvisionedTenant tenant, string user, string env = "prod", Guid? agent = null, IReadOnlySet<string>? scopes = null) => new()
    {
        TenantId = tenant.TenantId,
        EnvironmentId = tenant.Environments[env],
        PrincipalId = tenant.Users[user],
        AgentId = agent,
        Scopes = scopes,
    };

    /// <summary>Provisions a fresh tenant with the demo shape so a test can change its configuration freely.</summary>
    public async Task<ProvisionedTenant> NewTenantAsync(string prefix, string ticketsVersion = "1.0.0")
    {
        var slug = $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";
        string Email(string user) => $"{user}@{slug}.example";

        // Approvers (ADR 0016) exist from Tickets 1.3.0; on older releases they are plain members.
        string[] Approver(params string[] roles) => Esmbler.Sdk.Modules.SemVer.Parse(ticketsVersion) >= Esmbler.Sdk.Modules.SemVer.Parse("1.3.0") ? [.. roles, "core.tickets.approver"] : roles;
        var tenant = await Get<ProvisioningService>().ProvisionAsync(new TenantSeed(
            slug,
            slug,
            [
                new UserSeed("admin", Email("admin"), "Admin " + slug, ProvisioningService.DemoPassword, [Kernel.Configuration.PlatformModule.TenantAdminRole], ["service_desk"]),
                new UserSeed("builder", Email("builder"), "Builder " + slug, ProvisioningService.DemoPassword, ["core.tickets.admin"], []),
                new UserSeed("agent", Email("agent"), "Agent " + slug, ProvisioningService.DemoPassword, [], ["service_desk", "network"]),
                new UserSeed("dana", Email("dana"), "Dana " + slug, ProvisioningService.DemoPassword, ["core.tickets.requester"], []),
                new UserSeed("jordan", Email("jordan"), "Jordan " + slug, ProvisioningService.DemoPassword, ["core.tickets.requester"], []),
                new UserSeed("riley", Email("riley"), "Riley " + slug, ProvisioningService.DemoPassword, ["core.tickets.requester"], []),
                new UserSeed("sam", Email("sam"), "Sam " + slug, ProvisioningService.DemoPassword, [], ["service_desk"]),
                new UserSeed("alex", Email("alex"), "Alex " + slug, ProvisioningService.DemoPassword, [], ["service_desk"]),
                new UserSeed("jamie", Email("jamie"), "Jamie " + slug, ProvisioningService.DemoPassword, Approver("core.tickets.requester"), []),
                new UserSeed("quinn", Email("quinn"), "Quinn " + slug, ProvisioningService.DemoPassword, Approver(), []),
            ],
            [new GroupSeed("service_desk", "Service Desk", ["core.tickets.agent"]), new GroupSeed("network", "Network", ["core.tickets.agent"])],
            [("core.tickets", ticketsVersion)]), default);
        Slugs[tenant.TenantId] = slug;
        return tenant;
    }

    public async Task InitializeAsync()
    {
        await _pg.InitializeAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Esmbler:Database:App"] = _pg.AppConnectionString,
            ["Esmbler:Database:Migrator"] = _pg.MigratorConnectionString,
            ["Esmbler:Auth:SigningKey"] = "integration-test-signing-key-0123456789abcdef",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddEsmblerPlatform(config);
        services.AddSingleton<IRelationshipGraph>(Graph);
        Services = services.BuildServiceProvider();
        await Get<DemoTenants>().EnsureAsync(default);
        Acme = await Reload("acme");
        Globex = await Reload("globex");
    }

    /// <summary>Provisioning returns IDs only once; tests look them up by email for repeatability.</summary>
    private async Task<ProvisionedTenant> Reload(string slug)
    {
        var identity = Get<Kernel.Infrastructure.Identity.IdentityService>();
        var alex = await identity.VerifyPasswordAsync("alex@acme.example", ProvisioningService.DemoPassword, default) ?? throw new InvalidOperationException("alex missing");
        var tenants = await identity.TenantsForAsync(alex.Id, default);
        var tenant = tenants.Single(t => t.Slug == slug);
        var ctx = new RequestContext { TenantId = tenant.TenantId, EnvironmentId = tenant.Environments.Single(e => e.Key == "prod").EnvironmentId, PrincipalId = alex.Id };
        var members = await identity.MembersAsync(ctx, default);
        var groups = await identity.GroupsAsync(ctx, default);
        return new ProvisionedTenant(
            tenant.TenantId,
            tenant.Environments.ToDictionary(e => e.Key, e => e.EnvironmentId),
            members.ToDictionary(m => m.Email!.Split('@')[0], m => m.Id),
            groups.ToDictionary(g => g.Key, g => g.Id));
    }

    public async Task DisposeAsync()
    {
        if (Services is IAsyncDisposable d)
        {
            await d.DisposeAsync();
        }

        await _pg.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PlatformCollection : ICollectionFixture<PlatformFixture>
{
    public const string Name = "platform";
}
