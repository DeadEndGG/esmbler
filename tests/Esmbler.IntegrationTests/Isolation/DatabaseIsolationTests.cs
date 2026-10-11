using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Esmbler.IntegrationTests.Isolation;

/// <summary>REQ-TEN-001: database-level isolation holds for the runtime role regardless of application code.</summary>
[Collection(PostgresCollection.Name)]
public class DatabaseIsolationTests(PostgresFixture pg)
{
    private async Task<(Guid Tenant, Guid Env)> NewTenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        var env = Guid.NewGuid();
        await pg.Database.AnonymousAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT kernel.provision_tenant({tenant}, {slug}, {slug})");
            return true;
        }, default);
        await pg.Database.InTenantWideAsync(tenant, null, async db =>
        {
            db.Environments.Add(new EnvironmentRow { Id = env, TenantId = tenant, Key = "prod", Name = "Production", Kind = "production", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return true;
        }, default);
        return (tenant, env);
    }

    private static RecordRow Record(Guid tenant, Guid env, string title) => new()
    {
        TenantId = tenant,
        EnvironmentId = env,
        Id = Guid.NewGuid(),
        EntityId = "core.tickets.ticket",
        Revision = 1,
        Data = $"{{\"title\":\"{title}\"}}",
        ConfigRevision = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CreatedBy = Guid.Empty,
        UpdatedBy = Guid.Empty,
    };

    [Fact]
    public async Task Runtime_role_sees_only_the_current_tenant_environment()
    {
        var a = await NewTenantAsync("iso-a-" + Guid.NewGuid().ToString("N")[..6]);
        var b = await NewTenantAsync("iso-b-" + Guid.NewGuid().ToString("N")[..6]);
        await pg.Database.InTenantAsync(a.Tenant, a.Env, null, async db =>
        {
            db.Records.Add(Record(a.Tenant, a.Env, "A secret"));
            await db.SaveChangesAsync();
        }, default);
        await pg.Database.InTenantAsync(b.Tenant, b.Env, null, async db =>
        {
            db.Records.Add(Record(b.Tenant, b.Env, "B secret"));
            await db.SaveChangesAsync();
        }, default);

        var seenByA = await pg.Database.InTenantAsync(a.Tenant, a.Env, null, db => db.Records.Select(r => r.Data).ToListAsync(), default);
        Assert.All(seenByA, d => Assert.Contains("A secret", d));
        Assert.Single(seenByA);

        var seenWithoutContext = await pg.Database.AnonymousAsync(db => db.Records.CountAsync(), default);
        Assert.Equal(0, seenWithoutContext);
    }

    [Fact]
    public async Task Writing_another_tenants_row_is_rejected_by_policy()
    {
        var a = await NewTenantAsync("iso-c-" + Guid.NewGuid().ToString("N")[..6]);
        var b = await NewTenantAsync("iso-d-" + Guid.NewGuid().ToString("N")[..6]);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => pg.Database.InTenantAsync(a.Tenant, a.Env, null, async db =>
        {
            db.Records.Add(Record(b.Tenant, b.Env, "smuggled"));
            await db.SaveChangesAsync();
        }, default));
        Assert.Contains("row-level security", ex.ToString());
    }

    [Fact]
    public async Task Runtime_role_is_not_privileged_and_cannot_rewrite_audit()
    {
        await using var conn = new NpgsqlConnection(pg.AppConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }

        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'kernel' AND tableowner = current_user", conn))
        {
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'kernel' AND c.relkind = 'r' AND NOT (c.relrowsecurity AND c.relforcerowsecurity)", conn))
        {
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        await using var update = new NpgsqlCommand("UPDATE kernel.audit_log SET outcome = 'success'", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Only_allow_listed_security_definer_functions_exist()
    {
        string[] allowed = ["activate_workspace", "audit_verification_due", "bump_authz_revision", "claim_jobs", "claim_outbox", "create_workspace", "find_tenant_by_slug", "login_lookup", "mail_inbox_by_token", "mail_inboxes_due", "oauth_code_lookup", "oauth_refresh_lookup", "provision_tenant", "purge_inbound_mail", "purge_outbox", "purge_retention", "refresh_lookup", "seal_audit", "verify_audit_chain"];
        await using var conn = new NpgsqlConnection(pg.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT p.proname, r.rolname, coalesce(array_to_string(p.proconfig, ','), '')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_roles r ON r.oid = p.proowner
            WHERE n.nspname = 'kernel' AND p.prosecdef ORDER BY p.proname
            """, conn);
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            found.Add(reader.GetString(0));
            Assert.Equal("esmbler_definer", reader.GetString(1));
            Assert.Contains("search_path=kernel, pg_temp", reader.GetString(2));
        }

        Assert.Equal(allowed, found);
    }

    [Fact]
    public async Task Tenant_context_does_not_leak_across_pooled_connections()
    {
        var a = await NewTenantAsync("iso-e-" + Guid.NewGuid().ToString("N")[..6]);
        await pg.Database.InTenantAsync(a.Tenant, a.Env, null, async db =>
        {
            db.Records.Add(Record(a.Tenant, a.Env, "pooled"));
            await db.SaveChangesAsync();
        }, default);
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(0, await pg.Database.AnonymousAsync(db => db.Records.CountAsync(), default));
        }
    }
}
