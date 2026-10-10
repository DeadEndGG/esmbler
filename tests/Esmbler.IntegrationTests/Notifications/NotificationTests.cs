using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Notifications;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Notifications;

/// <summary>
/// REQ-NOT-001..006 (ADR 0021): events reach people through subscriptions and routes, rendered under each recipient's
/// permissions; channels follow their environment's mode; mutes, mandatory routes and deduplication; delivery states,
/// retries and ambiguity; inspection, replay and previews; inbox isolation and authorization.
/// </summary>
[Collection(PlatformCollection.Name)]
public class NotificationTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    private NotificationService Service => f.Get<NotificationService>();

    private static OverlayOp[] ResolvedRoute(bool mandatory = false, string profile = PlatformModule.InAppEmailProfile, string id = "tenant__route_resolved") =>
    [
        new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_resolved", Label = "Ticket resolved", Event = "core.tickets.ticket.resolved" } },
        new AddNotificationTemplate
        {
            Template = new NotificationTemplateSpec
            {
                Id = "tenant__tpl_resolved",
                Entity = Ticket,
                Subject = "Closed out: {{record.title}}",
                Body = "{{record.number}} was resolved for {{recipient.name}}. Labels: {{record.labels}}. Notes: {{record.resolution_notes}}. {{link}}",
            },
        },
        new AddNotificationRoute
        {
            Route = new NotificationRouteSpec
            {
                Id = id,
                Label = "Tell the requester",
                Subscription = "tenant__sub_resolved",
                Profile = profile,
                Template = "tenant__tpl_resolved",
                Recipients = [new RecipientSpec { Kind = RecipientKinds.Field, Ref = "requester" }],
                Mandatory = mandatory,
            },
        },
    ];

    private async Task<ProvisionedTenant> TenantAsync(string prefix, params OverlayOp[] ops)
    {
        var t = await f.NewTenantAsync(prefix, "1.6.0");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyBatchAsync(admin, ops, draft.Version, "ui", default);
        await f.Get<PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);
        return t;
    }

    private async Task<Guid> ResolveTicketAsync(ProvisionedTenant t, string title = "Printer on fire")
    {
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = title, ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low", ["labels"] = new JsonArray("security") }, null, null, default);
        var started = await records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        await records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Put it out" }, null, null, default);
        await PumpAsync();
        return ticket.Id;
    }

    private async Task PumpAsync()
    {
        var dispatcher = f.Get<OutboxDispatcher>();
        var runner = f.Get<JobRunner>();
        while (await dispatcher.DispatchOnceAsync(default) > 0 | await runner.RunOnceAsync(default) > 0)
        {
        }
    }

    private Task<List<NotificationDeliveryRow>> DeliveriesAsync(ProvisionedTenant t) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.NotificationDeliveries.AsNoTracking().OrderBy(d => d.CreatedAt).ToListAsync(), default);

    [Fact]
    public async Task A_resolved_ticket_reaches_the_requester_in_the_app_with_only_fields_they_may_read()
    {
        var t = await TenantAsync("nt", ResolvedRoute());
        await ResolveTicketAsync(t);

        var dana = f.As(t, "dana");
        var inbox = await Service.InboxAsync(dana, unreadOnly: false, before: null, default);
        var item = Assert.Single(inbox.Items, i => i.Subject == "Closed out: Printer on fire");
        Assert.Contains("Notes: Put it out", item.Body);
        Assert.Contains("Labels: .", item.Body); // labels are hidden from requesters: rendered empty
        Assert.DoesNotContain("security", item.Body);
        Assert.Contains("/records/core.tickets.ticket/", item.Body);
        var unread = await Service.UnreadAsync(dana, default); // includes the release's own (legacy) template
        Assert.True(unread >= 1);

        // No email instance in this environment: the email delivery is suppressed, not lost silently.
        var deliveries = await DeliveriesAsync(t);
        Assert.Contains(deliveries, d => d.ChannelType == "in_app" && d.Status == DeliveryStatuses.Sent);
        Assert.Contains(deliveries, d => d.ChannelType == "email" && d.Status == DeliveryStatuses.Suppressed && d.StatusReason == "channel_not_configured");

        // Inboxes are personal: nobody else reads or marks it, and other workspaces see nothing.
        Assert.DoesNotContain((await Service.InboxAsync(f.As(t, "jordan"), false, null, default)).Items, i => i.Id == item.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Service.MarkReadAsync(f.As(t, "jordan"), item.Id, default))).Status);
        Assert.DoesNotContain((await Service.InboxAsync(f.As(f.Globex, "morgan"), false, null, default)).Items, i => i.Id == item.Id);
        await Service.MarkReadAsync(dana, item.Id, default);
        Assert.Equal(unread - 1, await Service.UnreadAsync(dana, default));
        Assert.NotNull((await Service.InboxAsync(dana, false, null, default)).Items.Single(i => i.Id == item.Id).ReadAt);
    }

    [Fact]
    public async Task Sandbox_style_capture_keeps_the_email_and_mutes_and_mandatory_routes_decide_what_is_sent()
    {
        var t = await TenantAsync("ntc", ResolvedRoute());
        var admin = f.As(t, "admin");
        await Service.SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "Team email", "capture", new JsonObject { ["host"] = "smtp.example.com", ["from"] = "desk@example.com" }, ["SMTP_PASSWORD"], 60), default);
        await ResolveTicketAsync(t, "First");
        var email = Assert.Single(await DeliveriesAsync(t), d => d.ChannelType == "email");
        Assert.Equal((DeliveryStatuses.Captured, "Closed out: First"), (email.Status, email.Subject));
        Assert.NotNull(email.RecipientAddress);

        // Dana mutes the route: the next event is suppressed for her.
        var dana = f.As(t, "dana");
        await Service.SetPreferenceAsync(dana, "tenant__route_resolved", "*", true, default);
        await ResolveTicketAsync(t, "Second");
        Assert.All((await DeliveriesAsync(t)).Where(d => d.Subject == "Closed out: Second"), d => Assert.Equal((DeliveryStatuses.Suppressed, "muted"), (d.Status, d.StatusReason)));

        // A mandatory route ignores mutes and cannot be muted.
        var m = await TenantAsync("ntm", ResolvedRoute(mandatory: true));
        Assert.Equal(409, (await Assert.ThrowsAsync<KernelException>(() => Service.SetPreferenceAsync(f.As(m, "dana"), "tenant__route_resolved", "*", true, default))).Status);
    }

    [Fact]
    public async Task Two_routes_to_the_same_person_and_channel_deliver_once_and_replays_are_explicit()
    {
        var t = await TenantAsync("ntd", [.. ResolvedRoute(), new AddNotificationRoute
        {
            Route = new NotificationRouteSpec
            {
                Id = "tenant__route_again",
                Label = "Again",
                Subscription = "tenant__sub_resolved",
                Profile = PlatformModule.InAppProfile,
                Template = "tenant__tpl_resolved",
                Recipients = [new RecipientSpec { Kind = RecipientKinds.Field, Ref = "requester" }],
            },
        }]);
        await ResolveTicketAsync(t);
        var inApp = Assert.Single(await DeliveriesAsync(t), d => d.ChannelType == "in_app");

        var admin = f.As(t, "admin");
        var notForced = await Assert.ThrowsAsync<KernelException>(() => Service.ReplayAsync(admin, inApp.Id, force: false, default));
        Assert.Equal("not_replayable", notForced.Code);
        var copy = await Service.ReplayAsync(admin, inApp.Id, force: true, default);
        await PumpAsync();
        var replayed = (await DeliveriesAsync(t)).Single(d => d.Id == copy);
        Assert.Equal((inApp.Id, DeliveryStatuses.Sent), (replayed.ReplayOf!.Value, replayed.Status));
        Assert.Equal(2, (await Service.InboxAsync(f.As(t, "dana"), false, null, default)).Items.Count(i => i.Subject == inApp.Subject));

        // Agents and people without the right cannot replay or inspect bodies.
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Service.ReplayAsync(f.As(t, "agent"), inApp.Id, true, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Service.DeliveriesAsync(f.As(t, "dana"), null, null, default))).Status);
        Assert.All(await Service.DeliveriesAsync(admin, null, null, default), d => Assert.NotNull(d.Body));
    }

    [Fact]
    public async Task Channel_settings_hold_no_secrets_and_live_sends_need_allowed_hosts_and_the_right()
    {
        var t = await f.NewTenantAsync("ntch", "1.6.0");
        var admin = f.As(t, "admin");
        var secret = await Assert.ThrowsAsync<KernelException>(() => Service.SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "E", "capture", new JsonObject { ["password"] = "hunter2" }, null, null), default));
        Assert.Equal("secret_in_settings", secret.Code);
        var host = await Assert.ThrowsAsync<KernelException>(() => Service.SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "E", "live", new JsonObject { ["host"] = "10.0.0.5", ["from"] = "a@b.co" }, null, null), default));
        Assert.Equal("host_not_allowed", host.Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Service.SaveChannelAsync(f.As(t, "builder"), new SaveChannelRequest("email", "email", "E", "capture", null, null, null), default))).Status);
        var identity = f.Get<IdentityService>();
        var agentId = await identity.EnsureAgentAsync(t.Users["admin"], t.TenantId, default);
        var agent = f.As(t, "admin", agent: agentId, scopes: new HashSet<string>(AgentScopes.All));
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Service.ChannelsAsync(agent, default))).Status);

        var saved = await Service.SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "E", "capture", new JsonObject { ["host"] = "smtp.example.com" }, ["SMTP_PASSWORD"], 30), default);
        Assert.False(saved.Secrets["SMTP_PASSWORD"]);
        Assert.False(saved.HostAllowed);
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(await Service.ChannelsAsync(admin, default)));
    }

    [Fact]
    public async Task Delivery_states_follow_the_provider_transient_retries_and_unknown_is_ambiguous()
    {
        var t = await TenantAsync("nts", ResolvedRoute(profile: PlatformModule.InAppEmailProfile));
        var admin = f.As(t, "admin");
        var db = f.Get<Database>();
        await Service.SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "E", "capture", new JsonObject { ["host"] = "smtp.example.com", ["from"] = "d@example.com" }, null, 60), default);
        // Switch the instance to live directly (allow-listing is an operator setting; this test drives a fake adapter).
        await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.NotificationChannels.ExecuteUpdateAsync(s => s.SetProperty(c => c.Mode, "live")), default);
        var dispatcher = f.Get<OutboxDispatcher>();
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Flaky", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low" }, null, null, default);
        var started = await records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        await records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Done" }, null, null, default);
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }

        // Resolve deliveries without running the real SMTP adapter.
        var resolve = f.Get<IEnumerable<IJobHandler>>().OfType<NotifyResolveJobHandler>().Single();
        var job = await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.Jobs.AsNoTracking().FirstAsync(j => j.Kind == "notify.resolve"), default);
        await resolve.RunAsync(new JobContext(db, job.Id, t.TenantId, t.Environments["prod"], job.Kind, 1, null, JsonNode.Parse(job.Payload)!.AsObject(), null, "test"), default);
        var email = Assert.Single(await DeliveriesAsync(t), d => d.ChannelType == "email" && d.Subject == "Closed out: Flaky");

        async Task<NotificationDeliveryRow> Deliver(INotificationChannel channel)
        {
            var handler = new NotifyDeliverJobHandler([channel], f.Get<ISecretStore>(), f.Get<ConfigStore>(), f.Get<AccessService>(), f.Get<IClock>());
            try
            {
                await handler.RunAsync(new JobContext(db, Guid.NewGuid(), t.TenantId, t.Environments["prod"], "notify.deliver", 1, null, new JsonObject { ["delivery"] = email.Id.ToString() }, null, "test"), default);
            }
            catch (InvalidOperationException)
            {
                // transient: the job runner would retry with backoff
            }

            return (await DeliveriesAsync(t)).Single(d => d.Id == email.Id);
        }

        Assert.Equal(DeliveryStatuses.Queued, (await Deliver(new FakeEmail(ChannelResult.Transient("smtp_421"), idempotent: false))).Status);
        var ambiguous = await Deliver(new FakeEmail(ChannelResult.Unknown("connection_lost"), idempotent: false));
        Assert.Equal(DeliveryStatuses.Ambiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.Attempts);
        // SMTP has no idempotency: sending an ambiguous message again needs an explicit force.
        Assert.Equal("ambiguous_delivery", (await Assert.ThrowsAsync<KernelException>(() => Service.ReplayAsync(admin, email.Id, force: false, default))).Code);
        var replay = await Service.ReplayAsync(admin, email.Id, force: true, default);
        Assert.Equal(email.Id, replay);
        var sent = await Deliver(new FakeEmail(ChannelResult.Sent("msg-1"), idempotent: false));
        Assert.Equal((DeliveryStatuses.Sent, "msg-1"), (sent.Status, sent.ProviderMessageId));
        var attempts = await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.NotificationDeliveryAttempts.CountAsync(a => a.DeliveryId == email.Id), default);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Deliveries_check_the_recipient_again_and_never_override_a_mute_or_reach_other_workspaces()
    {
        var t = await TenantAsync("ntre", ResolvedRoute(profile: PlatformModule.InAppProfile));
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var db = f.Get<Database>();
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Moved on", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low" }, null, null, default);
        var started = await records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        var resolved = await records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Done" }, null, null, default);
        while (await f.Get<OutboxDispatcher>().DispatchOnceAsync(default) > 0)
        {
        }

        // Resolved for Dana; before delivery the ticket becomes Jordan's, which Dana (a requester) may no longer read.
        var resolve = f.Get<IEnumerable<IJobHandler>>().OfType<NotifyResolveJobHandler>().Single();
        foreach (var job in await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.Jobs.AsNoTracking().Where(j => j.Kind == "notify.resolve").ToListAsync(), default))
        {
            await resolve.RunAsync(new JobContext(db, job.Id, t.TenantId, t.Environments["prod"], job.Kind, 1, null, JsonNode.Parse(job.Payload)!.AsObject(), null, "test"), default);
        }

        await records.UpdateAsync(agent, Ticket, ticket.Id, resolved.Revision, new JsonObject { ["requester"] = t.Users["jordan"].ToString() }, null, null, default);
        await PumpAsync();
        var stale = Assert.Single(await DeliveriesAsync(t), d => d.Subject == "Closed out: Moved on" && d.RecipientPrincipal == t.Users["dana"]);
        Assert.Equal((DeliveryStatuses.Suppressed, "no_longer_authorized"), (stale.Status, stale.StatusReason));
        Assert.DoesNotContain((await Service.InboxAsync(f.As(t, "dana"), false, null, default)).Items, i => i.Subject == "Closed out: Moved on");

        // Lost access and mutes are decisions: no replay overrides them, forced or not.
        var admin = f.As(t, "admin");
        Assert.Equal("not_replayable", (await Assert.ThrowsAsync<KernelException>(() => Service.ReplayAsync(admin, stale.Id, force: true, default))).Code);
        await Service.SetPreferenceAsync(f.As(t, "dana"), "tenant__route_resolved", "*", true, default);
        await ResolveTicketAsync(t, "Quiet please");
        var muted = Assert.Single(await DeliveriesAsync(t), d => d.Subject == "Closed out: Quiet please");
        Assert.Equal("muted", muted.StatusReason);
        Assert.Equal("not_replayable", (await Assert.ThrowsAsync<KernelException>(() => Service.ReplayAsync(admin, muted.Id, force: true, default))).Code);

        // Channels and deliveries belong to one workspace environment.
        await Service.SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "E", "capture", new JsonObject { ["host"] = "smtp.example.com" }, null, null), default);
        var other = f.As(f.Globex, "morgan");
        Assert.DoesNotContain(await Service.DeliveriesAsync(other, null, null, default), d => d.Id == muted.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Service.ReplayAsync(other, muted.Id, true, default))).Status);
        var sandbox = f.As(t, "admin", env: "sandbox");
        Assert.Empty(await Service.ChannelsAsync(sandbox, default));
        Assert.Empty(await Service.DeliveriesAsync(sandbox, null, null, default));
    }

    [Fact]
    public async Task A_mandatory_route_reaches_a_person_who_muted_another_route_for_the_same_event()
    {
        var t = await f.NewTenantAsync("ntmx", "1.7.0");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyBatchAsync(admin,
        [
            new AddSubscription { Subscription = new SubscriptionSpec { Id = "tenant__sub_p1", Label = "P1 heads-up", Event = "core.tickets.ticket.priority_raised" } },
            new AddNotificationTemplate { Template = new NotificationTemplateSpec { Id = "tenant__tpl_p1", Entity = Ticket, Subject = "Heads up: {{record.title}}", Body = "{{link}}" } },
            new AddNotificationRoute { Route = new NotificationRouteSpec { Id = "tenant__route_p1", Label = "P1 heads-up", Subscription = "tenant__sub_p1", Profile = PlatformModule.InAppProfile, Template = "tenant__tpl_p1", Recipients = [new RecipientSpec { Kind = RecipientKinds.Field, Ref = "assignee" }] } },
        ], draft.Version, "ui", default);
        await f.Get<PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);
        var agent = f.As(t, "agent");
        await Service.SetPreferenceAsync(agent, "tenant__route_p1", "*", true, default);

        var records = f.Get<RecordService>();
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Storage full", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "high", ["priority"] = "p3", ["assignee"] = t.Users["agent"].ToString() }, null, null, default);
        await records.UpdateAsync(agent, Ticket, ticket.Id, ticket.Revision, new JsonObject { ["priority"] = "p1" }, null, null, default);
        while (await f.Get<OutboxDispatcher>().DispatchOnceAsync(default) > 0)
        {
        }

        // The muted optional route resolves first and reaches the agent's in-app key before the mandatory one.
        var db = f.Get<Database>();
        var resolve = f.Get<IEnumerable<IJobHandler>>().OfType<NotifyResolveJobHandler>().Single();
        var optional = (await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.Jobs.AsNoTracking().Where(j => j.Kind == "notify.resolve").ToListAsync(), default))
            .Where(j => j.Payload.Contains("tenant__sub_p1", StringComparison.Ordinal));
        foreach (var first in optional)
        {
            await resolve.RunAsync(new JobContext(db, first.Id, t.TenantId, t.Environments["prod"], first.Kind, 1, null, JsonNode.Parse(first.Payload)!.AsObject(), null, "test"), default);
        }

        Assert.Contains(await DeliveriesAsync(t), d => d.RecordId == ticket.Id && d.RecipientPrincipal == t.Users["agent"] && d.StatusReason == "muted");
        await PumpAsync();

        var key = Assert.Single(await DeliveriesAsync(t), d => d.RecordId == ticket.Id && d.RecipientPrincipal == t.Users["agent"] && d.ChannelType == "in_app");
        Assert.Equal(("core.tickets.route.p1_raised", DeliveryStatuses.Sent, true), (key.RouteId, key.Status, key.Mandatory));
    }

    [Fact]
    public async Task Previews_resolve_and_render_without_sending_or_writing()
    {
        var t = await TenantAsync("ntp", ResolvedRoute());
        var records = f.Get<RecordService>();
        var ticket = await records.CreateAsync(f.As(t, "agent"), Ticket, new JsonObject { ["title"] = "Preview me", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low" }, null, null, default);
        var before = (await DeliveriesAsync(t)).Count;
        var preview = await Service.PreviewAsync(f.As(t, "admin"), "tenant__route_resolved", ticket.Id, default);
        var dana = Assert.Single(preview.Recipients);
        Assert.Equal("Closed out: Preview me", dana.Subject);
        Assert.Equal(["in_app", "email"], dana.Channels);
        Assert.Equal(before, (await DeliveriesAsync(t)).Count);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Service.PreviewAsync(f.As(t, "dana"), "tenant__route_resolved", ticket.Id, default))).Status);
    }

    [Fact]
    public async Task Upgrading_tickets_adds_a_mandatory_p1_alert_and_keeps_the_workspace_routes()
    {
        // Tickets 1.7.0: escalations and P1 alerts become module routes. A workspace with its own route upgrades without
        // conflicts, a ticket becoming P1 reaches its assignee and team, and nobody can mute that alert.
        var t = await TenantAsync("ntup", ResolvedRoute());
        var admin = f.As(t, "admin");
        var upgrades = f.Get<UpgradeService>();
        var plan = await upgrades.PlanAsync(admin, "core.tickets", "1.7.0", default);
        Assert.True(plan.Compatible, string.Join("; ", plan.Errors.Select(e => e.Message)));
        Assert.All(plan.Overlay, o => Assert.Equal(OpStatus.Applied, o.Status));
        Assert.Contains(plan.BaseChanges, c => c.Id == "core.tickets.route.p1_raised" && c.Change == "added");
        var started = await upgrades.StartAsync(admin, "core.tickets", "1.7.0", default);
        await upgrades.RunAsync(t.TenantId, t.Environments["prod"], started.Id, default);
        Assert.Equal("activated", (await upgrades.GetAsync(admin, started.Id, default)).Status);

        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Core switch down", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "high", ["priority"] = "p3", ["assignee"] = t.Users["agent"].ToString() }, null, null, default);
        await records.UpdateAsync(agent, Ticket, ticket.Id, ticket.Revision, new JsonObject { ["priority"] = "p1" }, null, null, default);
        await PumpAsync();

        var p1 = (await DeliveriesAsync(t)).Where(d => d.RouteId == "core.tickets.route.p1_raised").ToList();
        Assert.Contains(p1, d => d.RecipientPrincipal == t.Users["agent"] && d.ChannelType == "in_app" && d.Status == DeliveryStatuses.Sent && d.Mandatory);
        var number = (await records.GetAsync(agent, Ticket, ticket.Id, default)).Number;
        Assert.Contains((await Service.InboxAsync(agent, false, null, default)).Items, i => i.Subject == $"P1: {number} Core switch down");
        Assert.Equal(409, (await Assert.ThrowsAsync<KernelException>(() => Service.SetPreferenceAsync(agent, "core.tickets.route.p1_raised", "*", true, default))).Status);

        // The workspace's own route survived the upgrade and still delivers.
        await ResolveTicketAsync(t, "After the upgrade");
        Assert.Contains(await DeliveriesAsync(t), d => d.RouteId == "tenant__route_resolved" && d.Subject == "Closed out: After the upgrade");
    }

    private sealed class FakeEmail(ChannelResult result, bool idempotent) : INotificationChannel
    {
        public string Type => "email";

        public bool SupportsIdempotency => idempotent;

        public Task<ChannelResult> SendAsync(ChannelMessage message, JsonObject settings, ISecretAccessor secrets, string idempotencyKey, CancellationToken ct) => Task.FromResult(result);
    }
}
