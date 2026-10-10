using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Identity;

/// <summary>
/// Builds the effective permissions for one request: relationship facts from the graph (decision source), with
/// role provenance from the identity tables (explanations only), evaluated against the pinned configuration.
/// Agents must be enabled and act for the calling user (REQ-AUTHZ-007).
/// </summary>
public sealed class AccessService(IRelationshipGraph graph, AuthzFactsCache cache)
{
    /// <param name="fresh">Bypass the facts cache. Sensitive actions (publish, rollback, permission and module
    /// administration) always read the graph so a revocation applies to them immediately (REQ-AUTHZ-006).</param>
    public async Task<EffectivePermissions> ForAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, CancellationToken cancellationToken, bool fresh = false)
    {
        if (ctx.AgentId is { } agentId)
        {
            var agent = await db.Principals.FirstOrDefaultAsync(p => p.Id == agentId, cancellationToken);
            if (agent is null || agent.Disabled || agent.Kind != "agent" || agent.ActsFor != ctx.PrincipalId || agent.AgentTenant != ctx.TenantId)
            {
                throw KernelException.Forbidden("This AI agent is disabled or not allowed to act for you.");
            }
        }

        var member = await db.Memberships.AnyAsync(m => m.TenantId == ctx.TenantId && m.PrincipalId == ctx.PrincipalId, cancellationToken);
        if (!member)
        {
            throw KernelException.Forbidden("You are not a member of this tenant.");
        }

        var revision = await db.Tenants.Where(t => t.Id == ctx.TenantId).Select(t => t.AuthzRevision).FirstAsync(cancellationToken);
        var (roles, groups) = fresh
            ? await graph.GetFactsAsync(ctx.TenantId, ctx.PrincipalId, cancellationToken)
            : await cache.GetOrAddAsync(ctx.TenantId, ctx.PrincipalId, revision, () => graph.GetFactsAsync(ctx.TenantId, ctx.PrincipalId, cancellationToken));
        var provenance = await RoleProvenanceAsync(db, ctx.PrincipalId, groups, cancellationToken);
        var facts = new AuthorizationFacts
        {
            PrincipalId = ctx.PrincipalId,
            Roles = roles.ToDictionary(r => r, r => provenance.GetValueOrDefault(r) ?? (IReadOnlyList<string>)["graph"], StringComparer.Ordinal),
            Groups = groups,
        };
        return new EffectivePermissions(config, facts, ctx);
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> RoleProvenanceAsync(KernelDbContext db, Guid principal, IReadOnlySet<Guid> groups, CancellationToken cancellationToken)
    {
        var groupIds = groups.ToList();
        var assignments = await db.RoleAssignments.Where(r => r.PrincipalId == principal || (r.GroupId != null && groupIds.Contains(r.GroupId.Value))).ToListAsync(cancellationToken);
        var groupKeys = await db.Groups.Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Key, cancellationToken);
        return assignments.GroupBy(a => a.RoleId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)g.Select(a => a.PrincipalId is not null ? "direct" : $"group:{groupKeys.GetValueOrDefault(a.GroupId!.Value, "?")}").Distinct().ToList(),
            StringComparer.Ordinal);
    }
}

/// <summary>
/// Relationship facts per (tenant, principal, authz_revision) for a few seconds. A revision bump (any membership,
/// group or role change) makes older entries unreachable; the short lifetime bounds staleness when the graph
/// and the identity tables are written in two steps (REQ-ACC-07: non-sensitive access within 10 seconds).
/// </summary>
public sealed class AuthzFactsCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, Guid Principal), Entry> entries = new();

    public async Task<(IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups)> GetOrAddAsync(Guid tenant, Guid principal, long revision, Func<Task<(IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups)>> load)
    {
        var now = clock.GetUtcNow();
        if (entries.TryGetValue((tenant, principal), out var e) && e.Revision == revision && now - e.At < Lifetime)
        {
            return e.Facts;
        }

        var facts = await load();
        entries[(tenant, principal)] = new Entry(revision, now, facts);
        if (entries.Count > 50_000)
        {
            foreach (var key in entries.Where(x => now - x.Value.At >= Lifetime).Select(x => x.Key).ToList())
            {
                entries.TryRemove(key, out _);
            }
        }

        return facts;
    }

    private sealed record Entry(long Revision, DateTimeOffset At, (IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups) Facts);
}
