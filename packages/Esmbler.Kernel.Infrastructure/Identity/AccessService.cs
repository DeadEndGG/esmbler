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
public sealed class AccessService(IRelationshipGraph graph)
{
    public async Task<EffectivePermissions> ForAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, CancellationToken cancellationToken)
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

        var (roles, groups) = await graph.GetFactsAsync(ctx.TenantId, ctx.PrincipalId, cancellationToken);
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
