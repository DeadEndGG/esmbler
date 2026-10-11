using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

public sealed class CompositionResult
{
    public required EffectiveConfig Config { get; init; }

    public required IReadOnlyList<OpOutcome> Outcomes { get; init; }

    public IEnumerable<Diagnostic> Diagnostics => Outcomes.SelectMany(o => o.Diagnostics);

    public bool HasConflicts => Outcomes.Any(o => o.Status is OpStatus.Conflict or OpStatus.Failed);
}

/// <summary>
/// Layered configuration (overlay pattern): applies typed overlay operations, in order, on top of the
/// composed base modules. Never blind-merges JSON; every operation targets stable identifiers and
/// declares the prior values it expects so base changes are detected as conflicts.
/// </summary>
public static class Composer
{
    public const string TenantLayer = "tenant";

    public static CompositionResult Compose(IReadOnlyList<ModuleSpec> modules, IReadOnlyList<OverlayOp> ops)
    {
        var config = ComposeBase(modules);
        var outcomes = new List<OpOutcome>(ops.Count);
        var resetBy = ResetIndexes(ops);
        for (var i = 0; i < ops.Count; i++)
        {
            if (resetBy[i] is not null)
            {
                outcomes.Add(new OpOutcome(i, OpName(ops[i]), ops[i].Describe(), OpStatus.Reset, []));
                continue;
            }

            var ctx = new OpContext(config, i) { ResetSomething = Array.IndexOf(resetBy, i) >= 0 };
            try
            {
                Apply(ctx, ops[i]);
            }
            catch (OverlayException ex)
            {
                ctx.Fail(ex.Code, ex.Message, ex.Target, ex.Suggestion);
            }

            outcomes.Add(new OpOutcome(i, OpName(ops[i]), ops[i].Describe(), ctx.Status, ctx.Diagnostics));
        }

        config.Customized.Sort(StringComparer.Ordinal);
        return new CompositionResult { Config = config, Outcomes = outcomes };
    }

    /// <summary>For each operation, the index of a later element.reset that undoes it (null when it applies).</summary>
    private static int?[] ResetIndexes(IReadOnlyList<OverlayOp> ops)
    {
        var result = new int?[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is not ResetElement reset)
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (result[j] is null && reset.Covers(ops[j]))
                {
                    result[j] = i;
                }
            }
        }

        return result;
    }

    public static EffectiveConfig ComposeBase(IReadOnlyList<ModuleSpec> modules)
    {
        var config = new EffectiveConfig();
        foreach (var module in modules.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var layer = $"{module.Id}@{module.Version}";
            var copy = SpecJson.Clone(module);
            if (module.Id == PlatformModule.ModuleId)
            {
                copy.EventTypes = PlatformModule.EventTypes.Select(SpecJson.Clone).ToList();
                copy.NotificationProfiles = PlatformModule.NotificationProfiles.Select(SpecJson.Clone).ToList();
            }
            config.Modules.Add(new LockedModule(module.Id, module.Version, Hash(SpecJson.Canonical(SpecJson.ToNode(module)))));
            config.Entities.AddRange(copy.Entities);
            config.ChoiceLists.AddRange(copy.ChoiceLists);
            config.Relationships.AddRange(copy.Relationships);
            config.Rules.AddRange(copy.Rules);
            config.Workflows.AddRange(copy.Workflows);
            config.Views.AddRange(copy.Views);
            config.Roles.AddRange(copy.Roles);
            config.PermissionSets.AddRange(copy.PermissionSets);
            config.AssignmentRules.AddRange(copy.AssignmentRules);
            config.NotificationTemplates.AddRange(copy.NotificationTemplates);
            config.ExtensionPoints.AddRange(copy.ExtensionPoints);
            config.CatalogItems.AddRange(copy.CatalogItems);
            config.ApprovalPolicies.AddRange(copy.ApprovalPolicies);
            config.EventTypes.AddRange(copy.EventTypes);
            config.BusinessCalendars.AddRange(copy.BusinessCalendars);
            config.SlaPolicies.AddRange(copy.SlaPolicies);
            config.Metrics.AddRange(copy.Metrics);
            config.Subscriptions.AddRange(copy.Subscriptions);
            config.NotificationProfiles.AddRange(copy.NotificationProfiles);
            config.NotificationRoutes.AddRange(copy.NotificationRoutes);
            config.AlertRules.AddRange(copy.AlertRules);
            config.MailIntakes.AddRange(copy.MailIntakes);
            foreach (var key in ElementKeys(copy))
            {
                config.Provenance[key] = layer;
            }
        }

        return config;
    }

    internal static IEnumerable<string> ElementKeys(SpecCatalog catalog)
    {
        foreach (var e in catalog.Entities)
        {
            yield return e.Id;
            foreach (var f in e.Fields)
            {
                yield return $"{e.Id}/{f.Key}";
            }
        }

        foreach (var c in catalog.ChoiceLists)
        {
            yield return c.Id;
            foreach (var o in c.Options)
            {
                yield return $"{c.Id}/{o.Id}";
            }
        }

        foreach (var r in catalog.Rules)
        {
            yield return r.Id;
        }

        foreach (var v in catalog.Views)
        {
            yield return v.Id;
            foreach (var n in v.Layout.Descendants())
            {
                yield return n.Id;
            }
        }

        foreach (var w in catalog.Workflows)
        {
            yield return w.Id;
            foreach (var st in w.States)
            {
                yield return $"{w.Id}/state.{st.Id}";
            }

            foreach (var t in w.Transitions)
            {
                yield return $"{w.Id}/{t.Id}";
            }
        }

        foreach (var c in catalog.CatalogItems)
        {
            yield return c.Id;
        }

        foreach (var p in catalog.ApprovalPolicies)
        {
            yield return p.Id;
        }

        foreach (var e in catalog.EventTypes)
        {
            yield return e.Id;
        }

        foreach (var c in catalog.BusinessCalendars)
        {
            yield return c.Id;
        }

        foreach (var p in catalog.SlaPolicies)
        {
            yield return p.Id;
        }

        foreach (var m in catalog.Metrics)
        {
            yield return m.Id;
        }

        foreach (var t in catalog.NotificationTemplates)
        {
            yield return t.Id;
        }

        foreach (var s in catalog.Subscriptions)
        {
            yield return s.Id;
        }

        foreach (var p in catalog.NotificationProfiles)
        {
            yield return p.Id;
        }

        foreach (var r in catalog.NotificationRoutes)
        {
            yield return r.Id;
        }

        foreach (var r in catalog.AlertRules)
        {
            yield return r.Id;
        }

        foreach (var m in catalog.MailIntakes)
        {
            yield return m.Id;
        }

        foreach (var p in catalog.PermissionSets)
        {
            yield return p.Id;
        }

        foreach (var r in catalog.Roles)
        {
            yield return r.Id;
        }
    }

    public static string Hash(string canonicalJson) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    public static string OpName(OverlayOp op) => SpecJson.ToNode<OverlayOp>(op)?["op"]?.GetValue<string>() ?? op.GetType().Name;

    private static void Apply(OpContext ctx, OverlayOp op)
    {
        switch (op)
        {
            case AddField a:
                ApplyAddField(ctx, a);
                break;
            case UpdateField u:
                ApplyUpdateField(ctx, u);
                break;
            case HideField h:
                ApplyHideField(ctx, h);
                break;
            case RemoveField r:
                ApplyRemoveField(ctx, r);
                break;
            case AddChoiceList acl:
                RequireTenantId(acl.ChoiceList.Id, "choice list");
                if (ctx.Config.FindChoiceList(acl.ChoiceList.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Choice list '{acl.ChoiceList.Id}' already exists.", acl.ChoiceList.Id);
                }

                ctx.Config.ChoiceLists.Add(SpecJson.Clone(acl.ChoiceList));
                ctx.Mark(acl.ChoiceList.Id);
                foreach (var o in acl.ChoiceList.Options)
                {
                    ctx.Mark($"{acl.ChoiceList.Id}/{o.Id}");
                }

                break;
            case AddChoiceOption aco:
                ApplyAddChoiceOption(ctx, aco);
                break;
            case UpdateChoiceOption uco:
                ApplyUpdateChoiceOption(ctx, uco);
                break;
            case AddRule ar:
                RequireTenantId(ar.Rule.Id, "rule");
                if (ctx.Config.FindRule(ar.Rule.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"A rule with ID '{ar.Rule.Id}' already exists.", ar.Rule.Id, "Use update_rule to change it, or choose a new ID.");
                }

                RequireEntity(ctx.Config, ar.Rule.Entity);
                ctx.Config.Rules.Add(SpecJson.Clone(ar.Rule));
                ctx.Mark(ar.Rule.Id);
                break;
            case UpdateRule ur:
                ApplyUpdateRule(ctx, ur);
                break;
            case RemoveRule rr:
                var rule = ctx.Config.FindRule(rr.Rule) ?? throw Missing("rule", rr.Rule);
                if (!IsTenant(ctx, rule.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Rule '{rr.Rule}' comes from {ctx.Layer(rule.Id)} and cannot be removed.", rr.Rule, "Disable it with update_rule (enabled: false) instead.");
                }

                ctx.Config.Rules.Remove(rule);
                ctx.Config.Provenance.Remove(rule.Id);
                break;
            case InsertNode ins:
                ApplyInsertNode(ctx, ins);
                break;
            case UpdateNode un:
                ApplyUpdateNode(ctx, un);
                break;
            case MoveNode mv:
                ApplyMoveNode(ctx, mv);
                break;
            case RemoveNode rn:
                ApplyRemoveNode(ctx, rn);
                break;
            case AddView av:
                RequireTenantId(av.View.Id, "view");
                if (av.View.Kind != ViewKinds.Dashboard)
                {
                    throw new OverlayException("overlay.unsupported", $"Only dashboards can be added; '{av.View.Label}' is a {av.View.Kind} view.", av.View.Id, "Set kind to dashboard, or change the module's record and list views with node operations.");
                }

                if (ctx.Config.FindView(av.View.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"View '{av.View.Id}' already exists.", av.View.Id, "Choose a new ID.");
                }

                foreach (var node in av.View.Layout.Descendants())
                {
                    if (node != av.View.Layout && !node.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
                    {
                        throw new OverlayException("overlay.namespace", $"New node IDs must start with '{Namespaces.TenantPrefix}'; '{node.Id}' does not.", node.Id, Namespaces.NewTenantNodeId());
                    }
                }

                ctx.Config.Views.Add(SpecJson.Clone(av.View));
                ctx.Mark(av.View.Id);
                foreach (var node in av.View.Layout.Descendants())
                {
                    ctx.Mark(node.Id);
                }

                break;
            case RemoveView rv:
                var removedView = ctx.Config.FindView(rv.View) ?? throw Missing("view", rv.View, Expression.Suggest(rv.View, ctx.Config.Views.Select(v => v.Id)));
                if (!IsTenant(ctx, removedView.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"View '{removedView.Label}' comes from {ctx.Layer(removedView.Id)} and cannot be removed.", rv.View, "Remove or hide its nodes instead.");
                }

                ctx.Config.Views.Remove(removedView);
                ctx.Config.Provenance.Remove(removedView.Id);
                foreach (var node in removedView.Layout.Descendants())
                {
                    ctx.Config.Provenance.Remove(node.Id);
                }

                break;
            case ResetElement re:
                if (!ctx.ResetSomething)
                {
                    ctx.Redundant($"'{re.Target}' has no customizations to reset.", re.Target);
                }

                break;
            case SetFieldAccess sfa:
                ApplySetFieldAccess(ctx, sfa);
                break;
            case AddCatalogItem aci:
                RequireTenantId(aci.Item.Id, "catalog item");
                if (ctx.Config.FindCatalogItem(aci.Item.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Catalog item '{aci.Item.Id}' already exists.", aci.Item.Id, "Use update_catalog_item to change it, or choose a new ID.");
                }

                RequireEntity(ctx.Config, aci.Item.RequestEntity);
                ctx.Config.CatalogItems.Add(SpecJson.Clone(aci.Item));
                ctx.Mark(aci.Item.Id);
                break;
            case UpdateCatalogItem uci:
                ApplyUpdateCatalogItem(ctx, uci);
                break;
            case RemoveCatalogItem rci:
                var item = ctx.Config.FindCatalogItem(rci.Item) ?? throw Missing("catalog item", rci.Item, Expression.Suggest(rci.Item, ctx.Config.CatalogItems.Select(c => c.Id)));
                if (!IsTenant(ctx, item.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Catalog item '{rci.Item}' comes from {ctx.Layer(item.Id)} and cannot be removed.", rci.Item, "Deactivate it with update_catalog_item (active: false) instead.");
                }

                ctx.Config.CatalogItems.Remove(item);
                ctx.Config.Provenance.Remove(item.Id);
                break;
            case AddApprovalPolicy aap:
                RequireTenantId(aap.Policy.Id, "approval policy");
                if (ctx.Config.FindApprovalPolicy(aap.Policy.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Approval policy '{aap.Policy.Id}' already exists.", aap.Policy.Id, "Change it with update_approval_policy, or choose a new ID.");
                }

                RequireEntity(ctx.Config, aap.Policy.Entity);
                ctx.Config.ApprovalPolicies.Add(SpecJson.Clone(aap.Policy));
                ctx.Mark(aap.Policy.Id);
                break;
            case UpdateApprovalPolicy uap:
                ApplyUpdateApprovalPolicy(ctx, uap);
                break;
            case RemoveApprovalPolicy rap:
                var policy = RequireApprovalPolicy(ctx.Config, rap.Policy);
                if (!IsTenant(ctx, policy.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Approval policy '{rap.Policy}' comes from {ctx.Layer(policy.Id)} and cannot be removed.", rap.Policy, "Point catalog items at another policy with update_catalog_item instead.");
                }

                if (ctx.Config.CatalogItems.Find(i => i.ApprovalPolicy == policy.Id) is { } user)
                {
                    throw new OverlayException("overlay.in_use", $"Catalog item '{user.Label}' still uses approval policy '{policy.Label}'.", rap.Policy, $"Change '{user.Id}' to another policy first.");
                }

                ctx.Config.ApprovalPolicies.Remove(policy);
                ctx.Config.Provenance.Remove(policy.Id);
                break;
            case AddEventType aet:
                RequireTenantId(aet.EventType.Id, "event type");
                if (ctx.Config.FindEventType(aet.EventType.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Event type '{aet.EventType.Id}' already exists.", aet.EventType.Id, "Choose a new ID.");
                }

                ctx.Config.EventTypes.Add(SpecJson.Clone(aet.EventType));
                ctx.Mark(aet.EventType.Id);
                break;
            case RemoveEventType ret:
                var eventType = ctx.Config.FindEventType(ret.EventType) ?? throw Missing("event type", ret.EventType, Expression.Suggest(ret.EventType, ctx.Config.EventTypes.Select(e => e.Id)));
                if (!IsTenant(ctx, eventType.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Event type '{ret.EventType}' comes from {ctx.Layer(eventType.Id)} and cannot be removed.", ret.EventType);
                }

                ctx.Config.EventTypes.Remove(eventType);
                ctx.Config.Provenance.Remove(eventType.Id);
                break;
            case AddBusinessCalendar abc:
                RequireTenantId(abc.Calendar.Id, "business calendar");
                if (ctx.Config.FindBusinessCalendar(abc.Calendar.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Business calendar '{abc.Calendar.Id}' already exists.", abc.Calendar.Id, "Choose a new ID.");
                }

                ctx.Config.BusinessCalendars.Add(SpecJson.Clone(abc.Calendar));
                ctx.Mark(abc.Calendar.Id);
                break;
            case UpdateBusinessCalendar ubc:
                ApplyUpdateBusinessCalendar(ctx, ubc);
                break;
            case RemoveBusinessCalendar rbc:
                var calendar = ctx.Config.FindBusinessCalendar(rbc.Calendar) ?? throw Missing("business calendar", rbc.Calendar, Expression.Suggest(rbc.Calendar, ctx.Config.BusinessCalendars.Select(c => c.Id)));
                if (!IsTenant(ctx, calendar.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Business calendar '{calendar.Label}' comes from {ctx.Layer(calendar.Id)} and cannot be removed.", rbc.Calendar, "Change its hours and holidays with business_calendar.update instead.");
                }

                if (ctx.Config.SlaPolicies.Find(p => p.Targets.Any(t => t.Calendar == calendar.Id || t.Goals.Any(g => g.Calendar == calendar.Id))) is { } calendarUser)
                {
                    throw new OverlayException("overlay.in_use", $"SLA policy '{calendarUser.Label}' still measures in business calendar '{calendar.Label}'.", rbc.Calendar, $"Change '{calendarUser.Id}' to another calendar first.");
                }

                ctx.Config.BusinessCalendars.Remove(calendar);
                ctx.Config.Provenance.Remove(calendar.Id);
                break;
            case AddSlaPolicy asp:
                RequireTenantId(asp.Policy.Id, "SLA policy");
                if (ctx.Config.FindSlaPolicy(asp.Policy.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"SLA policy '{asp.Policy.Id}' already exists.", asp.Policy.Id, "Choose a new ID.");
                }

                ctx.Config.SlaPolicies.Add(SpecJson.Clone(asp.Policy));
                ctx.Mark(asp.Policy.Id);
                break;
            case UpdateSlaPolicy usp:
                var sla = ctx.Config.FindSlaPolicy(usp.Policy) ?? throw Missing("SLA policy", usp.Policy, Expression.Suggest(usp.Policy, ctx.Config.SlaPolicies.Select(p => p.Id)));
                var slaTenant = IsTenant(ctx, sla.Id);
                CheckExpected(ctx, usp.Policy, SpecJson.ToNode(sla)!.AsObject(), usp.Expected, SpecJson.ToNode(usp.Set)!.AsObject());
                sla.Label = usp.Set.Label ?? sla.Label;
                sla.Description = usp.Set.Description ?? sla.Description;
                sla.When = usp.Set.When ?? sla.When;
                sla.Enabled = usp.Set.Enabled ?? sla.Enabled;
                if (usp.Set.Targets is { } targets)
                {
                    sla.Targets = targets.Select(SpecJson.Clone).ToList();
                }

                ctx.Mark(sla.Id, keepLayer: !slaTenant);
                break;
            case RemoveSlaPolicy rsp:
                var removedSla = ctx.Config.FindSlaPolicy(rsp.Policy) ?? throw Missing("SLA policy", rsp.Policy, Expression.Suggest(rsp.Policy, ctx.Config.SlaPolicies.Select(p => p.Id)));
                if (!IsTenant(ctx, removedSla.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"SLA policy '{removedSla.Label}' comes from {ctx.Layer(removedSla.Id)} and cannot be removed.", rsp.Policy, "Disable it with sla_policy.update (enabled: false) instead.");
                }

                ctx.Config.SlaPolicies.Remove(removedSla);
                ctx.Config.Provenance.Remove(removedSla.Id);
                break;
            case AddMetric am:
                RequireTenantId(am.Metric.Id, "metric");
                if (ctx.Config.FindMetric(am.Metric.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Metric '{am.Metric.Id}' already exists.", am.Metric.Id, "Choose a new ID.");
                }

                ctx.Config.Metrics.Add(SpecJson.Clone(am.Metric));
                ctx.Mark(am.Metric.Id);
                break;
            case UpdateMetric um:
                var metric = ctx.Config.FindMetric(um.Metric) ?? throw Missing("metric", um.Metric, Expression.Suggest(um.Metric, ctx.Config.Metrics.Select(m => m.Id)));
                var metricTenant = IsTenant(ctx, metric.Id);
                CheckExpected(ctx, um.Metric, SpecJson.ToNode(metric)!.AsObject(), um.Expected, SpecJson.ToNode(um.Set)!.AsObject());
                metric.Label = um.Set.Label ?? metric.Label;
                metric.Description = um.Set.Description ?? metric.Description;
                metric.Filter = um.Set.Filter ?? metric.Filter;
                metric.Measure = um.Set.Measure is { } measure ? SpecJson.Clone(measure) : metric.Measure;
                metric.GroupBy = um.Set.GroupBy is { } group ? SpecJson.Clone(group) : metric.GroupBy;
                metric.DateRange = um.Set.DateRange is { } range ? SpecJson.Clone(range) : metric.DateRange;
                metric.Sla = um.Set.Sla is { } metricSla ? SpecJson.Clone(metricSla) : metric.Sla;
                metric.Alerts = um.Set.Alerts is { } metricAlerts ? SpecJson.Clone(metricAlerts) : metric.Alerts;
                metric.Roles = um.Set.Roles?.ToList() ?? metric.Roles;
                metric.Format = um.Set.Format ?? metric.Format;
                ctx.Mark(metric.Id, keepLayer: !metricTenant);
                break;
            case RemoveMetric rm:
                var removedMetric = ctx.Config.FindMetric(rm.Metric) ?? throw Missing("metric", rm.Metric, Expression.Suggest(rm.Metric, ctx.Config.Metrics.Select(m => m.Id)));
                if (!IsTenant(ctx, removedMetric.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Metric '{removedMetric.Label}' comes from {ctx.Layer(removedMetric.Id)} and cannot be removed.", rm.Metric, "Remove its tiles from dashboards with node.remove instead.");
                }

                if (MetricUsers(ctx.Config, removedMetric.Id).FirstOrDefault() is { } metricUser)
                {
                    throw new OverlayException("overlay.in_use", $"'{metricUser}' still shows metric '{removedMetric.Label}'.", rm.Metric, $"Remove '{metricUser}' first.");
                }

                ctx.Config.Metrics.Remove(removedMetric);
                ctx.Config.Provenance.Remove(removedMetric.Id);
                break;
            case AddSubscription asub:
                RequireTenantId(asub.Subscription.Id, "subscription");
                Unique(ctx, asub.Subscription.Id, "Subscription");
                ctx.Config.Subscriptions.Add(SpecJson.Clone(asub.Subscription));
                ctx.Mark(asub.Subscription.Id);
                break;
            case UpdateSubscription usub:
                var subscription = ctx.Config.FindSubscription(usub.Subscription) ?? throw Missing("subscription", usub.Subscription, Expression.Suggest(usub.Subscription, ctx.Config.Subscriptions.Select(x => x.Id)));
                var subscriptionTenant = IsTenant(ctx, subscription.Id);
                CheckExpected(ctx, usub.Subscription, SpecJson.ToNode(subscription)!.AsObject(), usub.Expected, SpecJson.ToNode(usub.Set)!.AsObject());
                if (!subscriptionTenant && usub.Set.When is not null)
                {
                    throw new OverlayException("overlay.base_immutable", $"The condition of subscription '{subscription.Label}' comes from {ctx.Layer(subscription.Id)}.", usub.Subscription, "Disable it and add your own subscription instead.");
                }

                subscription.Label = usub.Set.Label ?? subscription.Label;
                subscription.Description = usub.Set.Description ?? subscription.Description;
                subscription.When = usub.Set.When ?? subscription.When;
                subscription.Enabled = usub.Set.Enabled ?? subscription.Enabled;
                ctx.Mark(subscription.Id, keepLayer: !subscriptionTenant);
                break;
            case RemoveSubscription rsub:
                var removedSubscription = ctx.Config.FindSubscription(rsub.Subscription) ?? throw Missing("subscription", rsub.Subscription, Expression.Suggest(rsub.Subscription, ctx.Config.Subscriptions.Select(x => x.Id)));
                RequireRemovable(ctx, removedSubscription.Id, removedSubscription.Label, "subscription",
                    ctx.Config.NotificationRoutes.Where(r => r.Subscription == removedSubscription.Id).Select(r => r.Id)
                        .Concat(ctx.Config.AlertRules.Where(r => r.Trigger == removedSubscription.Id || r.ResolveOn.Contains(removedSubscription.Id)).Select(r => r.Id)));
                ctx.Config.Subscriptions.Remove(removedSubscription);
                ctx.Config.Provenance.Remove(removedSubscription.Id);
                break;
            case AddNotificationTemplate ant:
                RequireTenantId(ant.Template.Id, "notification template");
                Unique(ctx, ant.Template.Id, "Notification template");
                ctx.Config.NotificationTemplates.Add(SpecJson.Clone(ant.Template));
                ctx.Mark(ant.Template.Id);
                break;
            case UpdateNotificationTemplate unt:
                var template = ctx.Config.FindNotificationTemplate(unt.Template) ?? throw Missing("notification template", unt.Template, Expression.Suggest(unt.Template, ctx.Config.NotificationTemplates.Select(x => x.Id)));
                var templateTenant = IsTenant(ctx, template.Id);
                CheckExpected(ctx, unt.Template, SpecJson.ToNode(template)!.AsObject(), unt.Expected, SpecJson.ToNode(unt.Set)!.AsObject());
                template.Label = unt.Set.Label ?? template.Label;
                template.Subject = unt.Set.Subject ?? template.Subject;
                template.Body = unt.Set.Body ?? template.Body;
                template.Enabled = unt.Set.Enabled ?? template.Enabled;
                if (unt.Set.Variants is { } variants)
                {
                    template.Variants = variants.ToDictionary(v => v.Key, v => SpecJson.Clone(v.Value));
                }

                if (unt.Set.ExternalFields is { } external)
                {
                    template.ExternalFields = [.. external];
                }

                ctx.Mark(template.Id, keepLayer: !templateTenant);
                break;
            case RemoveNotificationTemplate rnt:
                var removedTemplate = ctx.Config.FindNotificationTemplate(rnt.Template) ?? throw Missing("notification template", rnt.Template, Expression.Suggest(rnt.Template, ctx.Config.NotificationTemplates.Select(x => x.Id)));
                RequireRemovable(ctx, removedTemplate.Id, removedTemplate.Label ?? removedTemplate.Subject, "notification template", ctx.Config.NotificationRoutes.Where(r => r.Template == removedTemplate.Id).Select(r => r.Id));
                ctx.Config.NotificationTemplates.Remove(removedTemplate);
                ctx.Config.Provenance.Remove(removedTemplate.Id);
                break;
            case AddNotificationProfile anp:
                RequireTenantId(anp.Profile.Id, "notification profile");
                Unique(ctx, anp.Profile.Id, "Notification profile");
                ctx.Config.NotificationProfiles.Add(SpecJson.Clone(anp.Profile));
                ctx.Mark(anp.Profile.Id);
                break;
            case UpdateNotificationProfile unp:
                var profile = ctx.Config.FindNotificationProfile(unp.Profile) ?? throw Missing("notification profile", unp.Profile, Expression.Suggest(unp.Profile, ctx.Config.NotificationProfiles.Select(x => x.Id)));
                var profileTenant = IsTenant(ctx, profile.Id);
                CheckExpected(ctx, unp.Profile, SpecJson.ToNode(profile)!.AsObject(), unp.Expected, SpecJson.ToNode(unp.Set)!.AsObject());
                profile.Label = unp.Set.Label ?? profile.Label;
                profile.Description = unp.Set.Description ?? profile.Description;
                profile.Channels = unp.Set.Channels is { } channels ? [.. channels] : profile.Channels;
                profile.Delivery = unp.Set.Delivery ?? profile.Delivery;
                profile.MinSeverity = unp.Set.MinSeverity ?? profile.MinSeverity;
                profile.Enabled = unp.Set.Enabled ?? profile.Enabled;
                ctx.Mark(profile.Id, keepLayer: !profileTenant);
                break;
            case RemoveNotificationProfile rnp:
                var removedProfile = ctx.Config.FindNotificationProfile(rnp.Profile) ?? throw Missing("notification profile", rnp.Profile, Expression.Suggest(rnp.Profile, ctx.Config.NotificationProfiles.Select(x => x.Id)));
                RequireRemovable(ctx, removedProfile.Id, removedProfile.Label, "notification profile", ctx.Config.NotificationRoutes.Where(r => r.Profile == removedProfile.Id).Select(r => r.Id));
                ctx.Config.NotificationProfiles.Remove(removedProfile);
                ctx.Config.Provenance.Remove(removedProfile.Id);
                break;
            case AddNotificationRoute anr:
                RequireTenantId(anr.Route.Id, "notification route");
                Unique(ctx, anr.Route.Id, "Notification route");
                ctx.Config.NotificationRoutes.Add(SpecJson.Clone(anr.Route));
                ctx.Mark(anr.Route.Id);
                break;
            case UpdateNotificationRoute unr:
                var route = ctx.Config.FindNotificationRoute(unr.Route) ?? throw Missing("notification route", unr.Route, Expression.Suggest(unr.Route, ctx.Config.NotificationRoutes.Select(x => x.Id)));
                var routeTenant = IsTenant(ctx, route.Id);
                CheckExpected(ctx, unr.Route, SpecJson.ToNode(route)!.AsObject(), unr.Expected, SpecJson.ToNode(unr.Set)!.AsObject());
                if (!routeTenant && (unr.Set.Recipients is not null || unr.Set.Mandatory is not null || (route.Mandatory && unr.Set.Enabled == false)))
                {
                    throw new OverlayException("overlay.base_immutable", $"Route '{route.Label}' comes from {ctx.Layer(route.Id)}: its recipients and whether it is mandatory cannot change{(route.Mandatory ? ", and it cannot be turned off" : string.Empty)}.", unr.Route, "Add your own route for other recipients.");
                }

                if (route.Mandatory && unr.Set.Enabled == false && unr.Set.Mandatory != false)
                {
                    throw new OverlayException("overlay.mandatory", $"Route '{route.Label}' is mandatory and cannot be turned off.", unr.Route, "Make it optional first (a permission change), then turn it off.");
                }

                route.Label = unr.Set.Label ?? route.Label;
                route.Profile = unr.Set.Profile ?? route.Profile;
                route.Template = unr.Set.Template ?? route.Template;
                route.Recipients = unr.Set.Recipients is { } recipients ? recipients.Select(SpecJson.Clone).ToList() : route.Recipients;
                route.Mandatory = unr.Set.Mandatory ?? route.Mandatory;
                route.Enabled = unr.Set.Enabled ?? route.Enabled;
                ctx.Mark(route.Id, keepLayer: !routeTenant);
                break;
            case RemoveNotificationRoute rnr:
                var removedRoute = ctx.Config.FindNotificationRoute(rnr.Route) ?? throw Missing("notification route", rnr.Route, Expression.Suggest(rnr.Route, ctx.Config.NotificationRoutes.Select(x => x.Id)));
                if (removedRoute.Mandatory)
                {
                    throw new OverlayException("overlay.mandatory", $"Route '{removedRoute.Label}' is mandatory.", rnr.Route, "Make it optional first (a permission change), then remove it.");
                }

                RequireRemovable(ctx, removedRoute.Id, removedRoute.Label, "notification route", []);
                ctx.Config.NotificationRoutes.Remove(removedRoute);
                ctx.Config.Provenance.Remove(removedRoute.Id);
                break;
            case AddAlertRule aar:
                RequireTenantId(aar.Rule.Id, "alert rule");
                Unique(ctx, aar.Rule.Id, "Alert rule");
                ctx.Config.AlertRules.Add(SpecJson.Clone(aar.Rule));
                ctx.Mark(aar.Rule.Id);
                break;
            case UpdateAlertRule uar:
                var alertRule = ctx.Config.FindAlertRule(uar.Rule) ?? throw Missing("alert rule", uar.Rule, Expression.Suggest(uar.Rule, ctx.Config.AlertRules.Select(x => x.Id)));
                var alertRuleTenant = IsTenant(ctx, alertRule.Id);
                CheckExpected(ctx, uar.Rule, SpecJson.ToNode(alertRule)!.AsObject(), uar.Expected, SpecJson.ToNode(uar.Set)!.AsObject());
                if (!alertRuleTenant && (uar.Set.Trigger is not null || uar.Set.DedupKey is not null))
                {
                    throw new OverlayException("overlay.base_immutable", $"The trigger and key of alert rule '{alertRule.Label}' come from {ctx.Layer(alertRule.Id)}.", uar.Rule, "Disable it and add your own rule instead.");
                }

                alertRule.Label = uar.Set.Label ?? alertRule.Label;
                alertRule.Description = uar.Set.Description ?? alertRule.Description;
                alertRule.Trigger = uar.Set.Trigger ?? alertRule.Trigger;
                alertRule.MinSeverity = uar.Set.MinSeverity ?? alertRule.MinSeverity;
                alertRule.Severity = uar.Set.Severity ?? alertRule.Severity;
                alertRule.DedupKey = uar.Set.DedupKey ?? alertRule.DedupKey;
                alertRule.Title = uar.Set.Title ?? alertRule.Title;
                alertRule.Owner = uar.Set.Owner is { } owner ? SpecJson.Clone(owner) : alertRule.Owner;
                alertRule.Escalation = uar.Set.Escalation is { } steps ? steps.Select(SpecJson.Clone).ToList() : alertRule.Escalation;
                alertRule.ResolveOn = uar.Set.ResolveOn is { } resolveOn ? [.. resolveOn] : alertRule.ResolveOn;
                alertRule.ReopenWithinMinutes = uar.Set.ReopenWithinMinutes ?? alertRule.ReopenWithinMinutes;
                alertRule.Enabled = uar.Set.Enabled ?? alertRule.Enabled;
                ctx.Mark(alertRule.Id, keepLayer: !alertRuleTenant);
                break;
            case RemoveAlertRule rar:
                var removedAlertRule = ctx.Config.FindAlertRule(rar.Rule) ?? throw Missing("alert rule", rar.Rule, Expression.Suggest(rar.Rule, ctx.Config.AlertRules.Select(x => x.Id)));
                RequireRemovable(ctx, removedAlertRule.Id, removedAlertRule.Label, "alert rule", []);
                ctx.Config.AlertRules.Remove(removedAlertRule);
                ctx.Config.Provenance.Remove(removedAlertRule.Id);
                break;
            case AddWorkflowState aws:
                ApplyAddWorkflowState(ctx, aws);
                break;
            case AddTransition at:
                ApplyAddTransition(ctx, at);
                break;
            case UpdateTransition ut:
                ApplyUpdateTransition(ctx, ut);
                break;
            case SetActiveInstancePolicy sap:
                var flow = RequireWorkflow(ctx.Config, sap.Workflow);
                flow.ActiveInstances = SpecJson.Clone(sap.Policy);
                ctx.Mark($"{flow.Id}/active_instances");
                break;
            default:
                throw new OverlayException("overlay.unsupported", $"Operation {op.GetType().Name} is not supported.");
        }
    }

    private static void ApplyAddField(OpContext ctx, AddField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        if (!op.Field.Key.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
        {
            throw new OverlayException(
                "overlay.namespace",
                $"Tenant fields must use the '{Namespaces.TenantPrefix}' prefix; '{op.Field.Key}' does not.",
                $"{op.Entity}/{op.Field.Key}",
                Namespaces.TenantKeyFromLabel(op.Field.Label));
        }

        if (!Namespaces.IsValidFieldKey(op.Field.Key))
        {
            throw new OverlayException("overlay.invalid_key", $"'{op.Field.Key}' is not a valid field key (lowercase letters, digits, underscores; starts with a letter).", op.Field.Key, Namespaces.TenantKeyFromLabel(op.Field.Label));
        }

        if (entity.FindField(op.Field.Key) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"{entity.Label} already has a field '{op.Field.Key}'.", $"{op.Entity}/{op.Field.Key}", "Choose another key or update the existing field.");
        }

        if (op.Field.System)
        {
            throw new OverlayException("overlay.system_field", "Tenant fields cannot be system fields.", $"{op.Entity}/{op.Field.Key}");
        }

        entity.Fields.Add(SpecJson.Clone(op.Field));
        ctx.Mark($"{op.Entity}/{op.Field.Key}");
    }

    private static void ApplyUpdateField(OpContext ctx, UpdateField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        var field = entity.FindField(op.Field) ?? throw MissingField(entity, op.Field);
        var target = $"{op.Entity}/{op.Field}";
        var isTenant = IsTenant(ctx, target);
        var current = SpecJson.ToNode(field)!.AsObject();
        var patch = SpecJson.ToNode(op.Set)!.AsObject();
        if (patch.Count == 0)
        {
            throw new OverlayException("overlay.empty_patch", "Nothing to change: the update sets no properties.", target);
        }

        CheckExpected(ctx, target, current, op.Expected, patch);

        if (!isTenant)
        {
            if (op.Set.Required == false && field.Required)
            {
                throw new OverlayException("overlay.loosen_base", $"'{field.Label}' is required by {ctx.Layer(target)}; tenants can tighten base rules but not remove them.", target, "Hide it from a specific form with a form-scoped rule, or keep it required.");
            }

            if (op.Set.MaxLength is { } ml && field.MaxLength is { } baseMl && ml > baseMl)
            {
                throw new OverlayException("overlay.loosen_base", $"'{field.Label}' allows at most {baseMl} characters in {ctx.Layer(target)}; an overlay cannot raise the limit.", target, $"Use maxLength ≤ {baseMl}.");
            }

            if ((op.Set.Min is not null && field.Min is { } bmin && op.Set.Min < bmin) || (op.Set.Max is not null && field.Max is { } bmax && op.Set.Max > bmax))
            {
                throw new OverlayException("overlay.loosen_base", $"An overlay can only narrow the range of '{field.Label}'.", target);
            }
        }

        var allSame = true;
        foreach (var (key, value) in patch)
        {
            if (!JsonNode.DeepEquals(current[key], value))
            {
                allSame = false;
            }
        }

        if (allSame)
        {
            ctx.Redundant($"'{field.Label}' already has these values.", target);
            return;
        }

        var merged = current.DeepClone().AsObject();
        foreach (var (key, value) in patch)
        {
            merged[key] = value?.DeepClone();
        }

        var updated = SpecJson.Deserialize<FieldSpec>(merged.ToJsonString());
        entity.Fields[entity.Fields.IndexOf(field)] = updated;
        ctx.Mark(target, keepLayer: !isTenant);
    }

    private static void ApplyHideField(OpContext ctx, HideField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        var field = entity.FindField(op.Field) ?? throw MissingField(entity, op.Field);
        var target = $"{op.Entity}/{op.Field}";
        if (op.Hidden && entity.TitleField == field.Key)
        {
            throw new OverlayException("overlay.contract_field", $"'{field.Label}' names every {entity.Label} and cannot be hidden.", target, "Relabel it instead.");
        }

        if (op.Hidden && ctx.Config.WorkflowFor(entity.Id)?.StateField == field.Key)
        {
            throw new OverlayException("overlay.contract_field", $"'{field.Label}' drives the workflow and cannot be hidden.", target);
        }

        if (field.Hidden == op.Hidden)
        {
            ctx.Redundant($"'{field.Label}' is already {(op.Hidden ? "hidden" : "visible")}.", target);
            return;
        }

        field.Hidden = op.Hidden;
        ctx.Mark(target, keepLayer: !IsTenant(ctx, target));
    }

    private static void ApplyRemoveField(OpContext ctx, RemoveField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        var field = entity.FindField(op.Field) ?? throw MissingField(entity, op.Field);
        var target = $"{op.Entity}/{op.Field}";
        if (!IsTenant(ctx, target))
        {
            throw new OverlayException("overlay.base_immutable", $"'{field.Label}' comes from {ctx.Layer(target)} and cannot be removed.", target, "Hide it instead (hide_field).");
        }

        var dependents = DependencyGraph.Build(ctx.Config).DependentsOf($"{entity.Id}/{field.Key}").ToList();
        if (dependents.Count > 0)
        {
            throw new OverlayException(
                "overlay.has_dependents",
                $"'{field.Label}' is still used by: {string.Join(", ", dependents)}.",
                target,
                "Remove or change those first, then remove the field. Its stored values are kept until a separate data purge.");
        }

        entity.Fields.Remove(field);
        ctx.Config.Provenance.Remove(target);
    }

    private static void ApplyAddChoiceOption(OpContext ctx, AddChoiceOption op)
    {
        var list = ctx.Config.FindChoiceList(op.ChoiceList) ?? throw Missing("choice list", op.ChoiceList);
        var target = $"{op.ChoiceList}/{op.Option.Id}";
        if (!IsTenant(ctx, list.Id) && !op.Option.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
        {
            throw new OverlayException("overlay.namespace", $"Options added to {list.Label} must use the '{Namespaces.TenantPrefix}' prefix.", target, Namespaces.TenantKeyFromLabel(op.Option.Label));
        }

        if (list.Find(op.Option.Id) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"{list.Label} already has option '{op.Option.Id}'.", target);
        }

        var index = ResolveAnchor(ctx, list.Options.Select(o => o.Id).ToList(), op.Anchor, target);
        list.Options.Insert(index, SpecJson.Clone(op.Option));
        ctx.Mark(target);
    }

    private static void ApplyUpdateChoiceOption(OpContext ctx, UpdateChoiceOption op)
    {
        var list = ctx.Config.FindChoiceList(op.ChoiceList) ?? throw Missing("choice list", op.ChoiceList);
        var option = list.Find(op.Option) ?? throw Missing("option", $"{op.ChoiceList}/{op.Option}", Expression.Suggest(op.Option, list.Options.Select(o => o.Id)));
        var target = $"{op.ChoiceList}/{op.Option}";
        var patch = new JsonObject();
        if (op.Label is not null)
        {
            patch["label"] = op.Label;
        }

        if (op.Hidden is not null)
        {
            patch["hidden"] = op.Hidden;
        }

        CheckExpected(ctx, target, SpecJson.ToNode(option)!.AsObject(), op.Expected, patch);
        option.Label = op.Label ?? option.Label;
        option.Hidden = op.Hidden ?? option.Hidden;
        ctx.Mark(target, keepLayer: !IsTenant(ctx, target));
    }

    private static void ApplyUpdateRule(OpContext ctx, UpdateRule op)
    {
        var rule = ctx.Config.FindRule(op.Rule) ?? throw Missing("rule", op.Rule, Expression.Suggest(op.Rule, ctx.Config.Rules.Select(r => r.Id)));
        if (op.ExpectedHash is not null)
        {
            var currentHash = Hash(SpecJson.Canonical(SpecJson.ToNode(rule)));
            if (currentHash != op.ExpectedHash)
            {
                ctx.Conflict($"Rule '{op.Rule}' changed since this edit was made.", op.Rule, "Review the current rule and re-apply the change.");
            }
        }

        if (op.Replacement is not null)
        {
            if (!IsTenant(ctx, rule.Id))
            {
                throw new OverlayException("overlay.base_immutable", $"Rule '{op.Rule}' comes from {ctx.Layer(rule.Id)}; tenants can only enable or disable it.", op.Rule, "Disable it and add your own rule.");
            }

            if (op.Replacement.Id != rule.Id)
            {
                throw new OverlayException("overlay.id_change", "A rule's ID cannot change.", op.Rule);
            }

            ctx.Config.Rules[ctx.Config.Rules.IndexOf(rule)] = SpecJson.Clone(op.Replacement);
        }

        if (op.Enabled is { } enabled)
        {
            (ctx.Config.FindRule(op.Rule) ?? rule).Enabled = enabled;
        }

        ctx.Mark(op.Rule, keepLayer: !IsTenant(ctx, op.Rule));
    }

    private static void ApplyInsertNode(OpContext ctx, InsertNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var parent = view.Layout.Find(op.Parent) ?? throw MissingNode(view, op.Parent);
        foreach (var node in op.Node.Descendants())
        {
            if (!node.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
            {
                throw new OverlayException("overlay.namespace", $"New node IDs must start with '{Namespaces.TenantPrefix}'; '{node.Id}' does not.", node.Id, Namespaces.NewTenantNodeId());
            }

            if (view.Layout.Find(node.Id) is not null)
            {
                throw new OverlayException("overlay.duplicate", $"Node '{node.Id}' already exists in {view.Label}.", node.Id, Namespaces.NewTenantNodeId());
            }
        }

        var index = ResolveAnchor(ctx, parent.Children.Select(c => c.Id).ToList(), op.Anchor, op.Node.Id);
        parent.Children.Insert(index, SpecJson.Clone(op.Node));
        foreach (var node in op.Node.Descendants())
        {
            ctx.Mark(node.Id);
        }
    }

    private static void ApplyUpdateNode(OpContext ctx, UpdateNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var node = view.Layout.Find(op.Node) ?? throw MissingNode(view, op.Node);
        CheckExpected(ctx, op.Node, node.Props, op.Expected, op.Props);
        foreach (var (key, value) in op.Props)
        {
            if (value is null)
            {
                node.Props.Remove(key);
            }
            else
            {
                node.Props[key] = value.DeepClone();
            }
        }

        ctx.Mark(op.Node, keepLayer: !IsTenant(ctx, op.Node));
    }

    private static void ApplyMoveNode(OpContext ctx, MoveNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var node = view.Layout.Find(op.Node) ?? throw MissingNode(view, op.Node);
        var newParent = view.Layout.Find(op.Parent) ?? throw MissingNode(view, op.Parent);
        if (node.Find(op.Parent) is not null)
        {
            throw new OverlayException("overlay.cycle", $"Cannot move '{op.Node}' into itself or one of its children.", op.Node);
        }

        var oldParent = view.Layout.ParentOf(op.Node) ?? throw new OverlayException("overlay.root", "The page root cannot be moved.", op.Node);
        oldParent.Children.Remove(node);
        var index = ResolveAnchor(ctx, newParent.Children.Select(c => c.Id).ToList(), op.Anchor, op.Node);
        newParent.Children.Insert(index, node);
        ctx.Mark(op.Node, keepLayer: !IsTenant(ctx, op.Node));
    }

    private static void ApplyRemoveNode(OpContext ctx, RemoveNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var node = view.Layout.Find(op.Node) ?? throw MissingNode(view, op.Node);
        var parent = view.Layout.ParentOf(op.Node) ?? throw new OverlayException("overlay.root", "The page root cannot be removed.", op.Node);
        if (IsTenant(ctx, node.Id) && node.Descendants().All(n => IsTenant(ctx, n.Id)))
        {
            parent.Children.Remove(node);
            foreach (var n in node.Descendants())
            {
                ctx.Config.Provenance.Remove(n.Id);
            }
        }
        else
        {
            node.Hidden = true;
            ctx.Mark(op.Node, keepLayer: true);
        }
    }

    private static void ApplySetFieldAccess(OpContext ctx, SetFieldAccess op)
    {
        var set = ctx.Config.PermissionSets.Find(p => p.Id == op.PermissionSet) ?? throw Missing("permission set", op.PermissionSet, Expression.Suggest(op.PermissionSet, ctx.Config.PermissionSets.Select(p => p.Id)));
        var entity = RequireEntity(ctx.Config, op.Entity);
        if (op.Field != "*" && entity.FindField(op.Field) is null)
        {
            throw MissingField(entity, op.Field);
        }

        if (op.Access is not (FieldAccessLevels.Hidden or FieldAccessLevels.Read or FieldAccessLevels.Write))
        {
            throw new OverlayException("overlay.invalid_value", $"Access must be hidden, read, or write (got '{op.Access}').", op.PermissionSet);
        }

        var existing = set.Fields.Find(f => f.Entity == op.Entity && f.Field == op.Field);
        if (existing is not null)
        {
            existing.Access = op.Access;
        }
        else
        {
            set.Fields.Add(new FieldPermissionSpec { Entity = op.Entity, Field = op.Field, Access = op.Access });
        }

        ctx.Mark(op.PermissionSet, keepLayer: !IsTenant(ctx, op.PermissionSet));
    }

    private static void ApplyUpdateCatalogItem(OpContext ctx, UpdateCatalogItem op)
    {
        var item = ctx.Config.FindCatalogItem(op.Item) ?? throw Missing("catalog item", op.Item, Expression.Suggest(op.Item, ctx.Config.CatalogItems.Select(c => c.Id)));
        var tenant = IsTenant(ctx, item.Id);
        if (!tenant && (op.Set.Variables is not null || op.Set.Tasks is not null))
        {
            throw new OverlayException(
                "overlay.base_immutable",
                $"The questions and tasks of '{item.Label}' come from {ctx.Layer(item.Id)} and cannot be replaced.",
                op.Item,
                "Deactivate it and add your own catalog item with add_catalog_item.");
        }

        var patch = SpecJson.ToNode(op.Set)!.AsObject();
        patch.Remove("variables");
        patch.Remove("tasks");
        CheckExpected(ctx, op.Item, SpecJson.ToNode(item)!.AsObject(), op.Expected, patch);
        item.Label = op.Set.Label ?? item.Label;
        item.Description = op.Set.Description ?? item.Description;
        item.Category = op.Set.Category ?? item.Category;
        item.Icon = op.Set.Icon ?? item.Icon;
        item.Active = op.Set.Active ?? item.Active;
        item.Order = op.Set.Order ?? item.Order;
        if (op.Set.ApprovalPolicy is { } approval)
        {
            if (approval.Length > 0)
            {
                RequireApprovalPolicy(ctx.Config, approval);
            }

            item.ApprovalPolicy = approval.Length == 0 ? null : approval;
        }

        if (op.Set.Variables is not null)
        {
            item.Variables = op.Set.Variables.Select(SpecJson.Clone).ToList();
        }

        if (op.Set.Tasks is not null)
        {
            item.Tasks = op.Set.Tasks.Select(SpecJson.Clone).ToList();
        }

        ctx.Mark(item.Id, keepLayer: !tenant);
    }

    private static ApprovalPolicySpec RequireApprovalPolicy(EffectiveConfig config, string id) =>
        config.FindApprovalPolicy(id) ?? throw Missing("approval policy", id, Expression.Suggest(id, config.ApprovalPolicies.Select(p => p.Id)));

    private static void ApplyUpdateApprovalPolicy(OpContext ctx, UpdateApprovalPolicy op)
    {
        var policy = RequireApprovalPolicy(ctx.Config, op.Policy);
        var tenant = IsTenant(ctx, policy.Id);
        if (!tenant && op.Set.Stages is not null)
        {
            throw new OverlayException(
                "overlay.base_immutable",
                $"The stages of '{policy.Label}' come from {ctx.Layer(policy.Id)} and cannot be replaced.",
                op.Policy,
                "Add your own policy with add_approval_policy and point the catalog item at it.");
        }

        var patch = SpecJson.ToNode(op.Set)!.AsObject();
        patch.Remove("stages");
        CheckExpected(ctx, op.Policy, SpecJson.ToNode(policy)!.AsObject(), op.Expected, patch);
        policy.Label = op.Set.Label ?? policy.Label;
        policy.Description = op.Set.Description ?? policy.Description;
        if (op.Set.Stages is not null)
        {
            policy.Stages = op.Set.Stages.Select(SpecJson.Clone).ToList();
        }

        ctx.Mark(policy.Id, keepLayer: !tenant);
    }

    /// <summary>Layout nodes and saved views that reference a metric.</summary>
    private static void Unique(OpContext ctx, string id, string kind)
    {
        if (ctx.Config.Provenance.ContainsKey(id))
        {
            throw new OverlayException("overlay.duplicate", $"{kind} '{id}' already exists.", id, "Choose a new ID.");
        }
    }

    /// <summary>Only tenant objects that nothing uses may be removed; module objects are disabled instead.</summary>
    private static void RequireRemovable(OpContext ctx, string id, string label, string kind, IEnumerable<string> users)
    {
        if (!IsTenant(ctx, id))
        {
            throw new OverlayException("overlay.base_immutable", $"The {kind} '{label}' comes from {ctx.Layer(id)} and cannot be removed.", id, "Disable it instead (enabled: false).");
        }

        if (users.FirstOrDefault() is { } user)
        {
            throw new OverlayException("overlay.in_use", $"'{user}' still uses the {kind} '{label}'.", id, $"Remove or change '{user}' first.");
        }
    }

    public static IEnumerable<string> MetricUsers(SpecCatalog config, string metricId) =>
        config.Views.SelectMany(v => v.Layout.Descendants()).Where(n =>
            (n.Props.TryGetPropertyValue("metric", out var m) && m?.GetValueKind() == System.Text.Json.JsonValueKind.String && m.GetValue<string>() == metricId)
            || (n.Props.TryGetPropertyValue("views", out var views) && views is JsonArray list && list.Any(i => i?["metric"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && i["metric"]!.GetValue<string>() == metricId)))
            .Select(n => n.Id);

    private static void ApplyUpdateBusinessCalendar(OpContext ctx, UpdateBusinessCalendar op)
    {
        var calendar = ctx.Config.FindBusinessCalendar(op.Calendar) ?? throw Missing("business calendar", op.Calendar, Expression.Suggest(op.Calendar, ctx.Config.BusinessCalendars.Select(c => c.Id)));
        var tenant = IsTenant(ctx, calendar.Id);
        CheckExpected(ctx, op.Calendar, SpecJson.ToNode(calendar)!.AsObject(), op.Expected, SpecJson.ToNode(op.Set)!.AsObject());
        calendar.Label = op.Set.Label ?? calendar.Label;
        calendar.TimeZone = op.Set.TimeZone ?? calendar.TimeZone;
        calendar.Week = op.Set.Week?.Select(SpecJson.Clone).ToList() ?? calendar.Week;
        calendar.Holidays = op.Set.Holidays?.Select(SpecJson.Clone).ToList() ?? calendar.Holidays;
        ctx.Mark(calendar.Id, keepLayer: !tenant);
    }

    private static void ApplyAddWorkflowState(OpContext ctx, AddWorkflowState op)
    {
        var wf = RequireWorkflow(ctx.Config, op.Workflow);
        RequireTenantId(op.State.Id, "state");
        if (wf.FindState(op.State.Id) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"Workflow '{wf.Id}' already has a state '{op.State.Id}'.", $"{wf.Id}/state.{op.State.Id}");
        }

        var entity = RequireEntity(ctx.Config, wf.Entity);
        var list = entity.FindField(wf.StateField)?.ChoiceList is { } listId ? ctx.Config.FindChoiceList(listId) : null;
        if (list is null)
        {
            throw new OverlayException("overlay.not_found", $"Workflow '{wf.Id}' has no status choice list to add the state to.", wf.Id);
        }

        wf.States.Add(SpecJson.Clone(op.State));
        if (list.Find(op.State.Id) is null)
        {
            var siblings = list.Options.Select(o => o.Id).ToList();
            list.Options.Insert(ResolveAnchor(ctx, siblings, op.Anchor, op.State.Id), new ChoiceOption { Id = op.State.Id, Label = op.Label, Tone = op.Tone });
            ctx.Mark($"{list.Id}/{op.State.Id}");
        }

        ctx.Mark($"{wf.Id}/state.{op.State.Id}");
    }

    private static void ApplyAddTransition(OpContext ctx, AddTransition op)
    {
        var wf = RequireWorkflow(ctx.Config, op.Workflow);
        RequireTenantId(op.Transition.Id, "transition");
        if (wf.FindTransition(op.Transition.Id) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"Workflow '{wf.Id}' already has a transition '{op.Transition.Id}'.", $"{wf.Id}/{op.Transition.Id}", "Use update_transition to change it.");
        }

        if (op.Transition.System)
        {
            throw new OverlayException("overlay.system_transition", "Only modules define system transitions.", $"{wf.Id}/{op.Transition.Id}");
        }

        wf.Transitions.Add(SpecJson.Clone(op.Transition));
        ctx.Mark($"{wf.Id}/{op.Transition.Id}");
    }

    private static void ApplyUpdateTransition(OpContext ctx, UpdateTransition op)
    {
        var wf = RequireWorkflow(ctx.Config, op.Workflow);
        var target = $"{wf.Id}/{op.Transition}";
        var t = wf.FindTransition(op.Transition) ?? throw Missing("transition", target, Expression.Suggest(op.Transition, wf.Transitions.Select(x => x.Id)));
        var tenant = IsTenant(ctx, target);
        var p = op.Set;
        if (!tenant && (p.From is not null || p.To is not null || p.Guard is not null || p.RequiredFields is not null || p.Roles is not null || p.Set is not null))
        {
            throw new OverlayException(
                "overlay.base_immutable",
                $"Transition '{t.Label}' comes from {ctx.Layer(target)}; tenants can change only its label, guard message, tone, whether it is enabled, and add events it publishes.",
                target,
                "Disable it and add your own transition with add_transition.");
        }

        // Subscribers rely on the events a module publishes: tenants may add emits to a module transition, never drop its own.
        // (Element reset restores the module's list.)
        if (!tenant && p.Emits is not null && t.Emits.Select(e => e.Type).Where(type => !Namespaces.IsTenantOwned(type)).Except(p.Emits.Select(e => e.Type), StringComparer.Ordinal).FirstOrDefault() is { } dropped)
        {
            throw new OverlayException("overlay.base_immutable", $"Transition '{t.Label}' publishes '{dropped}' for {ctx.Layer(target)}; tenants can add events but not remove the module's.", target, "Keep the module's events and add yours.");
        }

        if (t.System && p.Enabled == false)
        {
            throw new OverlayException("overlay.system_transition", $"'{t.Label}' is used by the platform and cannot be disabled.", target);
        }

        CheckExpected(ctx, target, SpecJson.ToNode(t)!.AsObject(), op.Expected, SpecJson.ToNode(p)!.AsObject());
        t.Label = p.Label ?? t.Label;
        t.From = p.From ?? t.From;
        t.To = p.To ?? t.To;
        t.Guard = p.Guard ?? t.Guard;
        t.GuardMessage = p.GuardMessage ?? t.GuardMessage;
        t.RequiredFields = p.RequiredFields ?? t.RequiredFields;
        t.Roles = p.Roles ?? t.Roles;
        t.Set = p.Set ?? t.Set;
        t.Tone = p.Tone ?? t.Tone;
        t.Enabled = p.Enabled ?? t.Enabled;
        t.Emits = p.Emits?.Select(SpecJson.Clone).ToList() ?? t.Emits;
        ctx.Mark(target, keepLayer: !tenant);
    }

    private static WorkflowSpec RequireWorkflow(EffectiveConfig config, string id) =>
        config.Workflows.Find(w => w.Id == id) ?? throw Missing("workflow", id, Expression.Suggest(id, config.Workflows.Select(w => w.Id)));

    private static void CheckExpected(OpContext ctx, string target, JsonObject current, JsonObject? expected, JsonObject patch)
    {
        if (expected is null)
        {
            return;
        }

        foreach (var (key, expectedValue) in expected)
        {
            var actual = current[key];
            if (JsonNode.DeepEquals(actual, expectedValue) || (actual is null && expectedValue is null))
            {
                continue;
            }

            // Base now equals what the overlay wanted: compatible, nothing to do for this property.
            if (patch.TryGetPropertyValue(key, out var wanted) && JsonNode.DeepEquals(actual, wanted))
            {
                continue;
            }

            ctx.Conflict(
                $"{target}: '{key}' was {expectedValue?.ToJsonString() ?? "unset"} when this change was made but is now {actual?.ToJsonString() ?? "unset"} ({ctx.Layer(target)}).",
                target,
                $"Keep your value ({patch[key]?.ToJsonString() ?? "unset"}) or accept the new default ({actual?.ToJsonString() ?? "unset"}).");
        }
    }

    private static int ResolveAnchor(OpContext ctx, List<string> siblings, Anchor? anchor, string target)
    {
        if (anchor is null || (anchor.Before is null && anchor.After is null))
        {
            return anchor?.Position == "start" ? 0 : siblings.Count;
        }

        var reference = anchor.Before ?? anchor.After!;
        var index = siblings.IndexOf(reference);
        if (index < 0)
        {
            ctx.Warn("overlay.anchor_missing", $"'{reference}' is no longer next to where '{target}' was placed; it was placed at the end instead.", target);
            return siblings.Count;
        }

        return anchor.Before is not null ? index : index + 1;
    }

    private static bool IsTenant(OpContext ctx, string key) =>
        !ctx.Config.Provenance.TryGetValue(key, out var layer) || layer == TenantLayer;

    private static void RequireTenantId(string id, string kind)
    {
        if (!id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
        {
            throw new OverlayException("overlay.namespace", $"New {kind} IDs must start with '{Namespaces.TenantPrefix}'; '{id}' does not.", id, Namespaces.TenantPrefix + id.Split('.')[^1]);
        }
    }

    private static EntitySpec RequireEntity(EffectiveConfig config, string id) =>
        config.FindEntity(id) ?? throw Missing("entity", id, Expression.Suggest(id, config.Entities.Select(e => e.Id)));

    private static ViewSpec RequireView(EffectiveConfig config, string id) =>
        config.FindView(id) ?? throw Missing("view", id, Expression.Suggest(id, config.Views.Select(v => v.Id)));

    private static OverlayException Missing(string kind, string id, string? suggestion = null) =>
        new("overlay.not_found", $"No {kind} '{id}' exists." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), id, suggestion);

    private static OverlayException MissingField(EntitySpec entity, string key)
    {
        var suggestion = Expression.Suggest(key, entity.Fields.Select(f => f.Key));
        return new OverlayException(
            "overlay.not_found",
            $"{entity.Label} has no field '{key}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"),
            $"{entity.Id}/{key}",
            suggestion);
    }

    private static OverlayException MissingNode(ViewSpec view, string id)
    {
        var suggestion = Expression.Suggest(id, view.Layout.Descendants().Select(n => n.Id));
        return new OverlayException("overlay.not_found", $"{view.Label} has no node '{id}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), id, suggestion);
    }

    private sealed class OpContext(EffectiveConfig config, int index)
    {
        private readonly List<Diagnostic> _diagnostics = [];

        public EffectiveConfig Config { get; } = config;

        public string Status { get; private set; } = OpStatus.Applied;

        /// <summary>For element.reset: whether it undid at least one earlier operation.</summary>
        public bool ResetSomething { get; init; }

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        public string Layer(string key) => Config.Provenance.GetValueOrDefault(key, TenantLayer);

        public void Mark(string key, bool keepLayer = false)
        {
            if (!Config.Customized.Contains(key))
            {
                Config.Customized.Add(key);
            }

            if (!keepLayer)
            {
                Config.Provenance[key] = TenantLayer;
            }
        }

        public void Warn(string code, string message, string? target) =>
            _diagnostics.Add(new Diagnostic(Severity.Warning, code, message, target, null, index));

        public void Redundant(string message, string target)
        {
            Status = OpStatus.Redundant;
            _diagnostics.Add(new Diagnostic(Severity.Warning, "overlay.redundant", message, target, null, index));
        }

        public void Conflict(string message, string target, string? suggestion)
        {
            Status = OpStatus.Conflict;
            _diagnostics.Add(new Diagnostic(Severity.Error, "overlay.conflict", message, target, suggestion, index));
        }

        public void Fail(string code, string message, string? target, string? suggestion)
        {
            Status = OpStatus.Failed;
            _diagnostics.Add(new Diagnostic(Severity.Error, code, message, target, suggestion, index));
        }
    }
}

public sealed class OverlayException : Exception
{
    public OverlayException(string code, string message, string? target = null, string? suggestion = null)
        : base(message)
    {
        Code = code;
        Target = target;
        Suggestion = suggestion;
    }

    public OverlayException()
        : this("overlay.error", "Overlay error")
    {
    }

    public OverlayException(string message)
        : this("overlay.error", message)
    {
    }

    public OverlayException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "overlay.error";
    }

    public string Code { get; }

    public string? Target { get; }

    public string? Suggestion { get; }
}
