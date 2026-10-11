using Esmbler.Kernel.Alerts;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-ALR-001: alert rules as validated configuration and the alert lifecycle (ADR 0022).</summary>
public class AlertSpecTests
{
    private const string Ticket = "core.tickets.ticket";

    private static SubscriptionSpec Escalated(string id = "tenant__sub_escalated") =>
        new() { Id = id, Label = "Escalated", Event = "core.tickets.ticket.escalated", Target = SubscriptionTargets.Alert };

    private static SubscriptionSpec Closed() =>
        new() { Id = "tenant__sub_closed", Label = "Closed", Event = "record.transitioned", Entity = Ticket, When = "record.status in ['resolved', 'closed']", Target = SubscriptionTargets.Alert };

    private static AlertRuleSpec Rule(Action<AlertRuleSpec>? change = null)
    {
        var rule = new AlertRuleSpec
        {
            Id = "tenant__critical_escalation",
            Label = "Critical escalation",
            Trigger = "tenant__sub_escalated",
            MinSeverity = EventSeverities.Critical,
            Title = "Escalated: {{record.number}} {{record.title}} ({{event.reason}})",
            Owner = new AlertOwnerSpec { Kind = AlertOwnerKinds.Group, Ref = "network" },
            Escalation = [new() { AfterMinutes = 15 }, new() { AfterMinutes = 60, RepeatEveryMinutes = 60 }],
            ResolveOn = ["tenant__sub_closed"],
        };
        change?.Invoke(rule);
        return rule;
    }

    private static OverlayOp[] Setup(AlertRuleSpec? rule = null, SubscriptionSpec? trigger = null) =>
    [
        new AddSubscription { Subscription = trigger ?? Escalated() },
        new AddSubscription { Subscription = Closed() },
        new AddAlertRule { Rule = rule ?? Rule() },
    ];

    [Fact]
    public void An_alert_rule_with_its_subscriptions_compiles_and_shows_in_the_dependency_graph()
    {
        var result = Compile("1.7.0", Setup());
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        var graph = DependencyGraph.Build(result.Config);
        Assert.Contains("tenant__critical_escalation", graph.ImpactOf("tenant__sub_escalated"));
        Assert.Contains("tenant__critical_escalation", graph.ImpactOf($"{Ticket}/title"));
        Assert.Contains(ConfigDiff.Compare(Compile("1.7.0").Config, result.Config), c => c.Kind == "alert_rule" && c.Id == "tenant__critical_escalation");

        // A subscription a rule uses cannot be removed.
        Assert.Equal(OpStatus.Failed, Compile("1.7.0", [.. Setup(), new RemoveSubscription { Subscription = "tenant__sub_closed" }]).Composition.Outcomes[^1].Status);
    }

    [Theory]
    [InlineData("trigger_target", "config.bad_target")]
    [InlineData("loop", "config.alert_loop")]
    [InlineData("dedup", "config.alert_bad_dedup")]
    [InlineData("title", "config.bad_placeholder")]
    [InlineData("html", "config.raw_html")]
    [InlineData("owner", "config.alert_bad_owner")]
    [InlineData("escalation", "config.alert_bad_escalation")]
    [InlineData("repeat", "config.alert_bad_escalation")]
    [InlineData("severity", "config.bad_severity")]
    public void Alert_rules_need_an_alert_subscription_a_valid_key_title_owner_and_escalation(string problem, string code)
    {
        var trigger = Escalated();
        var rule = Rule(r =>
        {
            switch (problem)
            {
                case "dedup": r.DedupKey = "record.nope + 1"; break;
                case "title": r.Title = "{{record.nope}}"; break;
                case "html": r.Title = "<b>Escalated</b>"; break;
                case "owner": r.Owner = new AlertOwnerSpec { Kind = AlertOwnerKinds.Field, Ref = "title" }; break;
                case "escalation": r.Escalation = [new() { AfterMinutes = 30 }, new() { AfterMinutes = 15 }]; break;
                case "repeat": r.Escalation = [new() { AfterMinutes = 30, RepeatEveryMinutes = 30 }, new() { AfterMinutes = 60 }]; break;
                case "severity": r.MinSeverity = "urgent"; break;
            }
        });
        if (problem == "trigger_target")
        {
            trigger.Target = SubscriptionTargets.Notify;
        }

        if (problem == "loop")
        {
            trigger.Event = "platform.alert.opened";
            trigger.Entity = Ticket;
            rule.Title = "Loop";
        }

        Assert.Contains(Compile("1.7.0", Setup(rule, trigger)).Errors, e => e.Code == code);
    }

    [Fact]
    public void Alert_titles_keys_and_owners_never_read_hidden_or_restricted_values()
    {
        // estimated_effort is hidden from requesters.
        Assert.Contains(Compile("1.7.0", Setup(Rule(r => r.Title = "{{record.estimated_effort}}"))).Errors, e => e.Code == "config.alert_hidden_field");
        Assert.Contains(Compile("1.7.0", Setup(Rule(r => r.DedupKey = "record.estimated_effort"))).Errors, e => e.Code == "config.alert_hidden_field");

        // A route cannot use an alert subscription, and alert_owner only exists on alert events.
        var route = new AddNotificationRoute
        {
            Route = new NotificationRouteSpec { Id = "tenant__r", Label = "R", Subscription = "tenant__sub_escalated", Profile = PlatformModule.InAppProfile, Template = "core.tickets.template.escalated", Recipients = [new RecipientSpec { Kind = RecipientKinds.AlertOwner, Ref = "alert" }] },
        };
        var errors = Compile("1.7.0", [.. Setup(), route]).Errors;
        Assert.Contains(errors, e => e.Code == "config.bad_target" && e.Target == "tenant__r");
        Assert.Contains(errors, e => e.Code == "config.bad_recipient" && e.Target == "tenant__r");
    }

    [Fact]
    public void Module_alert_rules_keep_their_trigger_and_key_and_changes_conflict_like_other_ops()
    {
        var module = Tickets("1.7.0");
        module.Subscriptions.Add(new SubscriptionSpec { Id = "core.tickets.sub.escalated_alert", Label = "Escalated", Event = "core.tickets.ticket.escalated", Target = SubscriptionTargets.Alert });
        module.AlertRules.Add(new AlertRuleSpec { Id = "core.tickets.alert.escalated", Label = "Escalated", Trigger = "core.tickets.sub.escalated_alert", Title = "Escalated: {{record.number}}" });
        CompileResult With(params OverlayOp[] ops) => Compiler.Compile([PlatformSpec, module], ops);
        Assert.True(With().Succeeded, string.Join("\n", With().Errors.Select(e => e.Message)));
        Assert.Equal(OpStatus.Failed, With(new UpdateAlertRule { Rule = "core.tickets.alert.escalated", Set = new AlertRulePatch { DedupKey = "event.reason" } }).Composition.Outcomes[^1].Status);
        Assert.Equal(OpStatus.Applied, With(new UpdateAlertRule { Rule = "core.tickets.alert.escalated", Set = new AlertRulePatch { Owner = new AlertOwnerSpec { Kind = AlertOwnerKinds.Group, Ref = "network" }, Enabled = false } }).Composition.Outcomes[^1].Status);
        Assert.Equal(OpStatus.Failed, With(new RemoveAlertRule { Rule = "core.tickets.alert.escalated" }).Composition.Outcomes[^1].Status);
        var stale = new UpdateAlertRule { Rule = "core.tickets.alert.escalated", Set = new AlertRulePatch { Label = "Mine" }, Expected = new System.Text.Json.Nodes.JsonObject { ["label"] = "Somebody else's" } };
        Assert.NotEqual(OpStatus.Applied, With(stale).Composition.Outcomes[^1].Status);

        // A module may not raise alerts from another module's subscription.
        var billing = new ModuleSpec
        {
            Id = "core.billing",
            Version = "1.0.0",
            Name = "Billing",
            AlertRules = [new AlertRuleSpec { Id = "core.billing.alert.borrowed", Label = "Borrowed", Trigger = "core.tickets.sub.escalated_alert", Title = "Borrowed" }],
        };
        Assert.Contains(Compiler.Compile([PlatformSpec, module, billing], []).Errors, e => e.Code == "config.foreign_notification_element" && e.Target == "core.billing.alert.borrowed");
    }

    [Fact]
    public void Silencing_an_alert_rule_behind_a_mandatory_alert_route_is_a_permission_change()
    {
        OverlayOp[] withRoute =
        [
            .. Setup(),
            new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_alert_opened", Label = "Alert opened", Event = "platform.alert.opened" } },
            new AddNotificationTemplate { Template = new NotificationTemplateSpec { Id = "tenant__tpl_alert", Label = "Alert", Subject = "{{event.title}}", Body = "{{link}}" } },
            new AddNotificationRoute { Route = new NotificationRouteSpec { Id = "tenant__route_alert", Label = "Alert owners", Subscription = "tenant__sub_alert_opened", Profile = PlatformModule.InAppProfile, Template = "tenant__tpl_alert", Recipients = [new RecipientSpec { Kind = RecipientKinds.AlertOwner, Ref = "alert" }], Mandatory = true } },
        ];
        var before = Compile("1.7.0", withRoute);
        Assert.True(before.Succeeded, string.Join("\n", before.Errors.Select(e => $"{e.Code}: {e.Message}")));
        var after = Compile("1.7.0", [.. withRoute, new UpdateAlertRule { Rule = "tenant__critical_escalation", Set = new AlertRulePatch { Enabled = false } }]).Config;
        Assert.True(ConfigCompiler.PermissionDiff(before.Config, after).Changed);
        var relabel = Compile("1.7.0", [.. withRoute, new UpdateAlertRule { Rule = "tenant__critical_escalation", Set = new AlertRulePatch { Label = "Critical" } }]).Config;
        Assert.False(ConfigCompiler.PermissionDiff(before.Config, relabel).Changed);
    }

    [Fact]
    public void Tickets_alerts_on_sla_breach_and_risk_and_resolves_them_with_the_ticket()
    {
        var result = Compile("1.8.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        var breach = result.Config.FindAlertRule("core.tickets.alert.sla_breached")!;
        Assert.Equal("platform.sla.breached", result.Config.FindSubscription(breach.Trigger)!.Event);
        Assert.Equal(new AlertOwnerSpec { Kind = AlertOwnerKinds.Field, Ref = "assignment_group" }.Ref, breach.Owner!.Ref);
        Assert.Contains("core.tickets.sub.ticket_done_alert", breach.ResolveOn);
        Assert.Contains("core.tickets.sub.sla_breached_alert", result.Config.FindAlertRule("core.tickets.alert.sla_at_risk")!.ResolveOn);
        Assert.All(result.Config.NotificationRoutes.Where(r => r.Id.StartsWith("core.tickets.route.sla_alert", StringComparison.Ordinal)), r => Assert.False(r.Mandatory));

        // Upgrading from 1.7.0 adds alerts without changing who may see or do what.
        Assert.False(ConfigCompiler.PermissionDiff(Compile("1.7.0").Config, result.Config).Changed);
    }

    [Fact]
    public void Agents_never_hold_alert_or_audit_administration()
    {
        var config = Compile("1.7.0").Config;
        var principal = Guid.NewGuid();
        var facts = new AuthorizationFacts { PrincipalId = principal, Roles = new Dictionary<string, IReadOnlyList<string>> { [PlatformModule.TenantAdminRole] = ["direct"] }, Groups = new HashSet<Guid>() };
        var person = new EffectivePermissions(config, facts, new Kernel.Tenancy.RequestContext { TenantId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), PrincipalId = principal });
        var agent = new EffectivePermissions(config, facts, new Kernel.Tenancy.RequestContext { TenantId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), PrincipalId = principal, AgentId = Guid.NewGuid() });
        foreach (var action in new[] { PlatformActions.AlertsRespond, PlatformActions.AlertsManage, PlatformActions.AuditExport, PlatformActions.AuditManage })
        {
            Assert.True(person.Can(action));
            Assert.False(agent.Can(action));
        }
    }

    [Fact]
    public void Escalation_steps_raise_the_level_on_time_repeat_a_bounded_number_of_times_and_wait_for_acknowledgment()
    {
        var rule = Rule();
        var opened = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal((opened.AddMinutes(15), 2), AlertLifecycle.NextEscalation(rule, opened, 1));
        Assert.Equal((opened.AddMinutes(60), 3), AlertLifecycle.NextEscalation(rule, opened, 2));
        Assert.Equal((opened.AddMinutes(120), 4), AlertLifecycle.NextEscalation(rule, opened, 3));
        Assert.Null(AlertLifecycle.NextEscalation(rule, opened, 3 + AlertLifecycle.MaxRepeats));
        Assert.Null(AlertLifecycle.NextEscalation(Rule(r => r.Escalation = [new() { AfterMinutes = 5 }]), opened, 2));

        Assert.True(AlertLifecycle.Escalates(rule, AlertStatuses.Open, 2));
        Assert.False(AlertLifecycle.Escalates(rule, AlertStatuses.Acknowledged, 2));
        Assert.True(AlertLifecycle.Escalates(Rule(r => r.Escalation = [new() { AfterMinutes = 5, UnlessAcknowledged = false }]), AlertStatuses.Acknowledged, 2));
        Assert.False(AlertLifecycle.Escalates(rule, AlertStatuses.Suppressed, 2));

        Assert.True(AlertLifecycle.Reopens(Rule(r => r.ReopenWithinMinutes = 30), opened, opened.AddMinutes(20)));
        Assert.False(AlertLifecycle.Reopens(Rule(r => r.ReopenWithinMinutes = 30), opened, opened.AddMinutes(40)));
        Assert.True(AlertLifecycle.CanMove(AlertStatuses.Open, AlertStatuses.Acknowledged));
        Assert.False(AlertLifecycle.CanMove(AlertStatuses.Resolved, AlertStatuses.Acknowledged));
    }
}
