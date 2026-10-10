using Esmbler.Expressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

public sealed record NavItem(string Entity, string Label, string PluralLabel, string? ListView, string? RecordView, string? QuickCreateView, bool CanCreate);

public sealed record AppBootstrap(
    object Tenant,
    object Environment,
    object User,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Actions,
    int ConfigRevision,
    IReadOnlyList<object> Modules,
    IReadOnlyList<NavItem> Navigation,
    IReadOnlyList<TenantChoice> Workspaces);

/// <summary>Entity definition as one viewer may see it (hidden fields and rules that read them are removed).</summary>
public sealed record EntityClientSpec(
    EntitySpec Entity,
    IReadOnlyDictionary<string, string> FieldAccess,
    IReadOnlyList<ChoiceListSpec> ChoiceLists,
    IReadOnlyList<RelationshipSpec> Relationships,
    WorkflowSpec? Workflow,
    IReadOnlyList<RuleSpec> Rules,
    IReadOnlyList<string> Customized,
    int ConfigRevision);

public sealed record ViewClientSpec(ViewSpec View, EntityClientSpec Entity, IReadOnlyDictionary<string, EntityClientSpec> RelatedEntities, int ConfigRevision);

/// <summary>Serves permission-filtered configuration to clients: pages follow permissions automatically (REQ-AUTHZ-004).</summary>
public sealed class ExperienceService(Database database, ConfigStore configs, AccessService access, IdentityService identity)
{
    public async Task<AppBootstrap> AppAsync(RequestContext ctx, CancellationToken ct)
    {
        var workspaces = await identity.TenantsForAsync(ctx.PrincipalId, ct);
        return await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == ctx.TenantId, ct);
            var env = await db.Environments.FirstAsync(e => e.Id == ctx.EnvironmentId, ct);
            var user = await db.Principals.FirstAsync(p => p.Id == ctx.PrincipalId, ct);
            var nav = artifact.Config.Entities
                .Where(e => perms.Entity(e.Id).Read != RecordScopes.None)
                .Select(e => new NavItem(
                    e.Id,
                    e.Label,
                    e.PluralLabel,
                    artifact.Config.Views.Find(v => v.Entity == e.Id && v.Kind == ViewKinds.List)?.Id,
                    artifact.Config.Views.Find(v => v.Entity == e.Id && v.Kind == ViewKinds.Record)?.Id,
                    artifact.Config.Views.Find(v => v.Entity == e.Id && v.Kind == ViewKinds.Form)?.Id,
                    perms.CanCreate(e)))
                .ToList();
            return new AppBootstrap(
                new { tenant.Id, tenant.Slug, tenant.Name },
                new { env.Id, env.Key, env.Name, env.Kind },
                new { user.Id, user.DisplayName, user.Email, agent = ctx.AgentId },
                perms.Roles.Select(r => artifact.Config.Roles.Find(x => x.Id == r)?.Label ?? r).ToList(),
                perms.Actions.Order(StringComparer.Ordinal).ToList(),
                artifact.Revision,
                artifact.Lockfile.Where(m => m.Id != PlatformModule.ModuleId).Select(m => (object)new { m.Id, m.Version }).ToList(),
                nav,
                workspaces);
        }, ct);
    }

    /// <summary>Roles defined by the active configuration, for role assignment in administration.</summary>
    public Task<IReadOnlyList<RoleSpec>> RolesAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<RoleSpec>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            if (!perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden("Role administration requires the permission administration right.");
            }

            return artifact.Config.Roles.OrderBy(r => r.Label, StringComparer.Ordinal).ToList();
        }, ct);

    public Task<EntityClientSpec> EntityAsync(RequestContext ctx, string entityId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            var entity = artifact.Config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
            if (perms.Entity(entity.Id).Read == RecordScopes.None && !perms.Entity(entity.Id).Create)
            {
                throw KernelException.NotFound($"Entity '{entityId}'");
            }

            return ForEntity(artifact.Config, entity, perms, artifact.Revision);
        }, ct);

    public Task<ViewClientSpec> ViewAsync(RequestContext ctx, string viewId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            return ForView(artifact.Config, viewId, perms, artifact.Revision);
        }, ct);

    /// <summary>Filters a view for a viewer. Also used for draft previews (same shape, draft configuration).</summary>
    public static ViewClientSpec ForView(EffectiveConfig config, string viewId, EffectivePermissions perms, int revision)
    {
        var view = config.FindView(viewId) ?? throw KernelException.NotFound($"View '{viewId}'");
        var entity = config.FindEntity(view.Entity)!;
        if (perms.Entity(entity.Id).Read == RecordScopes.None && !perms.Entity(entity.Id).Create)
        {
            throw KernelException.NotFound($"View '{viewId}'");
        }

        var copy = SpecJson.Clone(view);
        var related = new Dictionary<string, EntityClientSpec>(StringComparer.Ordinal);
        Prune(copy.Layout, config, entity, perms, related, revision);
        return new ViewClientSpec(copy, ForEntity(config, entity, perms, revision), related, revision);
    }

    public static EntityClientSpec ForEntity(EffectiveConfig config, EntitySpec entity, EffectivePermissions perms, int revision)
    {
        var access = perms.FieldAccessMap(entity);
        var visible = access.Where(a => a.Value != FieldAccessLevels.Hidden).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var copy = SpecJson.Clone(entity);
        copy.Fields = copy.Fields.Where(f => visible.Contains(f.Key)).ToList();
        copy.AccessFields = copy.AccessFields.Where(a => visible.Contains(a.Field)).ToList();
        var lists = copy.Fields.Where(f => f.ChoiceList is not null).Select(f => config.FindChoiceList(f.ChoiceList!)).OfType<ChoiceListSpec>().DistinctBy(c => c.Id).ToList();
        var rels = config.Relationships.Where(r => r.Entity == entity.Id || r.Target == entity.Id).ToList();

        // REQ-RULE-006: send only rules the client can evaluate without hidden values; others are evaluated on the server
        // and delivered as field states with each record.
        var rules = config.Rules.Where(r => r.Entity == entity.Id && r.Enabled && ReadsOnly(r, visible)).ToList();
        var customized = config.Customized.Where(k => k.StartsWith(entity.Id + "/", StringComparison.Ordinal) && visible.Contains(k[(entity.Id.Length + 1)..])).ToList();
        return new EntityClientSpec(copy, access.Where(a => visible.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value), lists, rels, config.WorkflowFor(entity.Id), rules, customized, revision);
    }

    private static bool ReadsOnly(RuleSpec rule, HashSet<string> visible)
    {
        IEnumerable<string?> exprs = [rule.When, rule.Assert, .. rule.Actions.Select(a => a.Value)];
        foreach (var expr in exprs.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            try
            {
                var refs = Expression.Compile(expr!).References;
                if (refs.Where(r => r.StartsWith("record.", StringComparison.Ordinal) || r.StartsWith("prior.", StringComparison.Ordinal)).Any(r => !visible.Contains(r[(r.IndexOf('.', StringComparison.Ordinal) + 1)..])))
                {
                    return false;
                }
            }
            catch (ExpressionException)
            {
                return false;
            }
        }

        return rule.Actions.All(a => a.Field is null || visible.Contains(a.Field));
    }

    private static void Prune(NodeSpec node, EffectiveConfig config, EntitySpec entity, EffectivePermissions perms, Dictionary<string, EntityClientSpec> related, int revision)
    {
        node.Children.RemoveAll(c => c.Hidden);
        var nodeEntity = node.StringProp("entity") is { } eid ? config.FindEntity(eid) ?? entity : entity;
        foreach (var child in node.Children.ToList())
        {
            var fieldEntity = child.Type == "collection.column" ? nodeEntity : entity;
            if (child.StringProp("field") is { } field && (fieldEntity.FindField(field) is not { Hidden: false } || perms.FieldAccess(fieldEntity, field) == FieldAccessLevels.Hidden))
            {
                node.Children.Remove(child);
                continue;
            }

            if (child.StringProp("relationship") is { } relId && config.Relationships.Find(r => r.Id == relId) is { } rel && config.FindEntity(rel.Entity) is { } childEntity)
            {
                if (perms.Entity(childEntity.Id).Read == RecordScopes.None)
                {
                    node.Children.Remove(child);
                    continue;
                }

                related[childEntity.Id] = ForEntity(config, childEntity, perms, revision);
            }

            if (child.Type == "collection.data_table" && nodeEntity.Id != entity.Id)
            {
                related[nodeEntity.Id] = ForEntity(config, nodeEntity, perms, revision);
            }

            Prune(child, config, entity, perms, related, revision);
        }
    }
}
