using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Expressions;
using Esmbler.Kernel.Alerts;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Alerts;

/// <summary>
/// The alert router (ADR 0022): matches each event against enabled alert-target subscriptions and schedules one durable
/// <c>alert.evaluate</c> job per match. A separate consumer from the notification router, so each fails and retries alone.
/// </summary>
public sealed class AlertRouter(ConfigStore configs) : IEventConsumer
{
    public const string EvaluateKind = "alert.evaluate";

    public string Name => "alerts.router";

    // Alert events never raise alerts (the validator refuses such triggers too).
    public bool Handles(EventEnvelope envelope) => !envelope.Type.StartsWith("platform.alert.", StringComparison.Ordinal);

    public async Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, envelope.TenantId, envelope.EnvironmentId, ct);
        if (artifact.Config.AlertRules.Count == 0)
        {
            return;
        }

        foreach (var sub in await SubscriptionRouter.MatchAsync(db, artifact.Config, envelope, SubscriptionTargets.Alert, ct))
        {
            await JobScheduler.ScheduleAsync(db, envelope.TenantId, envelope.EnvironmentId, new JobRequest(EvaluateKind, new JsonObject { ["event"] = envelope.EventId.ToString(), ["subscription"] = sub.Id })
            {
                DedupKey = $"alert:{envelope.EventId}:{sub.Id}",
                CausationId = envelope.EventId,
                Depth = envelope.Depth + 1,
            }, ct);
        }
    }
}

/// <summary>
/// Raises and resolves alerts for one subscription match (ADR 0022): rules triggered by the subscription open an alert
/// or count an occurrence on the active one with the same key (inside a maintenance window the alert is recorded as
/// suppressed and announced later); rules resolved by it resolve their active alerts about the same record or key.
/// Alerts publish <c>platform.alert.*</c> events and never send anything themselves. Idempotent per event.
/// </summary>
public sealed partial class AlertEvaluateJobHandler(ConfigStore configs, IClock clock) : IJobHandler
{
    public const string EscalateKind = "alert.escalate";
    public const string Source = "platform.alerts";
    private const int MaxKey = 200;
    private const int MaxTitle = 200;

    public string Kind => AlertRouter.EvaluateKind;

    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            var eventId = Guid.Parse(job.Payload["event"]!.GetValue<string>());
            var outbox = await db.Outbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == eventId, ct);
            if (outbox is null)
            {
                return true;
            }

            var envelope = EventWriter.Read(outbox);
            var artifact = await configs.GetActiveAsync(db, job.TenantId, job.EnvironmentId, ct);
            var config = artifact.Config;
            if (config.FindSubscription(job.Payload["subscription"]!.GetValue<string>()) is not { Enabled: true } sub)
            {
                return true;
            }

            var type = config.FindEventType(envelope.Type);
            var entity = ConfigValidator.SubscriptionEntity(config, sub) ?? (envelope.Entity is { } e ? config.FindEntity(e) : null);
            JsonObject? record = null;
            if (envelope.RecordId is { } recordId)
            {
                var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId && r.DeletedAt == null, ct);
                record = row is null ? null : JsonNode.Parse(row.Data)!.AsObject();
            }

            var ctx = new RequestContext { TenantId = job.TenantId, EnvironmentId = job.EnvironmentId, PrincipalId = Guid.Empty, Channel = "worker" };
            foreach (var rule in config.AlertRules.Where(r => r.Enabled && r.ResolveOn.Contains(sub.Id)))
            {
                await ResolveAsync(db, ctx, artifact.Revision, rule, envelope, entity, record, type, ct);
            }

            foreach (var rule in config.AlertRules.Where(r => r.Enabled && r.Trigger == sub.Id))
            {
                await RaiseAsync(db, ctx, job, artifact.Revision, rule, envelope, entity, record, type, ct);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    private async Task RaiseAsync(KernelDbContext db, RequestContext ctx, JobContext job, int revision, AlertRuleSpec rule, EventEnvelope envelope, EntitySpec? entity, JsonObject? record, EventTypeSpec? type, CancellationToken ct)
    {
        var severity = envelope.Severity ?? type?.Severity ?? EventSeverities.Info;
        if (rule.MinSeverity is { } min && EventSeverities.Rank(severity) < EventSeverities.Rank(min))
        {
            return;
        }

        var key = Key(rule, envelope, record);
        var now = clock.UtcNow;
        var active = (await db.Alerts.FromSqlInterpolated($"SELECT * FROM kernel.alerts WHERE rule_id = {rule.Id} AND dedup_key = {key} AND status IN ('open', 'acknowledged', 'suppressed') FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
        if (active is not null)
        {
            if (active.LastEventId == envelope.EventId)
            {
                return; // this event was counted already (retry)
            }

            var quiet = now - active.LastOccurredAt < TimeSpan.FromMinutes(1);
            active.Occurrences++;
            active.LastEventId = envelope.EventId;
            active.LastOccurredAt = now;
            active.UpdatedAt = now;
            active.Version++;
            if (active.Status == AlertStatuses.Suppressed && await SuppressionAsync(db, rule, entity, envelope.RecordId, now, ct) is null)
            {
                // The window ended and the problem is still there: announce it now.
                active.Status = AlertStatuses.Open;
                active.SuppressionId = null;
                active.OpenedAt = now;
                Activity(db, active, "unsuppressed", null, null);
                Publish(db, ctx, revision, active, EventTypes.AlertOpened, envelope.EventId);
                await ScheduleEscalationAsync(db, job, rule, active, ct);
            }
            else if (!quiet)
            {
                Activity(db, active, "occurred", null, new JsonObject { ["event"] = envelope.EventId.ToString(), ["occurrences"] = active.Occurrences });
            }

            return;
        }

        if (rule.ReopenWithinMinutes > 0)
        {
            var since = now.AddMinutes(-rule.ReopenWithinMinutes);
            var recent = (await db.Alerts.FromSqlInterpolated($"SELECT * FROM kernel.alerts WHERE rule_id = {rule.Id} AND dedup_key = {key} AND status = 'resolved' AND resolved_at > {since} ORDER BY resolved_at DESC LIMIT 1 FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
            if (recent is not null && recent.LastEventId != envelope.EventId && AlertLifecycle.Reopens(rule, recent.ResolvedAt!.Value, now))
            {
                recent.Status = AlertStatuses.Open;
                recent.ReopenedCount++;
                recent.Occurrences++;
                recent.Level = 1;
                recent.OpenedAt = now;
                recent.LastOccurredAt = now;
                recent.LastEventId = envelope.EventId;
                recent.ResolvedAt = null;
                recent.ResolvedBy = null;
                recent.Resolution = null;
                recent.AcknowledgedAt = null;
                recent.AcknowledgedBy = null;
                recent.UpdatedAt = now;
                recent.Version++;
                Activity(db, recent, "reopened", null, new JsonObject { ["event"] = envelope.EventId.ToString() });
                Publish(db, ctx, revision, recent, EventTypes.AlertOpened, envelope.EventId, reopened: true);
                await ScheduleEscalationAsync(db, job, rule, recent, ct);
                return;
            }
        }

        var (group, groupKey, principal) = await OwnerAsync(db, rule, record, ct);
        var suppression = await SuppressionAsync(db, rule, entity, envelope.RecordId, now, ct);
        var alert = new AlertRow
        {
            TenantId = job.TenantId,
            EnvironmentId = job.EnvironmentId,
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            DedupKey = key,
            Status = suppression is null ? AlertStatuses.Open : AlertStatuses.Suppressed,
            Severity = rule.Severity ?? severity,
            Title = Title(rule, envelope, record, entity),
            EntityId = entity?.Id ?? envelope.Entity,
            RecordId = envelope.RecordId,
            OwnerGroup = group,
            OwnerGroupKey = groupKey,
            OwnerPrincipal = principal,
            FirstEventId = envelope.EventId,
            LastEventId = envelope.EventId,
            SuppressionId = suppression,
            OpenedAt = now,
            LastOccurredAt = now,
            ConfigRevision = revision,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Alerts.Add(alert);
        Activity(db, alert, suppression is null ? "opened" : "suppressed", null, new JsonObject { ["event"] = envelope.EventId.ToString() });
        if (suppression is null)
        {
            Publish(db, ctx, revision, alert, EventTypes.AlertOpened, envelope.EventId);
            await ScheduleEscalationAsync(db, job, rule, alert, ct);
        }
    }

    private async Task ResolveAsync(KernelDbContext db, RequestContext ctx, int revision, AlertRuleSpec rule, EventEnvelope envelope, EntitySpec? entity, JsonObject? record, EventTypeSpec? type, CancellationToken ct)
    {
        List<AlertRow> alerts;
        if (envelope.RecordId is { } recordId)
        {
            alerts = await db.Alerts.FromSqlInterpolated($"SELECT * FROM kernel.alerts WHERE rule_id = {rule.Id} AND record_id = {recordId} AND status IN ('open', 'acknowledged', 'suppressed') FOR UPDATE").AsTracking().ToListAsync(ct);
        }
        else
        {
            var key = Key(rule, envelope, record);
            alerts = await db.Alerts.FromSqlInterpolated($"SELECT * FROM kernel.alerts WHERE rule_id = {rule.Id} AND dedup_key = {key} AND status IN ('open', 'acknowledged', 'suppressed') FOR UPDATE").AsTracking().ToListAsync(ct);
        }

        var now = clock.UtcNow;
        foreach (var alert in alerts)
        {
            var announced = alert.Status != AlertStatuses.Suppressed;
            alert.Status = AlertStatuses.Resolved;
            alert.Resolution = "auto";
            alert.ResolvedAt = now;
            alert.UpdatedAt = now;
            alert.Version++;
            Activity(db, alert, "resolved", null, new JsonObject { ["event"] = envelope.EventId.ToString(), ["resolution"] = "auto" });
            if (announced)
            {
                Publish(db, ctx, revision, alert, EventTypes.AlertResolved, envelope.EventId);
            }
        }
    }

    /// <summary>The rule's key for an event: its expression, or the record (or the event when there is none).</summary>
    internal static string Key(AlertRuleSpec rule, EventEnvelope envelope, JsonObject? record)
    {
        string? key = null;
        if (!string.IsNullOrWhiteSpace(rule.DedupKey))
        {
            try
            {
                key = Expression.Compile(rule.DedupKey).Evaluate(SubscriptionRouter.Variables(envelope, record)) switch
                {
                    null => null,
                    string s => s,
                    var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                };
            }
            catch (ExpressionException)
            {
                key = null; // validated at publish; fall back to the record
            }
        }

        key ??= envelope.RecordId?.ToString() ?? envelope.EventId.ToString();
        return key.Length <= MaxKey ? key : key[..MaxKey];
    }

    private static string Title(AlertRuleSpec rule, EventEnvelope envelope, JsonObject? record, EntitySpec? entity)
    {
        var number = entity?.Fields.Find(f => f.Type == FieldTypes.AutoNumber) is { } nf && record?[nf.Key] is JsonValue nv ? nv.ToString() : null;
        var text = Placeholder().Replace(rule.Title, m =>
        {
            var name = m.Groups[1].Value;
            if (name == "record.number")
            {
                return number ?? string.Empty;
            }

            JsonNode? value = name.StartsWith("record.", StringComparison.Ordinal) ? record?[name["record.".Length..]]
                : name.StartsWith("event.", StringComparison.Ordinal) ? envelope.Payload[name["event.".Length..]] : null;
            return value switch
            {
                null => string.Empty,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray a => string.Join(", ", a.Select(x => x?.ToString())),
                _ => value.ToJsonString(),
            };
        }).ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxTitle ? text : text[..(MaxTitle - 1)] + "…";
    }

    private static async Task<(Guid? Group, string? GroupKey, Guid? Principal)> OwnerAsync(KernelDbContext db, AlertRuleSpec rule, JsonObject? record, CancellationToken ct)
    {
        switch (rule.Owner)
        {
            case { Kind: AlertOwnerKinds.Group } owner:
                var group = await db.Groups.AsNoTracking().Where(g => g.Key == owner.Ref).Select(g => new { g.Id, g.Key }).FirstOrDefaultAsync(ct);
                return (group?.Id, group?.Key ?? owner.Ref, null);
            case { Kind: AlertOwnerKinds.User } owner when Guid.TryParse(owner.Ref, out var user):
                return (null, null, user);
            case { Kind: AlertOwnerKinds.Field } owner when record is not null && record[owner.Ref] is { } value:
                var first = value is JsonArray list ? list.FirstOrDefault()?.ToString() : value.ToString();
                if (!Guid.TryParse(first, out var id))
                {
                    return (null, null, null);
                }

                var asGroup = await db.Groups.AsNoTracking().Where(g => g.Id == id).Select(g => new { g.Id, g.Key }).FirstOrDefaultAsync(ct);
                return asGroup is not null ? (asGroup.Id, asGroup.Key, null) : (null, null, id);
            default:
                return (null, null, null);
        }
    }

    private static async Task<Guid?> SuppressionAsync(KernelDbContext db, AlertRuleSpec rule, EntitySpec? entity, Guid? recordId, DateTimeOffset now, CancellationToken ct) =>
        await db.AlertSuppressions.AsNoTracking()
            .Where(s => s.CancelledAt == null && s.StartsAt <= now && s.EndsAt > now
                && (s.RuleId == null || s.RuleId == rule.Id)
                && (s.EntityId == null || s.EntityId == (entity == null ? null : entity.Id))
                && (s.RecordId == null || s.RecordId == recordId))
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

    internal static async Task ScheduleEscalationAsync(KernelDbContext db, JobContext? job, AlertRuleSpec rule, AlertRow alert, CancellationToken ct)
    {
        if (AlertLifecycle.NextEscalation(rule, alert.OpenedAt, alert.Level) is not { } next)
        {
            return;
        }

        await JobScheduler.ScheduleAsync(db, alert.TenantId, alert.EnvironmentId, new JobRequest(EscalateKind, new JsonObject { ["alert"] = alert.Id.ToString(), ["level"] = next.Level })
        {
            RunAt = next.At,
            DedupKey = $"{EscalateKind}:{alert.Id}",
            ReplaceExisting = true,
            CausationId = alert.LastEventId,
        }, ct);
    }

    internal static void Activity(KernelDbContext db, AlertRow alert, string kind, RequestContext? actor, JsonObject? details) =>
        db.AlertActivity.Add(new AlertActivityRow
        {
            TenantId = alert.TenantId,
            EnvironmentId = alert.EnvironmentId,
            Id = Guid.NewGuid(),
            AlertId = alert.Id,
            Kind = kind,
            Actor = actor is null || actor.PrincipalId == Guid.Empty ? null : actor.PrincipalId,
            Agent = actor?.AgentId,
            Level = alert.Level,
            Details = details?.ToJsonString(),
            At = DateTimeOffset.UtcNow,
        });

    /// <summary>Publishes an alert event about the alert's subject record, so routes render and authorize against it.</summary>
    internal static void Publish(KernelDbContext db, RequestContext ctx, int revision, AlertRow alert, string type, Guid? causation, bool reopened = false) =>
        EventWriter.AddPlatform(db, ctx, type, Source, alert.EntityId, alert.RecordId, null, revision, new JsonObject
        {
            ["alert"] = alert.Id.ToString(),
            ["rule"] = alert.RuleId,
            ["title"] = alert.Title,
            ["severity"] = alert.Severity,
            ["level"] = alert.Level,
            ["occurrences"] = alert.Occurrences,
            ["owner_group"] = alert.OwnerGroupKey,
            ["assignee"] = alert.Assignee?.ToString(),
            ["reopened"] = reopened,
            ["resolution"] = alert.Resolution,
        }, alert.Severity, causation);

    [GeneratedRegex(@"\{\{\s*([a-z][a-z0-9_.]*)\s*\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// Raises an alert's level on its escalation timer (ADR 0022). The job completes itself before scheduling the next step
/// (a live job with the same key would otherwise absorb the new one); acknowledged alerts skip steps that wait for it.
/// </summary>
public sealed class AlertEscalateJobHandler(ConfigStore configs, IClock clock) : IJobHandler
{
    public string Kind => AlertEvaluateJobHandler.EscalateKind;

    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "succeeded").SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
            var id = Guid.Parse(job.Payload["alert"]!.GetValue<string>());
            var level = job.Payload["level"]!.GetValue<int>();
            var alert = (await db.Alerts.FromSqlInterpolated($"SELECT * FROM kernel.alerts WHERE id = {id} FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
            if (alert is null || alert.Status is AlertStatuses.Resolved or AlertStatuses.Suppressed || alert.Level >= level)
            {
                return true;
            }

            var artifact = await configs.GetActiveAsync(db, job.TenantId, job.EnvironmentId, ct);
            if (artifact.Config.FindAlertRule(alert.RuleId) is not { Enabled: true } rule)
            {
                return true;
            }

            if (AlertLifecycle.Escalates(rule, alert.Status, level))
            {
                alert.Level = level;
                alert.UpdatedAt = clock.UtcNow;
                alert.Version++;
                AlertEvaluateJobHandler.Activity(db, alert, "escalated", null, null);
                var ctx = new RequestContext { TenantId = job.TenantId, EnvironmentId = job.EnvironmentId, PrincipalId = Guid.Empty, Channel = "worker" };
                AlertEvaluateJobHandler.Publish(db, ctx, artifact.Revision, alert, EventTypes.AlertEscalated, alert.LastEventId);
                await AlertEvaluateJobHandler.ScheduleEscalationAsync(db, job, rule, alert, ct);
            }
            else if (AlertLifecycle.NextEscalation(rule, alert.OpenedAt, level) is { } next)
            {
                // This step waits for acknowledgment; later steps may not.
                await JobScheduler.ScheduleAsync(db, alert.TenantId, alert.EnvironmentId, new JobRequest(Kind, new JsonObject { ["alert"] = alert.Id.ToString(), ["level"] = next.Level })
                {
                    RunAt = next.At,
                    DedupKey = $"{Kind}:{alert.Id}",
                    ReplaceExisting = true,
                    CausationId = alert.LastEventId,
                }, ct);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
}
