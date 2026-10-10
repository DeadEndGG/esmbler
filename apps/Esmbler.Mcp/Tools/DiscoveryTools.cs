using System.ComponentModel;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Registry;
using Esmbler.Sdk.Specs;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Esmbler.Mcp.Tools;

/// <summary>Discovery tools: what the platform offers and what this tenant has built (REQ-AI-003).</summary>
[McpServerToolType]
public sealed class DiscoveryTools(ToolRunner runner, IElementRegistry registry, PreviewService preview, DraftService drafts)
{
    [McpServerTool(Name = "list_elements", ReadOnly = true), Description("List the platform elements you can build with (layout containers, field types, display widgets, collections, actions). Use describe_element for props and rules before inserting one.")]
    public Task<CallToolResult> ListElements([Description("Optional category: layout, field_type, field, display, collection, activity, action, feedback, spec_object.")] string? category = null) =>
        runner.RunAsync("list_elements", AgentScopes.ConfigRead, category, _ => Task.FromResult<object?>(registry.All
            .Where(e => category is null || e.Category == category)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new { e.Id, e.Label, e.Category, e.Placeable, summary = e.Description.Split(". ")[0] + "." })));

    [McpServerTool(Name = "describe_element", ReadOnly = true), Description("Full definition of one element: description, usage guidance, constraints, props JSON Schema, allowed parents and children, and an example.")]
    public Task<CallToolResult> DescribeElement([Description("Element ID, e.g. layout.card or field.short_text.")] string element) =>
        runner.RunAsync("describe_element", AgentScopes.ConfigRead, element, _ =>
            Task.FromResult<object?>(registry.Find(element) ?? throw Kernel.KernelException.NotFound($"Element '{element}'", element)));

    [McpServerTool(Name = "list_entities", ReadOnly = true), Description("Entities (record types) in this workspace, with their field counts and pages. Reflects the open draft if there is one.")]
    public Task<CallToolResult> ListEntities() =>
        runner.RunAsync("list_entities", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, published) = await preview.WorkingConfigAsync(ctx, default);
            return new
            {
                draftVersion,
                publishedRevision = published,
                entities = config.Entities.Select(e => new { e.Id, e.Label, fields = e.Fields.Count, views = config.Views.Where(v => v.Entity == e.Id).Select(v => new { v.Id, v.Kind, v.Label }) }),
            };
        });

    [McpServerTool(Name = "list_event_types", ReadOnly = true), Description("Event types in the open draft: what each means, its payload fields, default severity and sensitivity, and which transitions and rules publish it. Notifications, alerts and integrations subscribe to these.")]
    public Task<CallToolResult> ListEventTypes() =>
        runner.RunAsync("list_event_types", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            var publishers = config.Workflows.SelectMany(w => w.Transitions.SelectMany(t => t.Emits.Select(e => (e.Type, By: $"{w.Id}/{t.Id}"))))
                .Concat(config.Rules.SelectMany(r => r.Actions.Where(a => a.Emit is not null).Select(a => (a.Emit!.Type, By: r.Id))))
                .ToLookup(p => p.Type, p => p.By);
            return new
            {
                draftVersion,
                eventTypes = config.EventTypes.Select(e => new
                {
                    e.Id,
                    e.Version,
                    e.Label,
                    e.Description,
                    e.SubjectEntity,
                    e.Severity,
                    e.Sensitivity,
                    fields = e.Fields.Select(f => new { key = f.Key, f.Value.Type, f.Value.Required }),
                    publishedBy = publishers[e.Id],
                }),
            };
        });

    [McpServerTool(Name = "list_approval_policies", ReadOnly = true), Description("Approval policies in the open draft: stages in order with their approvers, conditions and expiry, and which catalog items use each policy.")]
    public Task<CallToolResult> ListApprovalPolicies() =>
        runner.RunAsync("list_approval_policies", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            return new
            {
                draftVersion,
                policies = config.ApprovalPolicies.Select(p => new
                {
                    p.Id,
                    p.Label,
                    p.Entity,
                    customized = config.Customized.Contains(p.Id),
                    usedBy = config.CatalogItems.Where(i => i.ApprovalPolicy == p.Id).Select(i => i.Id),
                    stages = p.Stages.Select(s => new { s.Id, s.Label, s.Mode, approvers = s.Approvers.Select(a => $"{a.Kind}:{a.Ref}"), s.When, s.ExpiresAfterHours, s.OnExpiry, escalateTo = s.EscalateTo.Select(a => $"{a.Kind}:{a.Ref}") }),
                }),
            };
        });

    [McpServerTool(Name = "list_sla_policies", ReadOnly = true), Description("SLA policies and business calendars in the open draft: each target's start, pause and stop conditions, goals (minutes, by condition) and the calendar they are measured in, plus each calendar's time zone, hours and holidays.")]
    public Task<CallToolResult> ListSlaPolicies() =>
        runner.RunAsync("list_sla_policies", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            return new
            {
                draftVersion,
                policies = config.SlaPolicies.Select(p => new
                {
                    p.Id,
                    p.Label,
                    p.Entity,
                    p.When,
                    p.Enabled,
                    customized = config.Customized.Contains(p.Id),
                    targets = p.Targets.Select(t => new { t.Id, t.Label, t.Start, t.Pause, t.Stop, t.OnReopen, t.Calendar, t.WarnAtPercent, goals = t.Goals.Select(g => new { g.When, g.Minutes, g.Calendar }) }),
                }),
                calendars = config.BusinessCalendars.Select(c => new
                {
                    c.Id,
                    c.Label,
                    c.TimeZone,
                    customized = config.Customized.Contains(c.Id),
                    week = c.Week.Select(w => $"{w.Day} {w.Start}-{w.End}"),
                    holidays = c.Holidays.Select(h => h.Label is null ? h.Date : $"{h.Date} {h.Label}"),
                }),
            };
        });

    [McpServerTool(Name = "list_metrics", ReadOnly = true), Description("Metrics in the open draft (what each counts or measures, its filter, grouping, date range, SLA condition and who sees it) and the dashboards with the tiles that show them. A tile and the list it opens always count the same records.")]
    public Task<CallToolResult> ListMetrics() =>
        runner.RunAsync("list_metrics", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            return new
            {
                draftVersion,
                metrics = config.Metrics.Select(m => new
                {
                    m.Id,
                    m.Label,
                    m.Entity,
                    m.Filter,
                    measure = m.Measure.Kind == MetricMeasures.Duration ? $"duration {m.Measure.From} → {m.Measure.To} in {m.Measure.Unit}" : m.Measure.Field is null ? m.Measure.Kind : $"{m.Measure.Kind}({m.Measure.Field})",
                    groupBy = m.GroupBy is null ? null : m.GroupBy.Bucket is null ? m.GroupBy.Field : $"{m.GroupBy.Field} by {m.GroupBy.Bucket}",
                    dateRange = m.DateRange is null ? null : $"{m.DateRange.Field} in the last {m.DateRange.Last} {m.DateRange.Unit}(s)",
                    sla = m.Sla is null ? null : $"{m.Sla.Policy}{(m.Sla.Target is null ? string.Empty : "/" + m.Sla.Target)}: {m.Sla.State}",
                    m.Roles,
                    customized = config.Customized.Contains(m.Id),
                    usedBy = Composer.MetricUsers(config, m.Id).ToList(),
                }),
                dashboards = config.Views.Where(v => v.Kind == ViewKinds.Dashboard).Select(v => new
                {
                    v.Id,
                    v.Label,
                    v.Entity,
                    tiles = v.Layout.Descendants().Where(n => n.StringProp("metric") is not null).Select(n => new { n.Id, n.Type, metric = n.StringProp("metric") }),
                    containers = v.Layout.Descendants().Where(n => n.Type is "layout.column" or "layout.card").Select(n => n.Id),
                }),
            };
        });

    [McpServerTool(Name = "describe_metric_fields", ReadOnly = true), Description("What a metric on an entity can use: fields to group by (choice, status, priority, user, group, toggle), date fields for date ranges and trends, number fields to sum or average, and SLA policies whose breaches it can count.")]
    public Task<CallToolResult> DescribeMetricFields(string entity) =>
        runner.RunAsync("describe_metric_fields", AgentScopes.ConfigRead, entity, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            var spec = config.FindEntity(entity) ?? throw KernelException.NotFound($"Entity '{entity}'");
            var visible = spec.Fields.Where(f => !f.Hidden).ToList();
            return new
            {
                draftVersion,
                entity = spec.Id,
                groupable = visible.Where(ConfigValidator.IsGroupable).Select(f => new { f.Key, f.Label, f.Type }),
                dates = visible.Where(f => f.Type is FieldTypes.Date or FieldTypes.DateTime).Select(f => new { f.Key, f.Label, f.Type }).Prepend(new { Key = "updated_at", Label = "Updated", Type = FieldTypes.DateTime }).Prepend(new { Key = "created_at", Label = "Created", Type = FieldTypes.DateTime }),
                numbers = visible.Where(f => f.Type is FieldTypes.Integer or FieldTypes.Decimal).Select(f => new { f.Key, f.Label, f.Type }),
                slaPolicies = config.SlaPolicies.Where(p => p.Entity == spec.Id).Select(p => new { p.Id, p.Label, targets = p.Targets.Select(t => t.Id) }),
                buckets = MetricBuckets.All,
                measures = MetricMeasures.All,
                hint = "Filters use the list filter language over record and user, e.g. !(record.status in ['resolved', 'closed']) && record.assignee == user.id.",
            };
        });

    [McpServerTool(Name = "list_catalog_items", ReadOnly = true), Description("Service catalog items in the open draft: questions (variables with types), fulfillment tasks with dependencies and conditions, and whether each can be requested.")]
    public Task<CallToolResult> ListCatalogItems() =>
        runner.RunAsync("list_catalog_items", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            return new
            {
                draftVersion,
                items = config.CatalogItems.OrderBy(i => i.Order).Select(i => new
                {
                    i.Id,
                    i.Label,
                    i.Category,
                    i.Active,
                    i.RequestEntity,
                    i.ApprovalPolicy,
                    customized = config.Customized.Contains(i.Id),
                    variables = i.Variables.Select(v => new { v.Key, v.Label, v.Type, v.Required, v.ChoiceList }),
                    tasks = i.Tasks.Select(t => new { t.Id, t.Title, t.DependsOn, t.When, t.AssignmentGroup }),
                }),
            };
        });

    [McpServerTool(Name = "describe_entity", ReadOnly = true), Description("Fields (key, type, label, required, options), relationships, workflow states and transitions, and rules of an entity, as seen in the open draft. Field keys you add must start with tenant__.")]
    public Task<CallToolResult> DescribeEntity([Description("Entity ID, e.g. core.tickets.ticket.")] string entity) =>
        runner.RunAsync("describe_entity", AgentScopes.ConfigRead, entity, async ctx =>
        {
            var (config, perms, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            var e = config.FindEntity(entity) ?? throw new Kernel.KernelException("not_found", $"No entity '{entity}'.", 404, entity, Esmbler.Expressions.Expression.Suggest(entity, config.Entities.Select(x => x.Id)));
            var access = perms.FieldAccessMap(e);
            return new
            {
                draftVersion,
                e.Id,
                e.Label,
                e.TitleField,
                fields = e.Fields.Where(f => access[f.Key] != FieldAccessLevels.Hidden).Select(f => new
                {
                    f.Key,
                    f.Type,
                    f.Label,
                    required = f.Required ? true : (bool?)null,
                    readOnly = f.ReadOnly || f.System ? true : (bool?)null,
                    hidden = f.Hidden ? true : (bool?)null,
                    customized = config.Customized.Contains($"{e.Id}/{f.Key}") ? true : (bool?)null,
                    options = f.ChoiceList is null ? null : config.FindChoiceList(f.ChoiceList)?.Options.Where(o => !o.Hidden).Select(o => new { o.Id, o.Label }),
                    f.ChoiceList,
                    f.Target,
                    f.MaxLength,
                }),
                relationships = config.Relationships.Where(r => r.Entity == e.Id || r.Target == e.Id).Select(r => new { r.Id, r.Entity, r.Field, r.Target, r.Label, r.InverseLabel }),
                workflow = config.WorkflowFor(e.Id) is { } wf ? new { wf.Id, wf.StateField, policy = wf.ActiveInstances.Policy, states = wf.States.Select(s => new { s.Id, s.Category }), transitions = wf.Transitions.Where(t => !t.System).Select(t => new { t.Id, t.Label, t.From, t.To, t.Enabled }) } : null,
                rules = config.Rules.Where(r => r.Entity == e.Id).Select(r => new { r.Id, r.Kind, r.Scope, r.Description, r.When, r.Assert, r.Enabled, actions = r.Actions.Select(a => new { a.Type, a.Field }) }),
            };
        });

    [McpServerTool(Name = "get_view", ReadOnly = true), Description("Layout tree of a page (record, list, or form view) with every node's id, type, and props, as seen in the open draft. Use node ids as parent/anchor targets for insert_node and add_card.")]
    public Task<CallToolResult> GetView([Description("View ID, e.g. core.tickets.record.")] string view) =>
        runner.RunAsync("get_view", AgentScopes.ConfigRead, view, async ctx =>
        {
            var (config, perms, draftVersion, revision) = await preview.WorkingConfigAsync(ctx, default);
            var spec = ExperienceService.ForView(config, view, perms, revision);
            return new { draftVersion, spec.View.Id, spec.View.Kind, spec.View.Entity, outline = Outline(spec.View.Layout, 0).ToList(), layout = spec.View.Layout };
        });

    [McpServerTool(Name = "get_customizations", ReadOnly = true), Description("What this workspace changed compared with the default modules (\"what have we customized?\"), per element, with the layer that owns each base element.")]
    public Task<CallToolResult> GetCustomizations() =>
        runner.RunAsync("get_customizations", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            return new { draftVersion, customized = config.Customized.Select(k => new { element = k, layer = config.Provenance.GetValueOrDefault(k, "tenant") }) };
        });

    [McpServerTool(Name = "get_draft", ReadOnly = true), Description("The open draft: its version (pass it as expected_version to build tools), the steps so far, validation diagnostics, and the change list.")]
    public Task<CallToolResult> GetDraft() =>
        runner.RunAsync("get_draft", AgentScopes.ConfigRead, null, async ctx => (object?)await drafts.CurrentAsync(ctx, default) ?? new { draft = (object?)null, hint = "No open draft. Call open_draft." });

    private static IEnumerable<string> Outline(NodeSpec node, int depth)
    {
        var label = node.StringProp("title") ?? node.StringProp("field") ?? node.StringProp("text") ?? string.Empty;
        var span = node.Props["span"] is { } s ? $" span={s}" : string.Empty;
        yield return $"{new string(' ', depth * 2)}{node.Type} {node.Id}{(label.Length > 0 ? $" \"{label}\"" : string.Empty)}{span}";
        foreach (var child in node.Children)
        {
            foreach (var line in Outline(child, depth + 1))
            {
                yield return line;
            }
        }
    }
}
