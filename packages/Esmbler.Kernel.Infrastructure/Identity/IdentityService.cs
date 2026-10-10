using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Identity;

public sealed record TenantChoice(Guid TenantId, string Slug, string Name, IReadOnlyList<EnvironmentChoice> Environments);

public sealed record EnvironmentChoice(Guid EnvironmentId, string Key, string Name, string Kind);

public sealed record MemberView(Guid Id, string DisplayName, string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups);

public sealed record GroupView(Guid Id, string Key, string Name, int Members);

/// <summary>
/// Identity: login, tenant switching, AI agent principals, groups, and role assignments (REQ-IAM-001/002, REQ-TEN-003).
/// The identity tables are the system of record; the relationship graph is updated in an order that never
/// over-grants: revocations reach the graph before the database commit, grants after it.
/// </summary>
public sealed class IdentityService(Database database, IRelationshipGraph graph, ConfigStore configs, AccessService access)
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashUser = new();

    public static string HashPassword(string password) => Hasher.HashPassword(HashUser, password);

    public async Task<(Guid Id, string Name)?> VerifyPasswordAsync(string email, string password, CancellationToken ct)
    {
        var found = await database.AnonymousAsync(async db =>
        {
            var rows = await db.Database.SqlQuery<LoginRow>($"SELECT id AS \"Id\", password_hash AS \"PasswordHash\", disabled AS \"Disabled\", display_name AS \"DisplayName\" FROM kernel.login_lookup({email})").ToListAsync(ct);
            return rows.FirstOrDefault();
        }, ct);

        if (found is null || found.Disabled || found.PasswordHash is null)
        {
            Hasher.HashPassword(HashUser, password); // equalize timing for unknown users
            return null;
        }

        return Hasher.VerifyHashedPassword(HashUser, found.PasswordHash, password) == PasswordVerificationResult.Failed ? null : (found.Id, found.DisplayName);
    }

    public Task<string> DisplayNameAsync(Guid principalId, CancellationToken ct) =>
        database.AsPrincipalAsync(principalId, db => db.Principals.Where(p => p.Id == principalId).Select(p => p.DisplayName).FirstAsync(ct), ct);

    public Task<IReadOnlyList<TenantChoice>> TenantsForAsync(Guid principalId, CancellationToken ct) =>
        database.AsPrincipalAsync<IReadOnlyList<TenantChoice>>(principalId, async db =>
        {
            var tenantIds = await db.Memberships.Where(m => m.PrincipalId == principalId).Select(m => m.TenantId).ToListAsync(ct);
            var tenants = await db.Tenants.Where(t => tenantIds.Contains(t.Id) && t.Status == "active").OrderBy(t => t.Name).ToListAsync(ct);
            var envs = await db.Environments.Where(e => tenantIds.Contains(e.TenantId)).ToListAsync(ct);
            return tenants.Select(t => new TenantChoice(t.Id, t.Slug, t.Name, envs.Where(e => e.TenantId == t.Id).OrderBy(e => e.Kind == "production" ? 0 : 1).Select(e => new EnvironmentChoice(e.Id, e.Key, e.Name, e.Kind)).ToList())).ToList();
        }, ct);

    /// <summary>Explicit, authorized switch (REQ-TEN-003): the principal must be a member of the tenant.</summary>
    public async Task<(TenantChoice Tenant, EnvironmentChoice Environment)> ResolveSwitchAsync(Guid principalId, string tenantSlug, string? environmentKey, CancellationToken ct)
    {
        var tenants = await TenantsForAsync(principalId, ct);
        var tenant = tenants.FirstOrDefault(t => t.Slug == tenantSlug) ?? throw KernelException.Forbidden($"You are not a member of '{tenantSlug}'.");
        var env = environmentKey is null ? tenant.Environments[0] : tenant.Environments.FirstOrDefault(e => e.Key == environmentKey)
            ?? throw KernelException.NotFound($"Environment '{environmentKey}'");
        return (tenant, env);
    }

    /// <summary>The user's builder agent in a tenant (created on first use). Agents are auditable principals.</summary>
    public Task<Guid> EnsureAgentAsync(Guid principalId, Guid tenantId, CancellationToken ct) =>
        database.InTenantWideAsync(tenantId, principalId, async db =>
        {
            var existing = await db.Principals.FirstOrDefaultAsync(p => p.Kind == "agent" && p.ActsFor == principalId && p.AgentTenant == tenantId, ct);
            if (existing is not null)
            {
                if (existing.Disabled)
                {
                    throw KernelException.Forbidden("Your AI agent is disabled in this workspace.");
                }

                return existing.Id;
            }

            var user = await db.Principals.FirstAsync(p => p.Id == principalId, ct);
            var agent = new PrincipalRow
            {
                Id = Guid.NewGuid(),
                Kind = "agent",
                DisplayName = $"AI builder for {user.DisplayName}",
                ActsFor = principalId,
                AgentTenant = tenantId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Principals.Add(agent);
            await db.SaveChangesAsync(ct);
            return agent.Id;
        }, ct);

    public Task SetAgentDisabledAsync(RequestContext ctx, Guid agentId, bool disabled, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var agent = await db.Principals.FirstOrDefaultAsync(p => p.Id == agentId && p.Kind == "agent", ct) ?? throw KernelException.NotFound("Agent");
            if (agent.ActsFor != ctx.PrincipalId)
            {
                await RequireActionAsync(db, ctx, PlatformActions.PermissionsAdmin, ct);
            }

            await db.Principals.Where(p => p.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Disabled, disabled), ct);
            AuditWriter.Add(db, ctx, disabled ? "agent.disable" : "agent.enable", "principal", agentId.ToString(), null, environmentScoped: false);
            await db.SaveChangesAsync(ct);
        }, ct);

    public Task<IReadOnlyList<MemberView>> MembersAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<MemberView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var members = await (from m in db.Memberships where m.TenantId == ctx.TenantId join p in db.Principals on m.PrincipalId equals p.Id select new { p.Id, p.DisplayName, p.Email }).OrderBy(x => x.DisplayName).ToListAsync(ct);
            var roles = await db.RoleAssignments.Where(r => r.PrincipalId != null).ToListAsync(ct);
            var groups = await db.Groups.ToDictionaryAsync(g => g.Id, g => g.Name, ct);
            var memberOf = await db.GroupMembers.Where(g => g.PrincipalId != null).ToListAsync(ct);
            return members.Select(m => new MemberView(
                m.Id,
                m.DisplayName,
                m.Email,
                roles.Where(r => r.PrincipalId == m.Id).Select(r => r.RoleId).ToList(),
                memberOf.Where(g => g.PrincipalId == m.Id).Select(g => groups.GetValueOrDefault(g.GroupId, "?")).ToList())).ToList();
        }, ct);

    public Task<IReadOnlyList<GroupView>> GroupsAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<GroupView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var members = await db.GroupMembers.GroupBy(g => g.GroupId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            return (await db.Groups.OrderBy(g => g.Name).ToListAsync(ct)).Select(g => new GroupView(g.Id, g.Key, g.Name, members.GetValueOrDefault(g.Id))).ToList();
        }, ct);

    public async Task AssignRoleAsync(RequestContext ctx, string roleId, Guid? principalId, Guid? groupId, CancellationToken ct)
    {
        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireActionAsync(db, ctx, PlatformActions.PermissionsAdmin, ct);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            if (!artifact.Config.Roles.Exists(r => r.Id == roleId))
            {
                throw KernelException.NotFound($"Role '{roleId}'");
            }

            db.RoleAssignments.Add(new RoleAssignmentRow { TenantId = ctx.TenantId, RoleId = roleId, PrincipalId = principalId, GroupId = groupId, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = ctx.PrincipalId });
            AuditWriter.Add(db, ctx, "role.assign", "role", roleId, artifact.Revision, new { principalId, groupId }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
        }, ct);
        await graph.WriteAsync(ctx.TenantId, [RoleTuple(roleId, principalId, groupId)], [], ct);
    }

    public async Task RevokeRoleAsync(RequestContext ctx, string roleId, Guid? principalId, Guid? groupId, CancellationToken ct)
    {
        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireActionAsync(db, ctx, PlatformActions.PermissionsAdmin, ct);
            await graph.WriteAsync(ctx.TenantId, [], [RoleTuple(roleId, principalId, groupId)], ct);
            await db.RoleAssignments.Where(r => r.RoleId == roleId && r.PrincipalId == principalId && r.GroupId == groupId).ExecuteDeleteAsync(ct);
            AuditWriter.Add(db, ctx, "role.revoke", "role", roleId, null, new { principalId, groupId }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task AddGroupMemberAsync(RequestContext ctx, Guid groupId, Guid principalId, CancellationToken ct)
    {
        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireActionAsync(db, ctx, PlatformActions.PermissionsAdmin, ct);
            db.GroupMembers.Add(new GroupMemberRow { TenantId = ctx.TenantId, GroupId = groupId, PrincipalId = principalId, CreatedAt = DateTimeOffset.UtcNow });
            AuditWriter.Add(db, ctx, "group.add_member", "group", groupId.ToString(), null, new { principalId }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
        }, ct);
        await graph.WriteAsync(ctx.TenantId, [RelationshipTuple.GroupMember(principalId, groupId)], [], ct);
    }

    public async Task RemoveGroupMemberAsync(RequestContext ctx, Guid groupId, Guid principalId, CancellationToken ct)
    {
        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireActionAsync(db, ctx, PlatformActions.PermissionsAdmin, ct);
            // Revoke in the graph first: if the commit then fails, access is lost (safe), never kept.
            await graph.WriteAsync(ctx.TenantId, [], [RelationshipTuple.GroupMember(principalId, groupId)], ct);
            await db.GroupMembers.Where(g => g.GroupId == groupId && g.PrincipalId == principalId).ExecuteDeleteAsync(ct);
            AuditWriter.Add(db, ctx, "group.remove_member", "group", groupId.ToString(), null, new { principalId }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>Rebuilds the tenant's graph from the identity tables (repair after drift or restore).</summary>
    public async Task<int> RebuildGraphAsync(Guid tenantId, CancellationToken ct)
    {
        var tuples = await database.InTenantWideAsync(tenantId, null, async db =>
        {
            var list = new List<RelationshipTuple>();
            list.AddRange((await db.GroupMembers.ToListAsync(ct)).Select(m => m.PrincipalId is { } p ? RelationshipTuple.GroupMember(p, m.GroupId) : RelationshipTuple.NestedGroup(m.MemberGroup!.Value, m.GroupId)));
            list.AddRange((await db.RoleAssignments.ToListAsync(ct)).Select(r => RoleTuple(r.RoleId, r.PrincipalId, r.GroupId)));
            return list;
        }, ct);
        await graph.EnsureTenantAsync(tenantId, ct);
        await graph.WriteAsync(tenantId, tuples, [], ct);
        return tuples.Count;
    }

    private async Task RequireActionAsync(KernelDbContext db, RequestContext ctx, string action, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
        if (!perms.Can(action))
        {
            throw KernelException.Forbidden("Changing people, groups, and roles requires the permission administration right.", suggestion: "Ask a tenant administrator.");
        }
    }

    private static RelationshipTuple RoleTuple(string roleId, Guid? principalId, Guid? groupId) =>
        principalId is { } p ? RelationshipTuple.UserRole(p, roleId) : RelationshipTuple.GroupRole(groupId!.Value, roleId);

    private sealed record LoginRow(Guid Id, string? PasswordHash, bool Disabled, string DisplayName);
}
