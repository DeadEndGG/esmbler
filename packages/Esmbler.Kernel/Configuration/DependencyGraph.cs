using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Expressions;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

public sealed record DependencyEdge(string Dependent, string Dependency, string Kind);

/// <summary>
/// Dependency graph over schema references, rules, pages, workflows, and policies. Element keys:
/// entity ID, "entity/field", choice list ID, rule ID, view node ID, workflow ID, permission set ID.
/// Used for removal blocking, impact previews, and upgrade reports.
/// </summary>
public sealed partial class DependencyGraph
{
    private readonly List<DependencyEdge> _edges = [];

    public IReadOnlyList<DependencyEdge> Edges => _edges;

    public IEnumerable<string> DependentsOf(string key) =>
        _edges.Where(e => e.Dependency == key).Select(e => e.Dependent).Distinct(StringComparer.Ordinal);

    /// <summary>Transitive dependents (impact set) of an element.</summary>
    public IReadOnlySet<string> ImpactOf(string key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([key]);
        while (queue.Count > 0)
        {
            foreach (var d in DependentsOf(queue.Dequeue()))
            {
                if (seen.Add(d))
                {
                    queue.Enqueue(d);
                }
            }
        }

        return seen;
    }

    public static DependencyGraph Build(SpecCatalog config)
    {
        var g = new DependencyGraph();
        foreach (var entity in config.Entities)
        {
            foreach (var field in entity.Fields)
            {
                var key = $"{entity.Id}/{field.Key}";
                g.Add(key, entity.Id, "field_of");
                if (field.ChoiceList is not null)
                {
                    g.Add(key, field.ChoiceList, "uses_choice_list");
                }

                if (field.Target is not null)
                {
                    g.Add(key, field.Target, "lookup_target");
                }
            }

            g.Add(entity.Id, $"{entity.Id}/{entity.TitleField}", "title_field");
            foreach (var access in entity.AccessFields)
            {
                g.Add(entity.Id, $"{entity.Id}/{access.Field}", "access_field");
            }
        }

        foreach (var rel in config.Relationships)
        {
            g.Add(rel.Id, $"{rel.Entity}/{rel.Field}", "relationship_field");
            g.Add(rel.Id, rel.Target, "relationship_target");
        }

        foreach (var rule in config.Rules)
        {
            g.AddExpression(rule.Id, rule.Entity, rule.When, "rule_condition");
            g.AddExpression(rule.Id, rule.Entity, rule.Assert, "rule_assert");
            if (rule.Field is not null)
            {
                g.Add(rule.Id, $"{rule.Entity}/{rule.Field}", "rule_message_field");
            }

            foreach (var action in rule.Actions.Where(a => a.Emit is not null))
            {
                g.Add(rule.Id, action.Emit!.Type, "rule_emits");
            }

            foreach (var action in rule.Actions)
            {
                if (action.Field is not null)
                {
                    g.Add(rule.Id, $"{rule.Entity}/{action.Field}", "rule_action");
                }

                g.AddExpression(rule.Id, rule.Entity, action.Value, "rule_action_value");
            }

            if (rule.View is not null)
            {
                g.Add(rule.Id, rule.View, "rule_view");
            }
        }

        foreach (var wf in config.Workflows)
        {
            g.Add(wf.Id, $"{wf.Entity}/{wf.StateField}", "workflow_state_field");
            foreach (var t in wf.Transitions)
            {
                g.AddExpression(wf.Id, wf.Entity, t.Guard, "transition_guard");
                foreach (var emit in t.Emits)
                {
                    g.Add(wf.Id, emit.Type, "transition_emits");
                }

                foreach (var f in t.RequiredFields)
                {
                    g.Add(wf.Id, $"{wf.Entity}/{f}", "transition_required_field");
                }

                foreach (var (f, expr) in t.Set)
                {
                    g.Add(wf.Id, $"{wf.Entity}/{f}", "transition_sets");
                    g.AddExpression(wf.Id, wf.Entity, expr, "transition_value");
                }

                foreach (var role in t.Roles)
                {
                    g.Add(wf.Id, role, "transition_role");
                }
            }
        }

        foreach (var entity in config.Entities.Where(e => e.Request is not null))
        {
            var b = entity.Request!;
            g.Add(entity.Id, $"{entity.Id}/{b.CatalogItemField}", "request_catalog_item_field");
            g.Add(entity.Id, $"{entity.Id}/{b.VariablesField}", "request_variables_field");
            g.Add(entity.Id, b.TaskEntity, "request_task_entity");
            g.Add(entity.Id, $"{b.TaskEntity}/{b.TaskRequestField}", "request_task_link");
        }

        foreach (var item in config.CatalogItems)
        {
            g.Add(item.Id, item.RequestEntity, "catalog_request_entity");
            foreach (var v in item.Variables.Where(v => v.ChoiceList is not null))
            {
                g.Add(item.Id, v.ChoiceList!, "variable_choice_list");
            }

            foreach (var t in item.Tasks.Where(t => !string.IsNullOrWhiteSpace(t.When)))
            {
                g.AddExpression(item.Id, item.RequestEntity, t.When, "task_condition");
            }

            if (item.ApprovalPolicy is { } policy)
            {
                g.Add(item.Id, policy, "catalog_approval_policy");
            }
        }

        foreach (var metric in config.Metrics)
        {
            g.Add(metric.Id, metric.Entity, "metric_entity");
            g.AddExpression(metric.Id, metric.Entity, metric.Filter, "metric_filter");
            foreach (var key in new[] { metric.Measure.Field, metric.Measure.From, metric.Measure.To }.OfType<string>())
            {
                g.Add(metric.Id, $"{metric.Entity}/{key}", "metric_measure");
            }

            if (metric.GroupBy is { } group)
            {
                g.Add(metric.Id, $"{metric.Entity}/{group.Field}", "metric_group");
            }

            if (metric.DateRange is { } range)
            {
                g.Add(metric.Id, $"{metric.Entity}/{range.Field}", "metric_date");
                if (range.Calendar is { } calendar)
                {
                    g.Add(metric.Id, calendar, "metric_calendar");
                }
            }

            if (metric.Sla is { } metricSla)
            {
                g.Add(metric.Id, metricSla.Policy, "metric_sla");
            }

            foreach (var role in metric.Roles)
            {
                g.Add(metric.Id, role, "metric_role");
            }
        }

        foreach (var sla in config.SlaPolicies)
        {
            g.Add(sla.Id, sla.Entity, "sla_entity");
            g.AddExpression(sla.Id, sla.Entity, sla.When, "sla_condition");
            foreach (var target in sla.Targets)
            {
                foreach (var condition in new[] { target.Start, target.Stop, target.Pause }.Concat(target.Goals.Select(goal => goal.When)))
                {
                    g.AddExpression(sla.Id, sla.Entity, condition, "sla_condition");
                }

                foreach (var calendar in target.Goals.Select(goal => goal.Calendar).Append(target.Calendar).OfType<string>().Where(c => c != SlaCalendars.AroundTheClock).Distinct(StringComparer.Ordinal))
                {
                    g.Add(sla.Id, calendar, "sla_calendar");
                }
            }
        }

        foreach (var policy in config.ApprovalPolicies)
        {
            g.Add(policy.Id, policy.Entity, "approval_entity");
            foreach (var stage in policy.Stages)
            {
                foreach (var approver in stage.Approvers.Concat(stage.EscalateTo))
                {
                    if (approver.Kind == ApproverKinds.Role)
                    {
                        g.Add(policy.Id, approver.Ref, "approver_role");
                    }
                    else if (approver.Kind == ApproverKinds.Field && approver.Ref.StartsWith("record.", StringComparison.Ordinal))
                    {
                        g.Add(policy.Id, $"{policy.Entity}/{approver.Ref["record.".Length..]}", "approver_field");
                    }
                }

                if (!string.IsNullOrWhiteSpace(stage.When))
                {
                    g.AddExpression(policy.Id, policy.Entity, stage.When, "stage_condition");
                }
            }
        }

        foreach (var view in config.Views)
        {
            foreach (var node in view.Layout.Descendants())
            {
                g.Add(node.Id, view.Id, "node_of");
                var entity = node.StringProp("entity") ?? view.Entity;
                foreach (var prop in new[] { "field", "groupBy" })
                {
                    if (node.StringProp(prop) is { } field)
                    {
                        g.Add(node.Id, $"{entity}/{field}", "binds_field");
                    }
                }

                if (node.StringProp("metric") is { } metricId)
                {
                    g.Add(node.Id, metricId, "binds_metric");
                }

                if (node.Props["groupOptions"] is JsonArray groupOptions)
                {
                    foreach (var f in groupOptions.OfType<JsonValue>().Select(v => v.GetValue<string>()))
                    {
                        g.Add(node.Id, $"{entity}/{f}", "binds_field");
                    }
                }

                if (node.Props["views"] is JsonArray savedViews)
                {
                    foreach (var savedMetric in savedViews.Select(v => v?["metric"]).OfType<JsonValue>().Select(v => v.GetValue<string>()))
                    {
                        g.Add(node.Id, savedMetric, "binds_metric");
                    }
                }

                if (node.Props["fields"] is JsonArray fields)
                {
                    foreach (var f in fields.OfType<JsonValue>().Select(v => v.GetValue<string>()))
                    {
                        g.Add(node.Id, $"{entity}/{f}", "binds_field");
                    }
                }

                if (node.StringProp("relationship") is { } relId)
                {
                    g.Add(node.Id, relId, "binds_relationship");
                    var rel = config.Relationships.Find(r => r.Id == relId);
                    if (rel is not null && node.Props["columns"] is JsonArray cols)
                    {
                        foreach (var c in cols.OfType<JsonValue>().Select(v => v.GetValue<string>()))
                        {
                            g.Add(node.Id, $"{rel.Entity}/{c}", "binds_field");
                        }
                    }
                }

                g.AddExpression(node.Id, entity, node.StringProp("visibleWhen"), "node_condition");
                g.AddExpression(node.Id, entity, node.StringProp("filter"), "node_filter");
            }
        }

        foreach (var ps in config.PermissionSets)
        {
            foreach (var role in ps.Roles)
            {
                g.Add(ps.Id, role, "grants_role");
            }

            foreach (var e in ps.Entities)
            {
                g.Add(ps.Id, e.Entity, "entity_permission");
                g.AddExpression(ps.Id, e.Entity, e.UpdateCondition, "permission_condition");
            }

            foreach (var f in ps.Fields.Where(f => f.Field != "*"))
            {
                g.Add(ps.Id, $"{f.Entity}/{f.Field}", "field_permission");
            }
        }

        foreach (var ar in config.AssignmentRules)
        {
            g.AddExpression(ar.Id, ar.Entity, ar.When, "assignment_condition");
        }

        foreach (var nt in config.NotificationTemplates)
        {
            var text = string.Join(" ", nt.Variants.Values.Select(v => v.Subject + " " + v.Body).Prepend(nt.Subject + " " + nt.Body));
            foreach (Match m in PlaceholderRegex().Matches(text))
            {
                g.Add(nt.Id, $"{nt.Entity}/{m.Groups[1].Value}", "template_placeholder");
            }

            foreach (var f in nt.ExternalFields)
            {
                g.Add(nt.Id, $"{nt.Entity}/{f}", "template_external_field");
            }

            foreach (var r in nt.Recipients.Where(r => r != "watchers"))
            {
                g.Add(nt.Id, $"{nt.Entity}/{r}", "template_recipient");
            }
        }

        foreach (var sub in config.Subscriptions)
        {
            g.Add(sub.Id, sub.Event, "subscription_event");
            var entity = sub.Entity ?? config.FindEventType(sub.Event)?.SubjectEntity;
            if (entity is not null)
            {
                g.Add(sub.Id, entity, "subscription_entity");
                g.AddExpression(sub.Id, entity, sub.When, "subscription_condition");
            }
        }

        foreach (var route in config.NotificationRoutes)
        {
            g.Add(route.Id, route.Subscription, "route_subscription");
            g.Add(route.Id, route.Profile, "route_profile");
            g.Add(route.Id, route.Template, "route_template");
            var entity = config.FindSubscription(route.Subscription) is { } s ? s.Entity ?? config.FindEventType(s.Event)?.SubjectEntity : null;
            foreach (var r in route.Recipients)
            {
                switch (r.Kind)
                {
                    case RecipientKinds.Field when entity is not null:
                        g.Add(route.Id, $"{entity}/{r.Ref}", "route_recipient_field");
                        break;
                    case RecipientKinds.Role:
                        g.Add(route.Id, r.Ref, "route_recipient_role");
                        break;
                }
            }
        }

        return g;
    }

    private void Add(string dependent, string dependency, string kind) => _edges.Add(new DependencyEdge(dependent, dependency, kind));

    private void AddExpression(string dependent, string entity, string? expression, string kind)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        try
        {
            var compiled = Expression.Compile(expression);
            foreach (var reference in compiled.References)
            {
                if (reference.StartsWith("record.", StringComparison.Ordinal) || reference.StartsWith("prior.", StringComparison.Ordinal))
                {
                    Add(dependent, $"{entity}/{reference[(reference.IndexOf('.', StringComparison.Ordinal) + 1)..]}", kind);
                }
            }
        }
        catch (ExpressionException)
        {
            // Invalid expressions are reported by the validator; they contribute no edges.
        }
    }

    [GeneratedRegex(@"\{\{\s*record\.([a-z][a-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}
