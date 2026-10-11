using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Alerts;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Alerts;

/// <summary>
/// REQ-ALR-001, REQ-AUD-001 (ADR 0022): alerts deduplicate, escalate on timers until acknowledged, resolve by hand or
/// with the ticket, stay quiet in maintenance windows, reopen within their cool-down, publish events that notification
/// routes turn into messages, and are visible only to their owners and responders who may read the record.
/// </summary>
[Collection(PlatformCollection.Name)]
public class AlertTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";
    private const string Rule = "tenant__critical_escalation";

    private AlertService Alerts => f.Get<AlertService>();

    /// <summary>The critical-escalation example (architecture doc section 8) as configuration.</summary>
    private static OverlayOp[] CriticalEscalation(int reopenWithin = 0) =>
    [
        new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_escalated_alert", Label = "Escalated (alert)", Event = "core.tickets.ticket.escalated", Target = SubscriptionTargets.Alert } },
        new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_done_alert", Label = "Done (alert)", Event = EventTypes.RecordTransitioned, Entity = Ticket, When = "record.status in ['resolved', 'closed']", Target = SubscriptionTargets.Alert } },
        new AddAlertRule
        {
            Rule = new AlertRuleSpec
            {
                Id = Rule,
                Label = "Critical escalation",
                Trigger = "tenant__sub_escalated_alert",
                MinSeverity = EventSeverities.Critical,
                Title = "Critical: {{record.number}} {{record.title}}",
                Owner = new AlertOwnerSpec { Kind = AlertOwnerKinds.Group, Ref = "network" },
                Escalation = [new() { AfterMinutes = 15 }, new() { AfterMinutes = 60, RepeatEveryMinutes = 60 }],
                ResolveOn = ["tenant__sub_done_alert"],
                ReopenWithinMinutes = reopenWithin,
            },
        },
        new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_alert_opened", Label = "Alert opened", Event = EventTypes.AlertOpened } },
        new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_alert_escalated", Label = "Alert escalated", Event = EventTypes.AlertEscalated, When = "event.level >= 2" } },
        new AddNotificationTemplate { Template = new NotificationTemplateSpec { Id = "tenant__tpl_alert", Label = "Alert", Entity = Ticket, Subject = "{{event.title}}", Body = "Level {{event.level}}. {{link}}" } },
        new AddNotificationRoute { Route = new NotificationRouteSpec { Id = "tenant__route_alert_owners", Label = "Alert owners", Subscription = "tenant__sub_alert_opened", Profile = PlatformModule.InAppProfile, Template = "tenant__tpl_alert", Recipients = [new RecipientSpec { Kind = RecipientKinds.AlertOwner, Ref = "alert" }] } },
        new AddNotificationRoute { Route = new NotificationRouteSpec { Id = "tenant__route_alert_desk", Label = "Escalations to the desk", Subscription = "tenant__sub_alert_escalated", Profile = PlatformModule.InAppProfile, Template = "tenant__tpl_alert", Recipients = [new RecipientSpec { Kind = RecipientKinds.Group, Ref = "service_desk" }] } },
    ];

    private async Task<ProvisionedTenant> TenantAsync(string prefix, params OverlayOp[] ops)
    {
        var t = await f.NewTenantAsync(prefix, "1.7.0");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyBatchAsync(admin, ops, draft.Version, "ui", default);
        await f.Get<PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);
        return t;
    }

    private async Task PumpAsync()
    {
        var dispatcher = f.Get<OutboxDispatcher>();
        var runner = f.Get<JobRunner>();
        while (await dispatcher.DispatchOnceAsync(default) > 0 | await runner.RunOnceAsync(default) > 0)
        {
        }
    }

    /// <summary>A P1 ticket in progress, escalated (a critical event).</summary>
    private async Task<RecordView> EscalatedTicketAsync(ProvisionedTenant t, string title)
    {
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = title, ["requester"] = t.Users["dana"].ToString(), ["priority"] = "p1", ["impact"] = "high" }, null, null, default);
        var started = await records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        var escalated = await records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "escalate", new JsonObject { ["escalation_reason"] = "Core switch down" }, null, null, default);
        await PumpAsync();
        return escalated;
    }

    private async Task<RecordView> EscalateAgainAsync(ProvisionedTenant t, RecordView ticket)
    {
        var again = await f.Get<RecordService>().TransitionAsync(f.As(t, "agent"), Ticket, ticket.Id, ticket.Revision, "escalate", new JsonObject { ["escalation_reason"] = "Still down" }, null, null, default);
        await PumpAsync();
        return again;
    }

    private Task<List<AlertRow>> AlertRowsAsync(ProvisionedTenant t) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Alerts.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(), default);

    private Task<List<OutboxRow>> AlertEventsAsync(ProvisionedTenant t, string type) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Outbox.AsNoTracking().Where(o => o.Type == type).ToListAsync(), default);

    /// <summary>Makes the pending escalation timer due now, as time passing would.</summary>
    private async Task FastForwardAsync(ProvisionedTenant t)
    {
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.Where(j => j.Kind == AlertEvaluateJobHandler.EscalateKind && j.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1))), default);
        await PumpAsync();
    }

    [Fact]
    public async Task A_critical_escalation_opens_one_alert_escalates_until_acknowledged_and_resolves_with_the_ticket()
    {
        var t = await TenantAsync("alc", CriticalEscalation());
        var ticket = await EscalatedTicketAsync(t, "Core switch down");

        // One alert for the ticket, owned by Network, announced to its members by the alert-owner route.
        var alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal((AlertStatuses.Open, "network", 1, 1), (alert.Status, alert.OwnerGroupKey, alert.Level, alert.Occurrences));
        Assert.StartsWith("Critical: TCK-", alert.Title);
        var deliveries = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.NotificationDeliveries.AsNoTracking().Where(d => d.RouteId == "tenant__route_alert_owners").ToListAsync(), default);
        Assert.Contains(deliveries, d => d.RecipientPrincipal == t.Users["agent"] && d.Subject == alert.Title);
        Assert.DoesNotContain(deliveries, d => d.RecipientPrincipal == t.Users["sam"]); // service desk only, not network

        // A second escalation counts on the same alert.
        ticket = await EscalateAgainAsync(t, ticket);
        alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal(2, alert.Occurrences);
        Assert.Single(await AlertEventsAsync(t, EventTypes.AlertOpened));

        // Fifteen minutes later nobody took it: level 2, and the desk is told.
        await FastForwardAsync(t);
        alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal(2, alert.Level);
        Assert.Single(await AlertEventsAsync(t, EventTypes.AlertEscalated));
        var desk = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.NotificationDeliveries.AsNoTracking().Where(d => d.RouteId == "tenant__route_alert_desk").ToListAsync(), default);
        Assert.Contains(desk, d => d.RecipientPrincipal == t.Users["sam"]);

        // The agent acknowledges: audited, and the next step (which waits for acknowledgment) does not escalate.
        var agent = f.As(t, "agent");
        var acknowledged = await Alerts.AcknowledgeAsync(agent, alert.Id, alert.Version, default);
        Assert.Equal(AlertStatuses.Acknowledged, acknowledged.Status);
        Assert.Equal(AlertStatuses.Acknowledged, (await Alerts.AcknowledgeAsync(agent, alert.Id, null, default)).Status); // idempotent
        await FastForwardAsync(t);
        Assert.Equal(2, Assert.Single(await AlertRowsAsync(t)).Level);
        var audit = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "alert.acknowledge" && a.TargetId == alert.Id.ToString()).CountAsync(), default);
        Assert.Equal(1, audit);

        // Resolving the ticket resolves the alert automatically.
        var records = f.Get<RecordService>();
        await records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Replaced the switch" }, null, null, default);
        await PumpAsync();
        alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal((AlertStatuses.Resolved, "auto"), (alert.Status, alert.Resolution));
        Assert.Single(await AlertEventsAsync(t, EventTypes.AlertResolved));
        var timeline = await Alerts.GetAsync(agent, alert.Id, default);
        Assert.Equal(["opened", "escalated", "acknowledged", "resolved"], timeline.Activity.Select(a => a.Kind).Where(k => k != "occurred").ToArray());

        // Lower-severity escalations (a P2 is high, not critical) raise nothing.
        var p2 = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Slow wifi", ["requester"] = t.Users["dana"].ToString(), ["priority"] = "p2", ["impact"] = "low" }, null, null, default);
        var started = await records.TransitionAsync(agent, Ticket, p2.Id, p2.Revision, "start", [], null, null, default);
        await records.TransitionAsync(agent, Ticket, p2.Id, started.Revision, "escalate", new JsonObject { ["escalation_reason"] = "Annoying" }, null, null, default);
        await PumpAsync();
        Assert.Single(await AlertRowsAsync(t));
    }

    [Fact]
    public async Task Only_owners_and_responders_who_may_read_the_record_see_or_act_and_agents_never_act()
    {
        var t = await TenantAsync("alv", CriticalEscalation());
        await EscalatedTicketAsync(t, "Firewall down");
        var alert = Assert.Single(await AlertRowsAsync(t));

        // Owners (Network) and responders (the admin) see it; the desk member and the requester do not.
        Assert.Single((await Alerts.ListAsync(f.As(t, "agent"), null, mine: false, 50, 0, default)).Items);
        Assert.Single((await Alerts.ListAsync(f.As(t, "admin"), null, mine: false, 50, 0, default)).Items);
        Assert.Empty((await Alerts.ListAsync(f.As(t, "sam"), null, mine: false, 50, 0, default)).Items);
        Assert.Empty((await Alerts.ListAsync(f.As(t, "dana"), null, mine: false, 50, 0, default)).Items);
        Assert.Equal(1, await Alerts.CountAsync(f.As(t, "agent"), default));
        Assert.Equal(0, await Alerts.CountAsync(f.As(t, "sam"), default));
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Alerts.AcknowledgeAsync(f.As(t, "sam"), alert.Id, null, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Alerts.GetAsync(f.As(t, "dana"), alert.Id, default))).Status);

        // Agents read alerts (with records:read) but never acknowledge, resolve, assign or silence.
        var agentId = await f.Get<IdentityService>().EnsureAgentAsync(t.Users["admin"], t.TenantId, default);
        var ai = f.As(t, "admin", agent: agentId, scopes: new HashSet<string>(AgentScopes.All));
        Assert.Single((await Alerts.ListAsync(ai, null, false, 50, 0, default)).Items);
        Assert.False(Assert.Single((await Alerts.ListAsync(ai, null, false, 50, 0, default)).Items).CanRespond);
        Assert.Equal("agent_not_allowed", (await Assert.ThrowsAsync<KernelException>(() => Alerts.AcknowledgeAsync(ai, alert.Id, null, default))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Alerts.SuppressAsync(ai, new SuppressionRequest("x", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), default))).Status);

        // Assigning: only to people who may read the record; stale versions conflict.
        var admin = f.As(t, "admin");
        Assert.Equal("invalid_assignee", (await Assert.ThrowsAsync<KernelException>(() => Alerts.AssignAsync(admin, alert.Id, t.Users["jordan"], null, default))).Code);
        var assigned = await Alerts.AssignAsync(admin, alert.Id, t.Users["sam"], alert.Version, default);
        Assert.Equal("Sam " + PlatformFixture.Slugs[t.TenantId], assigned.Assignee);
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<KernelException>(() => Alerts.ResolveAsync(admin, alert.Id, null, alert.Version, default))).Code);
        Assert.Single((await Alerts.ListAsync(f.As(t, "sam"), null, mine: true, 50, 0, default)).Items); // now theirs
        Assert.Single(await AlertEventsAsync(t, EventTypes.AlertAssigned));

        // Other workspaces and environments see nothing.
        Assert.Empty((await Alerts.ListAsync(f.As(f.Globex, "morgan"), null, false, 50, 0, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Alerts.GetAsync(f.As(f.Globex, "morgan"), alert.Id, default))).Status);
        Assert.Empty((await Alerts.ListAsync(f.As(t, "admin", env: "sandbox"), null, false, 50, 0, default)).Items);
    }

    [Fact]
    public async Task A_maintenance_window_records_alerts_without_announcing_them_and_a_cool_down_reopens()
    {
        var t = await TenantAsync("als", CriticalEscalation(reopenWithin: 60));
        var admin = f.As(t, "admin");
        Assert.Equal("invalid_window", (await Assert.ThrowsAsync<KernelException>(() => Alerts.SuppressAsync(admin, new SuppressionRequest("Too long", Rule, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(20)), default))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Alerts.SuppressAsync(f.As(t, "agent"), new SuppressionRequest("Mine", Rule, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), default))).Status);
        var window = await Alerts.SuppressAsync(admin, new SuppressionRequest("Switch replacement", Rule, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2)), default);

        var ticket = await EscalatedTicketAsync(t, "Planned outage");
        var alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal(AlertStatuses.Suppressed, alert.Status);
        Assert.Empty(await AlertEventsAsync(t, EventTypes.AlertOpened));
        Assert.Empty(await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.AsNoTracking().Where(j => j.Kind == AlertEvaluateJobHandler.EscalateKind).ToListAsync(), default));

        // The window ends early and the problem recurs: now it is announced.
        await Alerts.CancelSuppressionAsync(admin, window.Id, default);
        ticket = await EscalateAgainAsync(t, ticket);
        alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal((AlertStatuses.Open, 2), (alert.Status, alert.Occurrences));
        Assert.Single(await AlertEventsAsync(t, EventTypes.AlertOpened));

        // Resolved by hand, then it happens again within the hour: the same alert reopens.
        var agent = f.As(t, "agent");
        await Alerts.ResolveAsync(agent, alert.Id, "Rebooted", null, default);
        await EscalateAgainAsync(t, ticket);
        alert = Assert.Single(await AlertRowsAsync(t));
        Assert.Equal((AlertStatuses.Open, 1, 3), (alert.Status, alert.ReopenedCount, alert.Occurrences));
        Assert.Contains(await AlertEventsAsync(t, EventTypes.AlertOpened), e => EventWriter.Read(e).Payload["reopened"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task Tickets_alerts_the_owning_team_on_an_sla_breach_and_resolves_it_with_the_ticket()
    {
        // Tickets 1.8.0, upgraded from 1.7.0 with a workspace route in place.
        var t = await f.NewTenantAsync("alsla", "1.7.0");
        var admin = f.As(t, "admin");
        var upgrades = f.Get<UpgradeService>();
        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.8.0", default);
        Assert.True(plan.Compatible, string.Join("; ", plan.Errors.Select(e => e.Message)));
        Assert.Contains(plan.BaseChanges, c => c.Id == "core.tickets.alert.sla_breached" && c.Change == "added");
        var started = await upgrades.StartAsync(admin, "core.tickets", "1.8.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        Assert.Equal("activated", (await upgrades.GetAsync(admin, started.Id, default)).Status);

        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Printer jam", ["requester"] = t.Users["dana"].ToString(), ["priority"] = "p3", ["impact"] = "low", ["category"] = "network" }, null, null, default);
        await PumpAsync();
        var db = f.Get<Database>();
        var timer = await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.SlaTimers.AsNoTracking().Where(s => s.RecordId == ticket.Id).OrderBy(s => s.TargetId).Select(s => s.Id).FirstAsync(), default);

        // The goal passes: the breach event raises an alert for the ticket's assignment group (Network).
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, async x =>
        {
            await x.SlaTimers.Where(s => s.Id == timer).ExecuteUpdateAsync(s => s.SetProperty(r => r.DueAt, past).SetProperty(r => r.WarnAt, (DateTimeOffset?)null));
            return await x.Jobs.Where(j => j.DedupKey == Kernel.Infrastructure.Sla.SlaService.JobKey(timer) && j.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, past));
        }, default);
        await PumpAsync();
        var alert = Assert.Single(await AlertRowsAsync(t), a => a.RuleId == "core.tickets.alert.sla_breached");
        Assert.Equal((AlertStatuses.Open, "network", ticket.Id), (alert.Status, alert.OwnerGroupKey, alert.RecordId));
        Assert.StartsWith("SLA breached: TCK-", alert.Title);
        var told = await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.NotificationDeliveries.AsNoTracking().Where(d => d.RouteId == "core.tickets.route.sla_alert_opened").ToListAsync(), default);
        Assert.Contains(told, d => d.RecipientPrincipal == t.Users["agent"] && d.ChannelType == "in_app");

        // Done with the ticket: the alert resolves itself.
        var current = await records.GetAsync(agent, Ticket, ticket.Id, default);
        var working = await records.TransitionAsync(agent, Ticket, ticket.Id, current.Revision, "start", [], null, null, default);
        await records.TransitionAsync(agent, Ticket, ticket.Id, working.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Cleared" }, null, null, default);
        await PumpAsync();
        Assert.Equal((AlertStatuses.Resolved, "auto"), Assert.Single(await AlertRowsAsync(t), a => a.Id == alert.Id) is var done ? (done.Status, done.Resolution) : default);
    }

    [Fact]
    public async Task Audit_captures_every_change_even_when_no_notification_can_be_sent()
    {
        // ADR 0017: audit is written in the business transaction, independent of notifications and alerts.
        var t = await TenantAsync("ala", CriticalEscalation());
        var admin = f.As(t, "admin");
        await f.Get<Kernel.Infrastructure.Notifications.NotificationService>().SaveChannelAsync(admin, new Kernel.Infrastructure.Notifications.SaveChannelRequest("email", "email", "E", "disabled", null, null, null), default);
        var ticket = await EscalatedTicketAsync(t, "Audit me");
        var operations = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.TargetId == ticket.Id.ToString()).Select(a => a.Operation).ToListAsync(), default);
        Assert.Contains("record.create", operations);
        Assert.Contains(operations, o => o.StartsWith("record.transition", StringComparison.Ordinal));
    }
}
