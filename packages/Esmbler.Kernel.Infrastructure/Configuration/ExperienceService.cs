using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <param name="Requests">The entity receives catalog requests: "New" opens the service catalog instead of a form.</param>
/// <summary>A dashboard in the navigation (ADR 0019).</summary>
public sealed record DashboardNavItem(string View, string Label, string Entity);

/// <summary>What the client needs to know about a metric a view shows (only metrics the viewer may see).</summary>
public sealed record MetricClientView(string Id, string Label, string Entity, string? Filter, IReadOnlyList<string> Sla, string Measure, string? Format, string? GroupBy);

public sealed record NavItem(string Entity, string Label, string PluralLabel, string? ListView, string? RecordView, string? QuickCreateView, bool CanCreate, bool Requests = false);

/// <summary>A catalog item as the catalog and request forms show it: questions, plus the fulfillment steps as titles.</summary>
public sealed record CatalogItemView(
    string Id,
    string Label,
    string? Description,
    string? Category,
    string? Icon,
    string RequestEntity,
    bool Active,
    int Order,
    IReadOnlyList<VariableSpec> Variables,
    IReadOnlyList<CatalogStepView> Steps);

public sealed record CatalogStepView(string Id, string Title, IReadOnlyList<string> DependsOn, bool Conditional);

public sealed record CatalogView(IReadOnlyList<CatalogItemView> Items, IReadOnlyList<ChoiceListSpec> ChoiceLists, int ConfigRevision);

public sealed record AppBootstrap(
    object Tenant,
    object Environment,
    object User,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Actions,
    int ConfigRevision,
    IReadOnlyList<object> Modules,
    IReadOnlyList<NavItem> Navigation,
    IReadOnlyList<TenantChoice> Workspaces,
    bool Catalog = false,
    bool Approvals = false,
    IReadOnlyList<DashboardNavItem>? Dashboards = null,
    bool Alerts = false);

/// <summary>Entity definition as one viewer may see it (hidden fields and rules that read them are removed).</summary>
public sealed record EntityClientSpec(
    EntitySpec Entity,
    IReadOnlyDictionary<string, string> FieldAccess,
    IReadOnlyList<ChoiceListSpec> ChoiceLists,
    IReadOnlyList<RelationshipSpec> Relationships,
    WorkflowSpec? Workflow,
    IReadOnlyList<RuleSpec> Rules,
    IReadOnlyList<string> Customized,
    int ConfigRevision,
    IReadOnlyList<CatalogItemView>? CatalogItems = null);

public sealed record ViewClientSpec(ViewSpec View, EntityClientSpec Entity, IReadOnlyDictionary<string, EntityClientSpec> RelatedEntities, int ConfigRevision, IReadOnlyDictionary<string, MetricClientView>? Metrics = null);

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
                    perms.CanCreate(e),
                    e.Request is not null))
                .ToList();
            var catalog = artifact.Config.CatalogItems.Any(i => i.Active && artifact.Config.FindEntity(i.RequestEntity) is { } re && perms.CanCreate(re));
            var dashboards = artifact.Config.Views
                .Where(v => v.Kind == ViewKinds.Dashboard && perms.Entity(v.Entity).Read != RecordScopes.None)
                .Where(v => v.Layout.Descendants().Any(n => !n.Hidden && n.StringProp("metric") is { } m && artifact.Config.FindMetric(m) is { } metric && !Metrics.MetricService.Restricted(artifact.Config, perms, artifact.Config.FindEntity(metric.Entity)!, metric)))
                .Select(v => new DashboardNavItem(v.Id, v.Label ?? v.Id, v.Entity))
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
                workspaces,
                catalog,
                ctx.AgentId is null && artifact.Config.Entities.Exists(e => e.Request?.Approval is not null),
                dashboards,
                // Alerts (ADR 0022): for responders and for members of a group, which may own alerts.
                ctx.AgentId is null && artifact.Config.AlertRules.Count > 0 && (Alerts.AlertService.CanRespond(perms) || perms.Facts.Groups.Count > 0));
        }, ct);
    }

    /// <summary>The service catalog: active items whose request entity the viewer may create, with their questions.</summary>
    public Task<CatalogView> CatalogAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            var items = artifact.Config.CatalogItems
                .Where(i => i.Active && artifact.Config.FindEntity(i.RequestEntity) is { } e && perms.CanCreate(e))
                .OrderBy(i => i.Order).ThenBy(i => i.Label, StringComparer.Ordinal)
                .ToList();
            return new CatalogView(items.Select(ToView).ToList(), VariableLists(artifact.Config, items), artifact.Revision);
        }, ct);

    public static CatalogItemView ToView(CatalogItemSpec item) => new(
        item.Id,
        item.Label,
        item.Description,
        item.Category,
        item.Icon,
        item.RequestEntity,
        item.Active,
        item.Order,
        item.Variables,
        item.Tasks.Select(t => new CatalogStepView(t.Id, t.Title, t.DependsOn, !string.IsNullOrWhiteSpace(t.When))).ToList());

    private static List<ChoiceListSpec> VariableLists(EffectiveConfig config, IEnumerable<CatalogItemSpec> items) =>
        items.SelectMany(i => i.Variables).Where(v => v.ChoiceList is not null).Select(v => config.FindChoiceList(v.ChoiceList!)).OfType<ChoiceListSpec>().DistinctBy(c => c.Id).ToList();

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
        var metrics = new Dictionary<string, MetricClientView>(StringComparer.Ordinal);
        foreach (var node in copy.Layout.Descendants())
        {
            var ids = node.Props["views"] is JsonArray items ? items.Select(i => i?["metric"]).OfType<JsonValue>().Select(v => v.ToString()) : [];
            foreach (var id in ids.Append(node.StringProp("metric")).OfType<string>())
            {
                if (config.FindMetric(id) is { } m)
                {
                    metrics[id] = new MetricClientView(m.Id, m.Label, m.Entity, m.Filter, m.Sla is { } s ? [$"{s.Policy}{(s.Target is null ? string.Empty : "/" + s.Target)}:{s.State}"] : [], m.Measure.Kind, m.Format, m.GroupBy?.Field);
                }
            }
        }

        return new ViewClientSpec(copy, ForEntity(config, entity, perms, revision), related, revision, metrics);
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

        // Requests: the items they can reference (inactive ones too, for older requests) and their questions' choice lists.
        List<CatalogItemView>? catalog = null;
        if (entity.Request is { } binding && visible.Contains(binding.VariablesField))
        {
            var items = config.CatalogItems.Where(i => i.RequestEntity == entity.Id).ToList();
            catalog = items.Select(ToView).ToList();
            lists = lists.Concat(VariableLists(config, items)).DistinctBy(c => c.Id).ToList();
        }

        return new EntityClientSpec(copy, access.Where(a => visible.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value), lists, rels, config.WorkflowFor(entity.Id), rules, customized, revision, catalog);
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
            // Figures the viewer may not see are removed, not shown empty (ADR 0019).
            if (child.StringProp("metric") is { } metricId && (config.FindMetric(metricId) is not { } metric || config.FindEntity(metric.Entity) is not { } metricEntity || Metrics.MetricService.Restricted(config, perms, metricEntity, metric)))
            {
                node.Children.Remove(child);
                continue;
            }

            if (child.Props["views"] is JsonArray savedViews)
            {
                foreach (var item in savedViews.ToList().Where(i => i?["metric"] is JsonValue v && (config.FindMetric(v.ToString()) is not { } sm || config.FindEntity(sm.Entity) is not { } se || Metrics.MetricService.Restricted(config, perms, se, sm))))
                {
                    savedViews.Remove(item);
                }
            }

            if (child.Type == "collection.data_table")
            {
                // Grouping on fields the viewer cannot see is dropped (the server refuses it anyway).
                var tableEntity = child.StringProp("entity") is { } te ? config.FindEntity(te) ?? nodeEntity : nodeEntity;
                bool Visible(string? key) => key is not null && tableEntity.FindField(key) is { Hidden: false } && perms.FieldAccess(tableEntity, key) != FieldAccessLevels.Hidden;
                if (child.StringProp("groupBy") is { } groupBy && !Visible(groupBy))
                {
                    child.Props.Remove("groupBy");
                }

                if (child.Props["groupOptions"] is JsonArray options)
                {
                    foreach (var option in options.ToList().Where(o => !Visible(o?.GetValue<string>())))
                    {
                        options.Remove(option);
                    }
                }
            }

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
