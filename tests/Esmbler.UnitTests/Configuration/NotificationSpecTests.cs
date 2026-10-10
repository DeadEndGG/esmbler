using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-NOT-002, REQ-NOT-003, REQ-NOT-004, REQ-NOT-007: subscriptions, profiles, routes and templates as validated configuration (ADR 0021).</summary>
public class NotificationSpecTests
{
    private const string Ticket = "core.tickets.ticket";

    private static SubscriptionSpec Resolved(string? when = null) => new() { Id = "tenant__sub_resolved", Label = "Ticket resolved", Event = "core.tickets.ticket.resolved", When = when };

    private static NotificationTemplateSpec Template(string body = "{{record.number}} {{record.title}} was resolved. {{link}}") => new()
    {
        Id = "tenant__tpl_resolved",
        Label = "Resolved",
        Entity = Ticket,
        Subject = "Resolved: {{record.title}}",
        Body = body,
        Variants = new() { [NotificationChannels.Sms] = new TemplateVariantSpec { Body = "{{record.number}} resolved" } },
    };

    private static NotificationRouteSpec Route(params RecipientSpec[] recipients) => new()
    {
        Id = "tenant__route_resolved",
        Label = "Tell the requester",
        Subscription = "tenant__sub_resolved",
        Profile = PlatformModule.InAppEmailProfile,
        Template = "tenant__tpl_resolved",
        Recipients = recipients.Length == 0 ? [new RecipientSpec { Kind = RecipientKinds.Field, Ref = "requester" }] : [.. recipients],
    };

    private static OverlayOp[] Setup(SubscriptionSpec? sub = null, NotificationTemplateSpec? template = null, NotificationRouteSpec? route = null) =>
    [
        new AddSubscription { Subscription = sub ?? Resolved() },
        new AddNotificationTemplate { Template = template ?? Template() },
        new AddNotificationRoute { Route = route ?? Route() },
    ];

    [Fact]
    public void A_subscription_route_and_template_compile_with_the_built_in_profiles()
    {
        var result = Compile("1.6.0", Setup(Resolved("event.priority == 'p1' && record.category == 'network'")));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Contains(result.Config.NotificationProfiles, p => p.Id == PlatformModule.UrgentProfile && p.MinSeverity == EventSeverities.High);
        var graph = DependencyGraph.Build(result.Config);
        Assert.Contains("tenant__route_resolved", graph.ImpactOf("tenant__sub_resolved"));
        Assert.Contains("tenant__route_resolved", graph.ImpactOf($"{Ticket}/requester"));
    }

    [Theory]
    [InlineData("core.tickets.ticket.reopened_twice", null, "config.not_found")]
    [InlineData("core.tickets.ticket.resolved", "event.nope == 'x'", "unknown_field")]
    public void Subscriptions_need_a_known_event_and_a_valid_condition(string evt, string? when, string code)
    {
        var result = Compile("1.6.0", Setup(new SubscriptionSpec { Id = "tenant__sub_resolved", Label = "S", Event = evt, When = when }));
        Assert.Contains(result.Errors, e => e.Code == code || e.Code.Contains(code, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Hello {{record.nope}}", "config.bad_placeholder")]
    [InlineData("Hello {{secrets.key}}", "config.bad_placeholder")]
    [InlineData("<b>Resolved</b>", "config.raw_html")]
    public void Templates_are_plain_text_with_known_placeholders(string body, string code)
    {
        var result = Compile("1.6.0", Setup(template: Template(body)));
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    [Fact]
    public void Routes_check_their_recipients_and_never_send_internal_events_outside()
    {
        Assert.Contains(Compile("1.6.0", Setup(route: Route(new RecipientSpec { Kind = RecipientKinds.Field, Ref = "title" }))).Errors, e => e.Code == "config.bad_recipient");
        Assert.Contains(Compile("1.6.0", Setup(route: Route(new RecipientSpec { Kind = RecipientKinds.Role, Ref = "core.tickets.agnt" }))).Errors, e => e.Code == "config.bad_recipient");
        Assert.Contains(Compile("1.6.0", Setup(route: Route(new RecipientSpec { Kind = RecipientKinds.Approvers, Ref = "all" }))).Errors, e => e.Code == "config.bad_recipient");
        var external = Compile("1.6.0", Setup(route: Route(new RecipientSpec { Kind = RecipientKinds.Address, Ref = "vendor@example.com" })));
        Assert.Contains(external.Errors, e => e.Code == "config.external_redaction");
        var bad = Compile("1.6.0", new AddNotificationProfile { Profile = new NotificationProfileSpec { Id = "tenant__p", Label = "P", Channels = ["pigeon"], Delivery = "sometimes" } });
        Assert.Contains(bad.Errors, e => e.Code == "config.bad_channel");
        Assert.Contains(bad.Errors, e => e.Code == "config.bad_delivery");
    }

    [Fact]
    public void Mandatory_routes_and_external_recipients_are_permission_changes()
    {
        Assert.False(new AddNotificationRoute { Route = Route() }.AffectsPermissions);
        var mandatoryRoute = Route();
        mandatoryRoute.Mandatory = true;
        Assert.True(new AddNotificationRoute { Route = mandatoryRoute }.AffectsPermissions);
        Assert.True(new AddNotificationRoute { Route = Route(new RecipientSpec { Kind = RecipientKinds.Address, Ref = "a@b.co" }) }.AffectsPermissions);
        Assert.True(new UpdateNotificationRoute { Route = "x", Set = new NotificationRoutePatch { Mandatory = false } }.AffectsPermissions);
        Assert.False(new UpdateNotificationRoute { Route = "x", Set = new NotificationRoutePatch { Label = "Renamed" } }.AffectsPermissions);
        Assert.True(new UpdateNotificationTemplate { Template = "x", Set = new NotificationTemplatePatch { ExternalFields = ["title"] } }.AffectsPermissions);

        var before = Compile("1.6.0", Setup()).Config;
        var mandatory = Route();
        mandatory.Mandatory = true;
        var after = Compile("1.6.0", Setup(route: mandatory)).Config;
        Assert.True(ConfigCompiler.PermissionDiff(before, after).Changed);
    }

    [Fact]
    public void Mandatory_routes_cannot_be_turned_off_or_removed_and_used_objects_stay()
    {
        var mandatory = Route();
        mandatory.Mandatory = true;
        string Last(params OverlayOp[] ops) => Compile("1.6.0", ops).Composition.Outcomes[^1].Status;
        Assert.Equal(OpStatus.Failed, Last([.. Setup(route: mandatory), new UpdateNotificationRoute { Route = mandatory.Id, Set = new NotificationRoutePatch { Enabled = false } }]));
        Assert.Equal(OpStatus.Applied, Last([.. Setup(route: mandatory), new UpdateNotificationRoute { Route = mandatory.Id, Set = new NotificationRoutePatch { Enabled = false, Mandatory = false } }]));
        Assert.Equal(OpStatus.Failed, Last([.. Setup(route: mandatory), new RemoveNotificationRoute { Route = mandatory.Id }]));
        Assert.Equal(OpStatus.Failed, Last([.. Setup(), new RemoveSubscription { Subscription = "tenant__sub_resolved" }]));
        Assert.Equal(OpStatus.Failed, Last(new RemoveNotificationProfile { Profile = PlatformModule.InAppProfile }));
        Assert.Equal(OpStatus.Applied, Last(new UpdateNotificationProfile { Profile = PlatformModule.UrgentProfile, Set = new NotificationProfilePatch { Enabled = false } }));
        Assert.Equal(OpStatus.Failed, Last([.. Setup(), new AddSubscription { Subscription = Resolved() }]));
    }

    [Fact]
    public void Modules_keep_notification_elements_in_their_namespace_and_route_only_through_their_own()
    {
        var billing = new ModuleSpec
        {
            Id = "core.billing",
            Version = "1.0.0",
            Name = "Billing",
            Subscriptions = [new SubscriptionSpec { Id = "core.tickets.sub.borrowed", Label = "Borrowed", Event = "core.tickets.ticket.resolved" }],
            NotificationRoutes =
            [
                new NotificationRouteSpec
                {
                    Id = "core.billing.route.escalations",
                    Label = "Escalations to billing",
                    Subscription = "core.tickets.sub.escalated",
                    Template = "core.tickets.template.escalated",
                    Profile = PlatformModule.InAppProfile,
                    Recipients = [new RecipientSpec { Kind = RecipientKinds.Role, Ref = "core.tickets.agent" }],
                },
            ],
        };
        var result = Compiler.Compile([PlatformSpec, Tickets("1.7.0"), billing], []);
        Assert.Contains(result.Errors, e => e.Code == "config.foreign_notification_element" && e.Target == "core.tickets.sub.borrowed");
        Assert.Equal(2, result.Errors.Count(e => e.Code == "config.foreign_notification_element" && e.Target == "core.billing.route.escalations"));
        Assert.DoesNotContain(Compile("1.7.0", Setup()).Errors, e => e.Code == "config.foreign_notification_element");
    }

    [Fact]
    public void Tickets_routes_escalations_and_makes_the_p1_alert_mandatory()
    {
        var config = Compile("1.7.0").Config;
        var p1 = config.FindNotificationRoute("core.tickets.route.p1_raised")!;
        Assert.True(p1.Mandatory);
        Assert.Equal(PlatformModule.UrgentProfile, p1.Profile);
        Assert.Equal("core.tickets.ticket.priority_raised", config.FindSubscription(p1.Subscription)!.Event);
        var escalated = config.FindNotificationRoute("core.tickets.route.escalated")!;
        Assert.False(escalated.Mandatory);
        Assert.Contains(escalated.Recipients, r => r is { Kind: RecipientKinds.Field, Ref: "assignment_group" });

        // A tenant can quiet the optional route but not the mandatory one, nor anything the mandatory route relies on.
        Assert.True(Compile("1.7.0", new UpdateNotificationRoute { Route = escalated.Id, Set = new NotificationRoutePatch { Enabled = false } }).Succeeded);
        Assert.Equal(OpStatus.Failed, Compile("1.7.0", new UpdateNotificationRoute { Route = p1.Id, Set = new NotificationRoutePatch { Enabled = false } }).Composition.Outcomes[^1].Status);
        Assert.Contains(Compile("1.7.0", new UpdateNotificationProfile { Profile = PlatformModule.UrgentProfile, Set = new NotificationProfilePatch { Enabled = false } }).Errors, e => e.Code == "config.mandatory_unreachable");
        Assert.Contains(Compile("1.7.0", new UpdateNotificationProfile { Profile = PlatformModule.UrgentProfile, Set = new NotificationProfilePatch { Channels = [NotificationChannels.Email] } }).Errors, e => e.Code == "config.mandatory_unreachable");
        Assert.Contains(Compile("1.7.0", new UpdateSubscription { Subscription = p1.Subscription, Set = new SubscriptionPatch { Enabled = false } }).Errors, e => e.Code == "config.mandatory_unreachable");
        Assert.Contains(Compile("1.7.0", new UpdateNotificationTemplate { Template = p1.Template, Set = new NotificationTemplatePatch { Enabled = false } }).Errors, e => e.Code == "config.mandatory_unreachable");
    }
}
