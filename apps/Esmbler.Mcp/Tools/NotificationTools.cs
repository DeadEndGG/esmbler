using System.ComponentModel;
using Esmbler.Kernel;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Esmbler.Mcp.Tools;

/// <summary>A recipient rule for notification routes (ADR 0021).</summary>
public sealed class RecipientInput
{
    [Description("field (a user, group or email field of the record), role (role ID), group (group key), user (principal ID), approvers (pending approvers of an approval event) or address (an external email address or phone number).")]
    public required string Kind { get; set; }

    [Description("Field key, role ID, group key, principal ID or address; for approvers use \"pending\".")]
    public required string Ref { get; set; }
}

/// <summary>Per-channel text of a template.</summary>
public sealed class TemplateVariantInput
{
    [Description("in_app, email or sms.")]
    public required string Channel { get; set; }

    public string? Subject { get; set; }

    public required string Body { get; set; }
}

/// <summary>
/// Notification tools (REQ-NOT-001..006, ADR 0021). Subscriptions, templates, profiles and routes are configuration the
/// AI builder edits in the draft. Mandatory routes and external recipients are permission changes that only people with
/// permission administration make, so agents are refused them. Channel settings, secrets and replays are for people.
/// </summary>
[McpServerToolType]
public sealed class NotificationTools(ToolRunner runner, DraftService drafts, PreviewService preview, NotificationService notifications)
{
    [McpServerTool(Name = "list_notification_setup", ReadOnly = true), Description("Notifications in the open draft: subscriptions (which events), templates (the text), profiles (which channels) and routes (who hears about it, how, with which template; mandatory routes cannot be muted), plus the channel types this environment can deliver on.")]
    public Task<CallToolResult> ListNotificationSetup() =>
        runner.RunAsync("list_notification_setup", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            IReadOnlyList<ChannelSummary>? channels = null;
            try
            {
                channels = await notifications.ChannelSummaryAsync(ctx, default);
            }
            catch (KernelException ex) when (ex.Status == 403)
            {
                // Without configuration rights the agent still sees the configuration; channel readiness is omitted.
            }

            return new
            {
                draftVersion,
                channels = channels?.Select(c => new { c.Type, c.Mode, ready = c.HostAllowed && c.SecretsConfigured }),
                channelNote = "in_app always works. email and sms need a channel set up by a person in Notification channels; capture mode records messages without sending them.",
                subscriptions = config.Subscriptions.Select(s => new { s.Id, s.Label, s.Event, s.Entity, s.When, s.Enabled }),
                templates = config.NotificationTemplates.Where(t => t.Event is null).Select(t => new
                {
                    t.Id,
                    t.Label,
                    t.Entity,
                    t.Subject,
                    t.Body,
                    variants = t.Variants.Count == 0 ? null : t.Variants,
                    externalFields = t.ExternalFields.Count == 0 ? null : t.ExternalFields,
                    t.Enabled,
                }),
                legacyTemplates = config.NotificationTemplates.Where(t => t.Event is not null).Select(t => new { t.Id, trigger = t.Event, t.Entity, t.Recipients, t.Subject, t.Enabled }),
                profiles = config.NotificationProfiles.Select(p => new { p.Id, p.Label, p.Channels, p.Delivery, p.MinSeverity, p.Enabled }),
                routes = config.NotificationRoutes.Select(r => new
                {
                    r.Id,
                    r.Label,
                    r.Subscription,
                    r.Profile,
                    r.Template,
                    recipients = r.Recipients.Select(x => $"{x.Kind}:{x.Ref}"),
                    r.Mandatory,
                    r.Enabled,
                    layer = r.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal) ? "workspace" : "module",
                }),
            };
        });

    [McpServerTool(Name = "preview_notification", ReadOnly = true), Description("Who a route of the draft would notify and what each person would read, for one record (optional), without sending or saving anything. Text shows only fields both the recipient and the current user may see. Pass a record only with the records:read scope.")]
    public Task<CallToolResult> PreviewNotification(
        [Description("Route ID from list_notification_setup.")] string route,
        [Description("Optional record ID to render with.")] Guid? record = null) =>
        runner.RunAsync("preview_notification", AgentScopes.ConfigRead, route, async ctx =>
        {
            if (record is not null && !ctx.HasScope(AgentScopes.RecordsRead))
            {
                throw new KernelException("scope_missing", "Previewing with a record needs the records:read scope.", 403, "record", "Preview without a record, or ask the user to grant records:read.");
            }

            var result = await notifications.PreviewAsync(ctx, route, record, default);
            return ToolRunner.Untrusted(result);
        });

    [McpServerTool(Name = "list_notification_deliveries", ReadOnly = true), Description("The latest notification deliveries (status, channel, reason, attempts) to check whether something went out. Needs audit access; message text and full addresses are never shown here.")]
    public Task<CallToolResult> ListNotificationDeliveries(
        [Description("Optional status: queued, sending, sent, failed, ambiguous, captured or suppressed.")] string? status = null,
        [Description("Optional event ID.")] Guid? @event = null) =>
        runner.RunAsync("list_notification_deliveries", AgentScopes.ConfigRead, status, async ctx =>
        {
            var rows = await notifications.DeliveriesAsync(ctx, status, @event, default);
            return rows.Take(50).Select(d => new { d.Id, d.EventId, d.EventType, d.Route, d.Channel, d.Recipient, d.External, d.Status, d.Reason, d.Attempts, d.CreatedAt, d.SentAt });
        });

    [McpServerTool(Name = "notify_when"), Description("The simplest way to set up a notification: when an event happens (optionally under a condition), tell these recipients through a profile's channels with this text. Adds a subscription, a template and a route as one change. Example: when core.tickets.ticket.escalated, tell field:assignee and group:service_desk \"Escalated: {{record.title}}\". Placeholders: {{record.<field>}}, {{record.number}}, {{event.<field>}}, {{recipient.name}}, {{link}}; plain text only.")]
    public Task<CallToolResult> NotifyWhen(
        [Description("What the notification is, e.g. \"Escalations to the assignee\".")] string label,
        [Description("Event type ID (list_event_types).")] string @event,
        [Description("Who hears about it.")] RecipientInput[] recipients,
        string subject,
        string body,
        int expected_version,
        [Description("Optional condition over event and record, e.g. record.priority == 'p1'.")] string? when = null,
        [Description("Profile ID (list_notification_setup), e.g. core.platform.profile.in_app_email. Default: in the app only.")] string? profile = null) =>
        runner.RunAsync("notify_when", AgentScopes.ConfigEdit, @event, async ctx =>
        {
            var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
            var entity = config.FindEventType(@event)?.SubjectEntity;
            var id = Id(label, 44);
            var ops = new List<OverlayOp>
            {
                new AddSubscription { Subscription = new SubscriptionSpec { Id = id + "_sub", Label = label, Event = @event, When = when } },
                new AddNotificationTemplate { Template = new NotificationTemplateSpec { Id = id + "_text", Label = label, Entity = entity, Subject = subject, Body = body } },
                new AddNotificationRoute
                {
                    Route = new NotificationRouteSpec
                    {
                        Id = id,
                        Label = label,
                        Subscription = id + "_sub",
                        Template = id + "_text",
                        Profile = profile ?? PlatformModule.InAppProfile,
                        Recipients = [.. recipients.Select(ToRecipient)],
                    },
                },
            };
            return await ApplyAllAsync(ctx, ops, expected_version);
        });

    [McpServerTool(Name = "add_subscription"), Description("Listen to an event type (optionally under a condition over event and record). Routes then decide who hears about it.")]
    public Task<CallToolResult> AddSubscription(
        string label,
        [Description("Event type ID (list_event_types).")] string @event,
        int expected_version,
        [Description("Optional condition, e.g. event.priority == 'p1'.")] string? when = null,
        string? description = null) =>
        runner.RunAsync("add_subscription", AgentScopes.ConfigEdit, @event, async ctx =>
            await ApplyAllAsync(ctx, [new AddSubscription { Subscription = new SubscriptionSpec { Id = Id(label, 56), Label = label, Description = description, Event = @event, When = when } }], expected_version));

    [McpServerTool(Name = "add_notification_template"), Description("Add the text of a notification: subject and body with placeholders ({{record.<field>}}, {{record.number}}, {{event.<field>}}, {{recipient.name}}, {{link}}), plain text only, and optional shorter text per channel (e.g. sms). Each recipient sees only fields they may read.")]
    public Task<CallToolResult> AddNotificationTemplate(
        string label,
        string subject,
        string body,
        int expected_version,
        [Description("Entity whose fields the placeholders read, e.g. core.tickets.ticket.")] string? entity = null,
        TemplateVariantInput[]? variants = null) =>
        runner.RunAsync("add_notification_template", AgentScopes.ConfigEdit, label, async ctx =>
            await ApplyAllAsync(ctx, [new AddNotificationTemplate
            {
                Template = new NotificationTemplateSpec { Id = Id(label, 56), Label = label, Entity = entity, Subject = subject, Body = body, Variants = Variants(variants) ?? [] },
            }], expected_version));

    [McpServerTool(Name = "update_notification_template"), Description("Change a template's label, subject, body, per-channel text or enabled state (module templates too; upgrades keep your text). Which fields external recipients see is a permission change people make.")]
    public Task<CallToolResult> UpdateNotificationTemplate(
        string template,
        int expected_version,
        string? label = null,
        string? subject = null,
        string? body = null,
        [Description("Replaces all per-channel text.")] TemplateVariantInput[]? variants = null,
        bool? enabled = null) =>
        runner.RunAsync("update_notification_template", AgentScopes.ConfigEdit, template, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateNotificationTemplate
            {
                Template = template,
                Set = new NotificationTemplatePatch { Label = label, Subject = subject, Body = body, Variants = Variants(variants), Enabled = enabled },
            }], expected_version));

    [McpServerTool(Name = "add_notification_profile"), Description("Add a delivery profile: channels in order (in_app, email, sms), all of them or the first that reaches the person (fallback), and an optional minimum event severity.")]
    public Task<CallToolResult> AddNotificationProfile(
        string label,
        string[] channels,
        int expected_version,
        [Description("all (default) or fallback.")] string? delivery = null,
        [Description("info, low, medium, high or critical.")] string? min_severity = null) =>
        runner.RunAsync("add_notification_profile", AgentScopes.ConfigEdit, label, async ctx =>
            await ApplyAllAsync(ctx, [new AddNotificationProfile
            {
                Profile = new NotificationProfileSpec { Id = Id(label, 56), Label = label, Channels = [.. channels], Delivery = delivery ?? NotificationDelivery.All, MinSeverity = min_severity },
            }], expected_version));

    [McpServerTool(Name = "add_notification_route"), Description("Route a subscription's events to recipients through a profile with a template. Mandatory routes and external addresses are permission changes only people with permission administration make; agents are refused.")]
    public Task<CallToolResult> AddNotificationRoute(
        string label,
        string subscription,
        string profile,
        string template,
        RecipientInput[] recipients,
        int expected_version) =>
        runner.RunAsync("add_notification_route", AgentScopes.ConfigEdit, subscription, async ctx =>
            await ApplyAllAsync(ctx, [new AddNotificationRoute
            {
                Route = new NotificationRouteSpec { Id = Id(label, 56), Label = label, Subscription = subscription, Profile = profile, Template = template, Recipients = [.. recipients.Select(ToRecipient)] },
            }], expected_version));

    [McpServerTool(Name = "update_notification_route"), Description("Change a route: label, profile, template, recipients (your routes) or enabled. Module routes accept profile, template and enabled; mandatory module routes cannot be disabled. Making a route mandatory or optional is a permission change people make.")]
    public Task<CallToolResult> UpdateNotificationRoute(
        string route,
        int expected_version,
        string? label = null,
        string? profile = null,
        string? template = null,
        [Description("Replaces the recipients.")] RecipientInput[]? recipients = null,
        bool? enabled = null) =>
        runner.RunAsync("update_notification_route", AgentScopes.ConfigEdit, route, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateNotificationRoute
            {
                Route = route,
                Set = new NotificationRoutePatch { Label = label, Profile = profile, Template = template, Recipients = recipients?.Select(ToRecipient).ToList(), Enabled = enabled },
            }], expected_version));

    private static RecipientSpec ToRecipient(RecipientInput r) => new() { Kind = r.Kind, Ref = r.Ref };

    private static Dictionary<string, TemplateVariantSpec>? Variants(TemplateVariantInput[]? variants)
    {
        if (variants is null)
        {
            return null;
        }

        var result = new Dictionary<string, TemplateVariantSpec>(StringComparer.Ordinal);
        foreach (var v in variants)
        {
            if (!result.TryAdd(v.Channel, new TemplateVariantSpec { Subject = v.Subject, Body = v.Body }))
            {
                throw new KernelException("duplicate_variant", $"Two texts for channel '{v.Channel}'.", 400, "variants", "Give each channel at most one text.");
            }
        }

        return result;
    }

    private static string Id(string label, int max)
    {
        var slug = BuildTools.Slug(label);
        return Namespaces.TenantPrefix + slug[..Math.Min(max, slug.Length)];
    }

    private async Task<object> ApplyAllAsync(RequestContext ctx, IReadOnlyList<OverlayOp> ops, int expectedVersion)
    {
        var result = await drafts.ApplyBatchAsync(ctx, ops, expectedVersion, "mcp", default);
        return new
        {
            version = result.Version,
            steps = result.Steps.Select(s => new { step = s.Seq, s.Summary, s.Targets, warnings = s.Warnings.Count == 0 ? null : s.Warnings }),
            note = result.Steps.Count > 1 ? "These steps are one change: undoing any of them undoes all of them." : null,
        };
    }
}
