using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>REQ-CFG-010: a revision that fails its post-activation health check never goes live; the previous one stays.</summary>
[Collection(PlatformCollection.Name)]
public class ActivationHealthTests(PlatformFixture f)
{
    private sealed class Unhealthy : IActivationHealthCheck
    {
        public Task<IReadOnlyList<string>> CheckAsync(KernelDbContext db, Guid tenantId, Guid environmentId, int revision, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["Ticket record 42: a rule fails to evaluate (injected)."]);
    }

    [Fact]
    public async Task Unhealthy_activation_is_rolled_back_and_the_previous_revision_stays_live()
    {
        var t = await f.NewTenantAsync("ah");
        var env = t.Environments["prod"];
        var database = f.Get<Database>();
        var configs = f.Get<ConfigStore>();
        var catalog = f.Get<ModuleCatalog>();
        var before = await database.InTenantAsync(t.TenantId, env, null, db => configs.GetActiveAsync(db, t.TenantId, env, default), default);
        var candidate = f.Get<ConfigCompiler>().Compile(before.Lockfile.Select(m => catalog.Release(m.Id, m.Version)).ToList(), before.Overlay, before.Config);

        var activator = new ConfigActivator(f.Get<ConfigCompiler>(), catalog, new Unhealthy());
        var ex = await Assert.ThrowsAsync<KernelException>(() => database.InTenantAsync(t.TenantId, env, null, db =>
            activator.ActivateAsync(db, t.TenantId, env, candidate, before.Overlay, "publish", before.Revision, null, null, default), default));
        Assert.Equal("activation_unhealthy", ex.Code);
        Assert.Contains("previous revision stays live", ex.Message, StringComparison.Ordinal);

        var after = await database.InTenantAsync(t.TenantId, env, null, db => configs.GetActiveAsync(db, t.TenantId, env, default), default);
        Assert.Equal(before.Revision, after.Revision);
        var revisions = await f.Get<PublishService>().RevisionsAsync(f.As(t, "admin"), default);
        Assert.DoesNotContain(revisions, r => r.Revision > before.Revision);
    }

    [Fact]
    public async Task Healthy_check_passes_against_live_records()
    {
        var t = await f.NewTenantAsync("ak");
        var env = t.Environments["prod"];
        var problems = await f.Get<Database>().InTenantAsync(t.TenantId, env, null, async db =>
        {
            var active = await f.Get<ConfigStore>().GetActiveAsync(db, t.TenantId, env, default);
            return await new ActivationHealthCheck().CheckAsync(db, t.TenantId, env, active.Revision, default);
        }, default);
        Assert.Empty(problems);
    }
}
