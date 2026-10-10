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
        var provisioning = Get<ProvisioningService>();
        await provisioning.EnsureDemoTenantsAsync(default);
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
