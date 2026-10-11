using System.ComponentModel;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Alerts;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Esmbler.Mcp.Tools;

/// <summary>An escalation step for add_alert_rule and update_alert_rule.</summary>
public sealed class EscalationStepInput
{
    [Description("Minutes after the alert opened, increasing from step to step (1 to 10080).")]
    public required int AfterMinutes { get; set; }

    [Description("Skip the step once someone acknowledged the alert. Default true.")]
    public bool? UnlessAcknowledged { get; set; }

    [Description("Last step only: repeat every this many minutes (at most 20 times).")]
    public int? RepeatEveryMinutes { get; set; }
}

/// <summary>
/// Alert tools (REQ-ALR-001, ADR 0022). Alert rules are configuration the AI builder edits in the draft. Agents may read
/// alerts (with records:read) but never acknowledge, resolve, assign or silence them: that is a person's attestation.
/// </summary>
[McpServerToolType]
public sealed class AlertTools(ToolRunner runner, DraftService drafts, PreviewService preview, AlertService alerts)
{
    private static readonly string[] AlertEvents = [Sdk.Events.EventTypes.AlertOpened, Sdk.Events.EventTypes.AlertAcknowledged, Sdk.Events.EventTypes.AlertEscalated, Sdk.Events.EventTypes.AlertAssigned, Sdk.Events.EventTypes.AlertResolved];

    [McpServerTool(Name = "list_alert_rules", ReadOnly = true), Description("Alert rules in the open draft: the events that raise each (trigger subscription and minimum severity), its key, title, owner, escalation steps and the events that resolve it. Alerts publish platform.alert.* events; notification routes on those events tell people.")]
    public Task<CallToolResult> ListAlertRules() =>
        runner.RunAsync("list_alert_rules", AgentScopes.ConfigRead, null, async ctx =>
        {
            var (config, _, draftVersion, _) = await preview.WorkingConfigAsync(ctx, default);
            return new
            {
                draftVersion,
                rules = config.AlertRules.Select(r => new
                {
                    r.Id,
                    r.Label,
                    r.Enabled,
                    trigger = config.FindSubscription(r.Trigger) is { } t ? new { t.Id, t.Event, t.Entity, t.When } : null,
                    r.MinSeverity,
                    r.Severity,
                    r.DedupKey,
                    r.Title,
                    owner = r.Owner is null ? null : $"{r.Owner.Kind}:{r.Owner.Ref}",
                    escalation = r.Escalation.Select(s => new { s.AfterMinutes, s.UnlessAcknowledged, s.RepeatEveryMinutes }),
                    resolveOn = r.ResolveOn.Select(id => config.FindSubscription(id) is { } s ? new { s.Id, Event = (string?)s.Event, s.When } : new { Id = id, Event = (string?)null, When = (string?)null }),
                    r.ReopenWithinMinutes,
                    layer = r.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal) ? "workspace" : "module",
                }),
                alertEvents = AlertEvents,
                note = "Route alert events to people with notify_when on a platform.alert.* event and the recipient alert_owner:alert (the alert's owners and assignee).",
            };
        });

    [McpServerTool(Name = "add_alert_rule"), Description("Raise alerts from an event, e.g. \"Critical escalations\": one alert per key (by default per record) that counts repeats, belongs to an owner, escalates on timers until someone acknowledges it, and resolves when a resolving event happens (e.g. the ticket is resolved). Adds the trigger subscription, the resolving subscription and the rule as one change. Alerts never send messages: use notify_when on platform.alert.opened or platform.alert.escalated (recipient alert_owner) for that.")]
    public Task<CallToolResult> AddAlertRule(
        [Description("What the alert is, e.g. \"Critical escalation\".")] string label,
        [Description("Event type ID that raises it (list_event_types), e.g. core.tickets.ticket.escalated.")] string @event,
        [Description("Alert title: plain text with {{record.<field>}}, {{record.number}} and {{event.<field>}}, at most 200 characters.")] string title,
        int expected_version,
        [Description("Optional condition over event and record, e.g. record.category == 'network'.")] string? when = null,
        [Description("Only events at least this severe: info, low, medium, high or critical.")] string? min_severity = null,
        [Description("Owner: group:<key>, field:<user or group field> or user:<principal ID>, e.g. group:network or field:assignment_group.")] string? owner = null,
        [Description("Escalation steps in order.")] EscalationStepInput[]? escalation = null,
        [Description("Optional event type that resolves the rule's alerts about the same record, e.g. record.transitioned.")] string? resolve_on_event = null,
        [Description("Condition for the resolving event, e.g. record.status in ['resolved', 'closed'].")] string? resolve_when = null,
        [Description("Optional key expression over event and record; default one alert per record.")] string? dedup_key = null) =>
        runner.RunAsync("add_alert_rule", AgentScopes.ConfigEdit, @event, async ctx =>
        {
            var slug = BuildTools.Slug(label);
            var id = Namespaces.TenantPrefix + slug[..Math.Min(44, slug.Length)];
            var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
            var entity = config.FindEventType(@event)?.SubjectEntity;
            var ops = new List<OverlayOp>
            {
                new AddSubscription { Subscription = new SubscriptionSpec { Id = id + "_trigger", Label = label, Event = @event, When = when, Target = SubscriptionTargets.Alert } },
            };
            var resolveOn = new List<string>();
            if (resolve_on_event is not null)
            {
                ops.Add(new AddSubscription { Subscription = new SubscriptionSpec { Id = id + "_resolve", Label = label + " (resolve)", Event = resolve_on_event, Entity = entity, When = resolve_when, Target = SubscriptionTargets.Alert } });
                resolveOn.Add(id + "_resolve");
            }

            ops.Add(new AddAlertRule
            {
                Rule = new AlertRuleSpec
                {
                    Id = id,
                    Label = label,
                    Trigger = id + "_trigger",
                    MinSeverity = min_severity,
                    DedupKey = dedup_key,
                    Title = title,
                    Owner = Owner(owner),
                    Escalation = [.. (escalation ?? []).Select(Step)],
                    ResolveOn = resolveOn,
                },
            });
            return await ApplyAllAsync(ctx, ops, expected_version);
        });

    [McpServerTool(Name = "update_alert_rule"), Description("Change an alert rule: label, title, minimum severity, owner, escalation steps or enabled. Module rules keep their trigger and key. Turning off a rule that a mandatory or external alert route relies on is a permission change a person publishes.")]
    public Task<CallToolResult> UpdateAlertRule(
        string rule,
        int expected_version,
        string? label = null,
        string? title = null,
        string? min_severity = null,
        [Description("group:<key>, field:<field> or user:<principal ID>.")] string? owner = null,
        [Description("Replaces all escalation steps.")] EscalationStepInput[]? escalation = null,
        bool? enabled = null) =>
        runner.RunAsync("update_alert_rule", AgentScopes.ConfigEdit, rule, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateAlertRule
            {
                Rule = rule,
                Set = new AlertRulePatch { Label = label, Title = title, MinSeverity = min_severity, Owner = owner is null ? null : Owner(owner), Escalation = escalation?.Select(Step).ToList(), Enabled = enabled },
            }], expected_version));

    [McpServerTool(Name = "list_alerts", ReadOnly = true), Description("Current alerts the user may see (open and acknowledged by default): title, severity, owner, level, occurrences and age. Needs records:read; titles carry record data. You cannot acknowledge, resolve or assign alerts: tell the user what to do.")]
    public Task<CallToolResult> ListAlerts([Description("active (default), open, acknowledged, suppressed or resolved.")] string? status = null) =>
        runner.RunAsync("list_alerts", AgentScopes.RecordsRead, status, async ctx =>
        {
            var page = await alerts.ListAsync(ctx, status ?? "active", mine: false, 50, 0, default);
            return ToolRunner.Untrusted(page.Items.Select(a => new { a.Id, a.RuleLabel, a.Status, a.Severity, a.Title, a.Entity, a.RecordId, a.OwnerGroup, a.Assignee, a.Level, a.Occurrences, a.OpenedAt, a.LastOccurredAt }));
        });

    private static AlertOwnerSpec? Owner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return null;
        }

        var parts = owner.Split(':', 2);
        return parts.Length == 2
            ? new AlertOwnerSpec { Kind = parts[0], Ref = parts[1] }
            : throw new KernelException("invalid_owner", $"'{owner}' is not an owner.", 400, "owner", "Use group:<key>, field:<field> or user:<principal ID>.");
    }

    private static AlertEscalationStepSpec Step(EscalationStepInput s) =>
        new() { AfterMinutes = s.AfterMinutes, UnlessAcknowledged = s.UnlessAcknowledged ?? true, RepeatEveryMinutes = s.RepeatEveryMinutes };

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
