using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Services;

public sealed record UserSeed(string Key, string Email, string DisplayName, string Password, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups);

public sealed record GroupSeed(string Key, string Name, IReadOnlyList<string> Roles);

public sealed record TenantSeed(string Slug, string Name, IReadOnlyList<UserSeed> Users, IReadOnlyList<GroupSeed> Groups, IReadOnlyList<(string ModuleId, string Version)> Modules, bool SampleData = true);

public sealed record ProvisionedTenant(Guid TenantId, IReadOnlyDictionary<string, Guid> Environments, IReadOnlyDictionary<string, Guid> Users, IReadOnlyDictionary<string, Guid> Groups);

/// <summary>
/// Control-plane provisioning: tenant, production and sandbox environments, identity, relationship store, module
/// installation (artifact revision 1), and synthetic sample data. Sandbox gets synthetic data only (REQ-TEN-005).
/// </summary>
public sealed class ProvisioningService(Database database, IRelationshipGraph graph, ConfigActivator activator, ModuleCatalog catalog, ConfigStore configs, RecordService records, ILogger<ProvisioningService> logger)
{
    public const string DemoPassword = "Esmbler!2026";

    public async Task<ProvisionedTenant> ProvisionAsync(TenantSeed seed, CancellationToken ct)
    {
        var tenantId = Guid.NewGuid();
        await database.AnonymousAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"SELECT kernel.provision_tenant({tenantId}, {seed.Slug}, {seed.Name})", ct), ct);

        var envs = new Dictionary<string, Guid> { ["prod"] = Guid.NewGuid(), ["sandbox"] = Guid.NewGuid() };
        var users = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var groups = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var preexisting = new HashSet<Guid>();
        foreach (var u in seed.Users)
        {
            // A person may belong to several tenants (REQ-IAM-002): reuse the global principal when the email exists.
            var existing = await database.AnonymousAsync(db => db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM kernel.login_lookup({u.Email})").ToListAsync(ct), ct);
            users[u.Key] = existing.Count > 0 ? existing[0] : Guid.NewGuid();
            if (existing.Count > 0)
            {
                preexisting.Add(existing[0]);
            }
        }

        await database.InTenantWideAsync(tenantId, null, async db =>
        {
            db.Environments.Add(new EnvironmentRow { Id = envs["prod"], TenantId = tenantId, Key = "prod", Name = "Production", Kind = "production", CreatedAt = DateTimeOffset.UtcNow });
            db.Environments.Add(new EnvironmentRow { Id = envs["sandbox"], TenantId = tenantId, Key = "sandbox", Name = "Sandbox", Kind = "sandbox", CreatedAt = DateTimeOffset.UtcNow });
            foreach (var g in seed.Groups)
            {
                groups[g.Key] = Guid.NewGuid();
                db.Groups.Add(new GroupRow { TenantId = tenantId, Id = groups[g.Key], Key = g.Key, Name = g.Name, CreatedAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync(ct);
            foreach (var u in seed.Users)
            {
                var id = users[u.Key];
                if (!preexisting.Contains(id))
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO kernel.principals (id, kind, email, display_name, password_hash) VALUES ({id}, 'user', {u.Email}, {u.DisplayName}, {IdentityService.HashPassword(u.Password)})", ct);
                }

                db.Memberships.Add(new MembershipRow { TenantId = tenantId, PrincipalId = id, CreatedAt = DateTimeOffset.UtcNow });
                foreach (var role in u.Roles)
                {
                    db.RoleAssignments.Add(new RoleAssignmentRow { TenantId = tenantId, RoleId = role, PrincipalId = id, CreatedAt = DateTimeOffset.UtcNow });
                }

                foreach (var g in u.Groups)
                {
                    db.GroupMembers.Add(new GroupMemberRow { TenantId = tenantId, GroupId = groups[g], PrincipalId = id, CreatedAt = DateTimeOffset.UtcNow });
                }
            }

            foreach (var g in seed.Groups)
            {
                foreach (var role in g.Roles)
                {
                    db.RoleAssignments.Add(new RoleAssignmentRow { TenantId = tenantId, RoleId = role, GroupId = groups[g.Key], CreatedAt = DateTimeOffset.UtcNow });
                }
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        var tuples = seed.Users.SelectMany(u => u.Roles.Select(r => RelationshipTuple.UserRole(users[u.Key], r)).Concat(u.Groups.Select(g => RelationshipTuple.GroupMember(users[u.Key], groups[g]))))
            .Concat(seed.Groups.SelectMany(g => g.Roles.Select(r => RelationshipTuple.GroupRole(groups[g.Key], r))))
            .ToList();
        await graph.EnsureTenantAsync(tenantId, ct);
        await graph.WriteAsync(tenantId, tuples, [], ct);

        var system = seed.Users.FirstOrDefault(u => u.Roles.Contains(PlatformModule.TenantAdminRole)) ?? seed.Users[0];
        foreach (var (_, envId) in envs)
        {
            await InstallAsync(tenantId, envId, users[system.Key], seed.Modules, ct);
            if (seed.SampleData)
            {
                await SeedSampleDataAsync(tenantId, envId, users[system.Key], users, groups, ct);
            }
        }

        logger.LogInformation("Provisioned tenant {Slug} ({Tenant})", seed.Slug, tenantId);
        return new ProvisionedTenant(tenantId, envs, users, groups);
    }

    public Task InstallAsync(Guid tenantId, Guid envId, Guid actor, IReadOnlyList<(string ModuleId, string Version)> modules, CancellationToken ct) =>
        database.InTenantAsync(tenantId, envId, actor, async db =>
        {
            var all = modules.Prepend((PlatformModule.ModuleId, catalog.Latest(PlatformModule.ModuleId).Version)).DistinctBy(m => m.Item1).ToList();
            var result = activator.Compile(all, []);
            foreach (var (moduleId, version) in all)
            {
                db.Installations.Add(new InstallationRow { TenantId = tenantId, EnvironmentId = envId, ModuleId = moduleId, Version = version, InstalledAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync(ct);
            await activator.ActivateAsync(db, tenantId, envId, result, [], "install", null, actor, actor, ct);
        }, ct);

    private Task SeedSampleDataAsync(Guid tenantId, Guid envId, Guid actor, Dictionary<string, Guid> users, Dictionary<string, Guid> groups, CancellationToken ct) =>
        database.InTenantAsync(tenantId, envId, actor, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, tenantId, envId, ct);
            var ctx = new RequestContext { TenantId = tenantId, EnvironmentId = envId, PrincipalId = actor, Channel = "system" };
            var created = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var installed = await db.Installations.Select(i => new { i.ModuleId, i.Version }).ToListAsync(ct);
            foreach (var sample in installed.SelectMany(i => catalog.Release(i.ModuleId, i.Version).SampleData))
            {
                var entity = artifact.Config.FindEntity(sample.Entity);
                if (entity is null)
                {
                    continue;
                }

                var data = Resolve(sample.Data, users, groups, created);
                var id = await records.SystemCreateAsync(db, ctx, artifact, entity, data, ct);
                created[sample.Key] = id;
                foreach (var act in sample.Activity)
                {
                    var author = act.Author is { } a && a.StartsWith("@user:", StringComparison.Ordinal) && users.TryGetValue(a[6..], out var au) ? au : actor;
                    db.Activity.Add(new ActivityRow { TenantId = tenantId, EnvironmentId = envId, Id = Guid.NewGuid(), RecordId = id, Kind = act.Kind, Body = act.Body, Author = author, CreatedAt = DateTimeOffset.UtcNow });
                }
            }

            await db.SaveChangesAsync(ct);
        }, ct);

    private static JsonObject Resolve(JsonObject data, Dictionary<string, Guid> users, Dictionary<string, Guid> groups, Dictionary<string, Guid> records)
    {
        var result = new JsonObject();
        foreach (var (key, value) in data)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('@'))
            {
                var parts = s[1..].Split(':', 2);
                Guid? id = parts[0] switch
                {
                    "user" => users.TryGetValue(parts[1], out var u) ? u : null,
                    "group" => groups.TryGetValue(parts[1], out var g) ? g : null,
                    "record" => records.TryGetValue(parts[1], out var r) ? r : null,
                    _ => null,
                };
                if (id is not null)
                {
                    result[key] = id.ToString();
                }
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>Two demo tenants (REQ-ACC-01 fixture). Alex belongs to both to show explicit tenant switching.</summary>
    public async Task<bool> EnsureDemoTenantsAsync(CancellationToken ct)
    {
        var acme = await database.AnonymousAsync(db => db.Database.SqlQuery<Guid?>($"SELECT kernel.find_tenant_by_slug('acme') AS \"Value\"").FirstOrDefaultAsync(ct), ct);
        if (acme is not null)
        {
            return false;
        }

        const string Admin = PlatformModule.TenantAdminRole;
        const string Agent = "core.tickets.agent";
        const string Requester = "core.tickets.requester";
        // Acme starts on Tickets 1.0.0 to demonstrate upgrades; Globex runs the newest release (service requests).
        (string, string)[] tickets = [("core.tickets", "1.0.0")];
        (string, string)[] latest = [("core.tickets", catalog.Latest("core.tickets").Version)];
        await ProvisionAsync(new TenantSeed(
            "acme",
            "Acme Corp",
            [
                new UserSeed("alex", "alex@acme.example", "Alex Rivera", DemoPassword, [Admin], ["service_desk"]),
                new UserSeed("sam", "sam@acme.example", "Sam Okafor", DemoPassword, [], ["service_desk", "network"]),
                new UserSeed("dana", "dana@acme.example", "Dana Kim", DemoPassword, [Requester], []),
                new UserSeed("jordan", "jordan@acme.example", "Jordan Lee", DemoPassword, [Requester], []),
                new UserSeed("riley", "riley@acme.example", "Riley Chen", DemoPassword, [Requester], []),
            ],
            [new GroupSeed("service_desk", "Service Desk", [Agent]), new GroupSeed("network", "Network", [Agent])],
            tickets), ct);
        await ProvisionAsync(new TenantSeed(
            "globex",
            "Globex Inc",
            [
                new UserSeed("alex", "alex@acme.example", "Alex Rivera", DemoPassword, [Admin], []),
                new UserSeed("morgan", "morgan@globex.example", "Morgan Diaz", DemoPassword, [Admin], ["service_desk"]),
                new UserSeed("sam", "casey@globex.example", "Casey Novak", DemoPassword, [], ["network"]),
                new UserSeed("dana", "taylor@globex.example", "Taylor Brooks", DemoPassword, [Requester], []),
                new UserSeed("jordan", "jamie@globex.example", "Jamie Ortiz", DemoPassword, [Requester], []),
                new UserSeed("riley", "quinn@globex.example", "Quinn Patel", DemoPassword, [Requester], []),
            ],
            [new GroupSeed("service_desk", "Service Desk", [Agent]), new GroupSeed("network", "Network", [Agent])],
            latest), ct);
        return true;
    }
}
