using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Notifications;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Notifications;

public static class DeliveryStatuses
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Ambiguous = "ambiguous";
    public const string Captured = "captured";
    public const string Suppressed = "suppressed";

    public static bool IsFinal(string status) => status is Sent or Failed or Ambiguous or Captured or Suppressed;
}

/// <summary>
/// The subscription router (ADR 0017, ADR 0021): matches each event against enabled subscriptions and schedules one
/// durable <c>notify.resolve</c> job per match. It never sends anything; failures of resolution or delivery never block
/// other consumers or the publisher.
/// </summary>
public sealed class SubscriptionRouter(ConfigStore configs) : IEventConsumer
{
    public const string ResolveKind = "notify.resolve";

    public string Name => "notifications.router";

    public bool Handles(EventEnvelope envelope) => true;

    public async Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, envelope.TenantId, envelope.EnvironmentId, ct);
        foreach (var sub in await MatchAsync(db, artifact.Config, envelope, SubscriptionTargets.Notify, ct))
        {
            await JobScheduler.ScheduleAsync(db, envelope.TenantId, envelope.EnvironmentId, new JobRequest(ResolveKind, new JsonObject { ["event"] = envelope.EventId.ToString(), ["subscription"] = sub.Id })
            {
                DedupKey = $"notify:{envelope.EventId}:{sub.Id}",
                CausationId = envelope.EventId,
                Depth = envelope.Depth + 1,
            }, ct);
        }
    }

    /// <summary>The enabled subscriptions of a target whose event, entity and condition match an event.</summary>
    internal static async Task<List<SubscriptionSpec>> MatchAsync(KernelDbContext db, EffectiveConfig config, EventEnvelope envelope, string target, CancellationToken ct)
    {
        var candidates = config.Subscriptions.Where(s => s.Enabled && s.Target == target && s.Event == envelope.Type).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var type = config.FindEventType(envelope.Type);
        JsonObject? record = null;
        if (envelope.RecordId is { } recordId)
        {
            var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
            record = row is null ? null : JsonNode.Parse(row.Data)!.AsObject();
        }

        var matches = new List<SubscriptionSpec>();
        foreach (var sub in candidates)
        {
            var entityId = sub.Entity ?? type?.SubjectEntity;
            if (entityId is not null && envelope.Entity != entityId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sub.When) && !Matches(sub, config, entityId, type, envelope, record))
            {
                continue;
            }

            matches.Add(sub);
        }

        return matches;
    }

    /// <summary>Expression variables for an event: <c>event</c> (its payload) and <c>record</c> (with <c>id</c>).</summary>
    internal static Dictionary<string, object?> Variables(EventEnvelope envelope, JsonObject? record)
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["event"] = Values.MapFromJson(envelope.Payload) };
        if (record is not null)
        {
            var withId = record.DeepClone().AsObject();
            withId["id"] = envelope.RecordId?.ToString();
            vars["record"] = Values.MapFromJson(withId);
        }

        return vars;
    }

    internal static bool Matches(SubscriptionSpec sub, EffectiveConfig config, string? entityId, EventTypeSpec? type, EventEnvelope envelope, JsonObject? record)
    {
        try
        {
            var entity = entityId is null ? null : config.FindEntity(entityId);
            var schema = type is null ? null : EntitySchema.ForSubscription(entity, type);
            var compiled = Expression.Compile(sub.When!, schema);
            return compiled.Evaluate(Variables(envelope, record)) is true;
        }
        catch (ExpressionException)
        {
            return false; // validated at publish; a condition that fails at run time never notifies
        }
    }
}

/// <summary>
/// Resolves one subscription match into deliveries (ADR 0021): the subscription's routes, their recipients, the
/// profile's channels and severity filter, people's mutes (except on mandatory routes), deduplication on event, person
/// or address, and channel, and rendering for each recipient under their own permissions. Idempotent.
/// </summary>
public sealed class NotifyResolveJobHandler(ConfigStore configs, AccessService access, NotificationOptions options) : IJobHandler
{
    public const string DeliverKind = "notify.deliver";

    public string Kind => SubscriptionRouter.ResolveKind;

    public string? RequiredAction => null;

    internal sealed record Target(Guid? Principal, string? Name, string? Email, string? Address, bool External);

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            var eventId = Guid.Parse(job.Payload["event"]!.GetValue<string>());
            var outbox = await db.Outbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == eventId, ct);
            if (outbox is null)
            {
                return true; // purged after retention
            }

            var envelope = EventWriter.Read(outbox);
            var artifact = await configs.GetActiveAsync(db, job.TenantId, job.EnvironmentId, ct);
            var config = artifact.Config;
            if (config.FindSubscription(job.Payload["subscription"]!.GetValue<string>()) is not { Enabled: true } sub)
            {
                return true;
            }

            var type = config.FindEventType(envelope.Type);
            var entityId = sub.Entity ?? type?.SubjectEntity ?? envelope.Entity;
            var entity = entityId is null ? null : config.FindEntity(entityId);
            JsonObject? record = null;
            string? number = null;
            if (envelope.RecordId is { } recordId)
            {
                var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId && r.DeletedAt == null, ct);
                if (row is null)
                {
                    return true;
                }

                record = JsonNode.Parse(row.Data)!.AsObject();
                number = entity?.Fields.Find(f => f.Type == FieldTypes.AutoNumber) is { } nf && record[nf.Key] is JsonValue nv ? nv.GetValue<string>() : null;
            }

            var severity = envelope.Severity ?? type?.Severity ?? EventSeverities.Info;
            // A missing sensitivity counts as restricted (ASM-053).
            var sensitivity = envelope.Sensitivity ?? type?.Sensitivity ?? EventSensitivities.Restricted;
            var link = entity is not null && envelope.RecordId is { } rid ? $"{options.WebUrl.TrimEnd('/')}/records/{entity.Id}/{rid}" : options.WebUrl;
            // Other subscriptions of the same event may already have reached someone (one job per subscription).
            var existing = await db.NotificationDeliveries.Where(d => d.EventId == eventId)
                .Select(d => new { d.IdempotencyKey, d.Id, d.Status, d.StatusReason })
                .ToDictionaryAsync(d => d.IdempotencyKey, d => (Guid?)(d.Status == DeliveryStatuses.Suppressed && d.StatusReason == "muted" ? d.Id : null), StringComparer.Ordinal, ct);
            var instances = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);
            var permissionCache = new Dictionary<Guid, EffectivePermissions?>();
            var created = new List<NotificationDeliveryRow>();
            var reached = existing.Count;

            // A public reply's text may be shown ({{activity.body}}); its author is not told about their own reply.
            // Internal work notes and redacted replies never reach a template (ADR 0023, REQ-BIZ-002).
            var (replyText, replyAuthor) = await ReplyAsync(db, envelope, ct);

            // Mandatory routes first, then the tenant layer before the module layer: the most specific route wins a duplicate.
            var routes = config.NotificationRoutes.Where(r => r.Enabled && r.Subscription == sub.Id)
                .OrderByDescending(r => r.Mandatory)
                .ThenBy(r => config.Provenance.GetValueOrDefault(r.Id, Composer.TenantLayer) == Composer.TenantLayer ? 0 : 1)
                .ToList();
            foreach (var route in routes)
            {
                if (config.FindNotificationProfile(route.Profile) is not { Enabled: true } profile || config.FindNotificationTemplate(route.Template) is not { Enabled: true } template)
                {
                    continue;
                }

                if (profile.MinSeverity is { } min && EventSeverities.Rank(severity) < EventSeverities.Rank(min))
                {
                    continue;
                }

                var mutes = await db.NotificationPreferences.AsNoTracking().Where(p => p.RouteId == route.Id && p.Muted).ToListAsync(ct);
                var targets = await TargetsAsync(db, route, entity, record, envelope, ct);
                if (targets.Count > options.MaxRecipientsPerEvent)
                {
                    // Bound the permission checks too, not only the deliveries.
                    AuditWriter.AddSystem(db, job.TenantId, job.EnvironmentId, "notification.recipients_capped", "event", eventId.ToString(), new { route = route.Id, limit = options.MaxRecipientsPerEvent }, outcome: "failed");
                    targets = targets.Take(options.MaxRecipientsPerEvent).ToList();
                }

                foreach (var target in targets)
                {
                    EffectivePermissions? perms = null;
                    if (replyAuthor is not null && target.Principal == replyAuthor)
                    {
                        continue;
                    }

                    if (target.Principal is { } principal)
                    {
                        if (!permissionCache.TryGetValue(principal, out perms))
                        {
                            try
                            {
                                perms = await access.ForAsync(db, new RequestContext { TenantId = job.TenantId, EnvironmentId = job.EnvironmentId, PrincipalId = principal, Channel = "worker" }, config, ct);
                            }
                            catch (KernelException)
                            {
                                perms = null; // not a member (any more)
                            }

                            permissionCache[principal] = perms;
                        }

                        // People hear only about records they may read.
                        if (perms is null || (entity is not null && record is not null && !perms.CanRead(entity, record)))
                        {
                            continue;
                        }
                    }

                    var channels = profile.Channels.Where(c => Reachable(c, target)).ToList();
                    if (profile.Delivery == NotificationDelivery.Fallback)
                    {
                        channels = channels.Take(1).ToList();
                    }

                    foreach (var channel in channels)
                    {
                        var who = target.Principal?.ToString() ?? target.Address!.ToLowerInvariant();
                        var key = $"{eventId}:{who}:{channel}";
                        NotificationDeliveryRow? replacing = null;
                        if (existing.TryGetValue(key, out var mutedRow))
                        {
                            // A mandatory route reaches the person even if a muted optional route of another
                            // subscription got there first (REQ-NOT-004); every other duplicate is skipped.
                            if (!route.Mandatory || mutedRow is null)
                            {
                                continue;
                            }

                            replacing = await db.NotificationDeliveries.AsTracking().FirstAsync(d => d.Id == mutedRow, ct);
                            existing[key] = null;
                        }
                        else
                        {
                            existing[key] = null;
                        }

                        if (++reached > options.MaxRecipientsPerEvent)
                        {
                            AuditWriter.AddSystem(db, job.TenantId, job.EnvironmentId, "notification.recipients_capped", "event", eventId.ToString(), new { route = route.Id, limit = options.MaxRecipientsPerEvent }, outcome: "failed");
                            await SaveAndScheduleAsync(db, job, created, eventId, ct);
                            return true;
                        }

                        var muted = !route.Mandatory && target.Principal is { } p && mutes.Any(m => m.PrincipalId == p && (m.ChannelType == "*" || m.ChannelType == channel));
                        var instance = channel == NotificationChannels.InApp ? null : instances.FirstOrDefault(i => i.Type == channel);
                        var (subject, body) = NotificationRenderer.Render(template, channel, entity, record, number, envelope.Payload, sensitivity, link, new RenderScope(perms, target.External, target.Name) { Reply = replyText });
                        var (status, reason) = muted ? (DeliveryStatuses.Suppressed, "muted")
                            : channel != NotificationChannels.InApp && instance is null ? (DeliveryStatuses.Suppressed, "channel_not_configured")
                            : (DeliveryStatuses.Queued, (string?)null);
                        if (replacing is not null)
                        {
                            replacing.SubscriptionId = sub.Id;
                            replacing.RouteId = route.Id;
                            replacing.TemplateId = template.Id;
                            replacing.ProfileId = profile.Id;
                            replacing.ChannelKey = instance?.Key;
                            replacing.Mandatory = true;
                            replacing.Status = status;
                            replacing.StatusReason = reason;
                            replacing.Subject = subject;
                            replacing.Body = body;
                            replacing.ConfigRevision = artifact.Revision;
                            replacing.UpdatedAt = DateTimeOffset.UtcNow;
                            created.Add(replacing);
                            continue;
                        }

                        var delivery = new NotificationDeliveryRow
                        {
                            TenantId = job.TenantId,
                            EnvironmentId = job.EnvironmentId,
                            Id = Guid.NewGuid(),
                            EventId = eventId,
                            EventType = envelope.Type,
                            SubscriptionId = sub.Id,
                            RouteId = route.Id,
                            TemplateId = template.Id,
                            ProfileId = profile.Id,
                            ChannelType = channel,
                            ChannelKey = instance?.Key,
                            RecipientPrincipal = target.Principal,
                            RecipientAddress = channel == NotificationChannels.Email ? target.Email ?? target.Address : channel == NotificationChannels.Sms ? target.Address : null,
                            External = target.External,
                            Mandatory = route.Mandatory,
                            Severity = severity,
                            EntityId = entity?.Id,
                            RecordId = envelope.RecordId,
                            Status = status,
                            StatusReason = reason,
                            IdempotencyKey = key,
                            Subject = subject,
                            Body = body,
                            ConfigRevision = artifact.Revision,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        };
                        db.NotificationDeliveries.Add(delivery);
                        created.Add(delivery);
                    }
                }
            }

            await SaveAndScheduleAsync(db, job, created, eventId, ct);
            return true;
        }, ct);

    private static async Task SaveAndScheduleAsync(KernelDbContext db, JobContext job, List<NotificationDeliveryRow> created, Guid eventId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        foreach (var d in created.Where(d => d.Status == DeliveryStatuses.Queued))
        {
            await JobScheduler.ScheduleAsync(db, job.TenantId, job.EnvironmentId, new JobRequest(DeliverKind, new JsonObject { ["delivery"] = d.Id.ToString() })
            {
                DedupKey = $"{DeliverKind}:{d.Id}",
                CausationId = eventId,
            }, ct);
        }
    }

    internal static bool Reachable(string channel, Target t) => channel switch
    {
        NotificationChannels.InApp => t.Principal is not null,
        NotificationChannels.Email => t.Email is not null || t.Address?.Contains('@') == true,
        NotificationChannels.Sms => t.Address?.StartsWith('+') == true,
        _ => false,
    };

    /// <summary>The people and addresses a route names for this event (people only if they are workspace members).</summary>
    internal static async Task<List<Target>> TargetsAsync(KernelDbContext db, NotificationRouteSpec route, EntitySpec? entity, JsonObject? record, EventEnvelope envelope, CancellationToken ct)
    {
        var people = new HashSet<Guid>();
        var groups = new HashSet<Guid>();
        var addresses = new List<string>();
        foreach (var r in route.Recipients)
        {
            switch (r.Kind)
            {
                case RecipientKinds.User when Guid.TryParse(r.Ref, out var u):
                    people.Add(u);
                    break;
                case RecipientKinds.Group:
                    groups.UnionWith(await db.Groups.AsNoTracking().Where(g => g.Key == r.Ref).Select(g => g.Id).ToListAsync(ct));
                    break;
                case RecipientKinds.Role:
                    foreach (var a in await db.RoleAssignments.AsNoTracking().Where(x => x.RoleId == r.Ref).ToListAsync(ct))
                    {
                        if (a.PrincipalId is { } p)
                        {
                            people.Add(p);
                        }
                        else if (a.GroupId is { } g)
                        {
                            groups.Add(g);
                        }
                    }

                    break;
                case RecipientKinds.Field when entity?.FindField(r.Ref) is { } field && record is not null && record[r.Ref] is { } value:
                    var values = value is JsonArray arr ? arr.OfType<JsonValue>().Select(v => v.ToString()).ToList() : [value.ToString()];
                    foreach (var v in values)
                    {
                        if (field.Type == FieldTypes.Email)
                        {
                            addresses.Add(v);
                        }
                        else if (Guid.TryParse(v, out var id))
                        {
                            if (field.Type == FieldTypes.Group)
                            {
                                groups.Add(id);
                            }
                            else
                            {
                                // user fields hold people; principals fields hold people or groups
                                people.Add(id);
                                groups.Add(id);
                            }
                        }
                    }

                    break;
                case RecipientKinds.Address:
                    addresses.Add(r.Ref);
                    break;
                // Only on alert events the platform published (defence in depth; the validator allows it nowhere else).
                case RecipientKinds.AlertOwner when EventTypes.IsAlert(envelope.Type) && envelope.Source == Alerts.AlertEvaluateJobHandler.Source && envelope.Payload["alert"]?.GetValue<string>() is { } alertRef && Guid.TryParse(alertRef, out var alertId):
                    // The alert's owner group, owner and assignee (ADR 0022).
                    if (await db.Alerts.AsNoTracking().Where(a => a.Id == alertId).Select(a => new { a.OwnerGroup, a.OwnerPrincipal, a.Assignee }).FirstOrDefaultAsync(ct) is { } owners)
                    {
                        if (owners.OwnerGroup is { } og)
                        {
                            groups.Add(og);
                        }

                        foreach (var p in new[] { owners.OwnerPrincipal, owners.Assignee }.OfType<Guid>())
                        {
                            people.Add(p);
                        }
                    }

                    break;
                case RecipientKinds.Approvers when envelope.RecordId is { } recordId:
                    // The pending approvers of the record's approval (people, groups or roles; ADR 0016).
                    foreach (var step in await db.ApprovalSteps.AsNoTracking().Where(x => x.RecordId == recordId && x.Status == "pending").ToListAsync(ct))
                    {
                        if (step.ApproverKind == "user" && Guid.TryParse(step.ApproverRef, out var approver))
                        {
                            people.Add(approver);
                        }
                        else if (step.ApproverKind == "group" && Guid.TryParse(step.ApproverRef, out var approverGroup))
                        {
                            groups.Add(approverGroup);
                        }
                        else if (step.ApproverKind == "role")
                        {
                            foreach (var a in await db.RoleAssignments.AsNoTracking().Where(x => x.RoleId == step.ApproverRef).ToListAsync(ct))
                            {
                                if (a.PrincipalId is { } p)
                                {
                                    people.Add(p);
                                }
                                else if (a.GroupId is { } g)
                                {
                                    groups.Add(g);
                                }
                            }
                        }
                    }

                    break;
            }
        }

        if (groups.Count > 0)
        {
            people.UnionWith(await db.GroupMembers.AsNoTracking().Where(m => groups.Contains(m.GroupId) && m.PrincipalId != null).Select(m => m.PrincipalId!.Value).ToListAsync(ct));
        }

        // Members of this workspace who are people (not agents or services).
        var members = await db.Memberships.AsNoTracking().Where(m => people.Contains(m.PrincipalId)).Select(m => m.PrincipalId).ToListAsync(ct);
        var principals = await db.Principals.AsNoTracking().Where(p => members.Contains(p.Id) && p.Kind == "user" && !p.Disabled).Select(p => new { p.Id, p.DisplayName, p.Email }).ToListAsync(ct);
        var targets = principals.Select(p => new Target(p.Id, p.DisplayName, p.Email, null, false)).ToList();
        foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // An address of a workspace member is that person (internal); anything else is external.
            if (principals.FirstOrDefault(p => string.Equals(p.Email, address, StringComparison.OrdinalIgnoreCase)) is null)
            {
                targets.Add(new Target(null, null, null, address, true));
            }
        }

        return targets;
    }

    /// <summary>The public reply an <c>activity.added</c> event is about (text and author), or nothing for anything else.</summary>
    internal static async Task<(string? Text, Guid? Author)> ReplyAsync(KernelDbContext db, EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Type != EventTypes.ActivityAdded || envelope.Payload["kind"]?.GetValue<string>() != "public_reply"
            || envelope.Payload["activityId"]?.GetValue<string>() is not { } raw || !Guid.TryParse(raw, out var activityId))
        {
            return (null, null);
        }

        var reply = await db.Activity.AsNoTracking()
            .Where(a => a.Id == activityId && a.RecordId == envelope.RecordId && a.Kind == "public_reply" && a.RedactedAt == null)
            .Select(a => new { a.Body, a.Author })
            .FirstOrDefaultAsync(ct);
        return reply is null ? (null, null) : (NotificationRenderer.PlainText(reply.Body), reply.Author);
    }
}

/// <summary>
/// Delivers one notification (ADR 0021): the in-app inbox inside the database, or a provider through the channel
/// instance of the environment (capture and disabled modes never send), with a per-instance rate limit, attempts
/// recorded, retries on transient failures and an honest <c>ambiguous</c> when a send may or may not have happened.
/// </summary>
public sealed class NotifyDeliverJobHandler(IEnumerable<INotificationChannel> channels, ISecretStore secrets, ConfigStore configs, AccessService access, IClock clock) : IJobHandler
{
    public string Kind => NotifyResolveJobHandler.DeliverKind;

    public string? RequiredAction => null;

    private sealed record Send(NotificationDeliveryRow Delivery, JsonObject Settings, string TenantSlug, string EnvironmentKey, IReadOnlySet<string> Declared, IReadOnlyList<string> References, string? ReplyTo);

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var id = Guid.Parse(job.Payload["delivery"]!.GetValue<string>());
        var send = await job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            var d = (await db.NotificationDeliveries.FromSqlInterpolated($"SELECT * FROM kernel.notification_deliveries WHERE id = {id} FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
            if (d is null || DeliveryStatuses.IsFinal(d.Status))
            {
                return null;
            }

            var adapter = d.ChannelType == NotificationChannels.InApp ? null : channels.FirstOrDefault(c => c.Type == d.ChannelType);
            if (d.Status == DeliveryStatuses.Sending && adapter is { SupportsIdempotency: false })
            {
                // A previous attempt stopped mid-send: the provider may have the message. Say so instead of sending twice.
                Finish(db, d, DeliveryStatuses.Ambiguous, "interrupted", "unknown");
                await db.SaveChangesAsync(ct);
                return null;
            }

            // Text was rendered when the event was resolved; by now the person may have left, been disabled or lost
            // access to the record (retries, rate waits, replays). Check again, and use their current address.
            if (d.RecipientPrincipal is { } person)
            {
                var current = await db.Principals.AsNoTracking().Where(p => p.Id == person && p.Kind == "user" && !p.Disabled).Select(p => new { p.Email }).FirstOrDefaultAsync(ct);
                var member = current is not null && await db.Memberships.AnyAsync(m => m.PrincipalId == person, ct);
                var allowed = member;
                if (allowed && d.RecordId is { } recordId && d.EntityId is { } entityId)
                {
                    var artifact = await configs.GetActiveAsync(db, job.TenantId, job.EnvironmentId, ct);
                    var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId && r.DeletedAt == null, ct);
                    try
                    {
                        var perms = await access.ForAsync(db, new RequestContext { TenantId = job.TenantId, EnvironmentId = job.EnvironmentId, PrincipalId = person, Channel = "worker" }, artifact.Config, ct, fresh: true);
                        allowed = row is not null && artifact.Config.FindEntity(entityId) is { } entity && perms.CanRead(entity, JsonNode.Parse(row.Data)!.AsObject());
                    }
                    catch (KernelException)
                    {
                        allowed = false;
                    }
                }

                if (!allowed)
                {
                    Finish(db, d, DeliveryStatuses.Suppressed, "no_longer_authorized", "suppressed");
                    await db.SaveChangesAsync(ct);
                    return null;
                }

                if (d.ChannelType == NotificationChannels.Email)
                {
                    d.RecipientAddress = current!.Email;
                }
            }

            if (d.ChannelType == NotificationChannels.InApp)
            {
                db.Notifications.Add(new NotificationRow
                {
                    TenantId = d.TenantId,
                    EnvironmentId = d.EnvironmentId,
                    Id = Guid.NewGuid(),
                    TemplateId = d.TemplateId,
                    Recipient = d.RecipientPrincipal,
                    Subject = d.Subject,
                    Body = d.Body,
                    // A forced replay is a second item for the same event, so it is keyed by the delivery itself.
                    EventId = d.ReplayOf is null ? d.EventId : d.Id,
                    Channel = NotificationChannels.InApp,
                    CreatedAt = clock.UtcNow,
                    DeliveryId = d.Id,
                    Severity = d.Severity,
                    EntityId = d.EntityId,
                    RecordId = d.RecordId,
                });
                Finish(db, d, DeliveryStatuses.Sent, null, "sent");
                await db.SaveChangesAsync(ct);
                return null;
            }

            var instance = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Key == d.ChannelKey, ct);
            if (instance is null || instance.Mode == "disabled" || adapter is null)
            {
                Finish(db, d, DeliveryStatuses.Suppressed, instance is null || adapter is null ? "channel_not_configured" : "channel_disabled", "suppressed");
                await db.SaveChangesAsync(ct);
                return null;
            }

            // Email threading (ADR 0023): an opaque Message-ID per delivery, kept so a reply finds the record.
            var settings = JsonNode.Parse(instance.Settings) as JsonObject ?? [];
            if (d.ChannelType == NotificationChannels.Email && d.MessageId is null)
            {
                d.MessageId = Mail.MailThreading.NewMessageId(settings["from"]?.GetValue<string>());
            }

            if (instance.Mode == "capture")
            {
                // Sandboxes capture by default: the rendered message is kept for inspection and never leaves the platform.
                Finish(db, d, DeliveryStatuses.Captured, null, "captured");
                await db.SaveChangesAsync(ct);
                return null;
            }

            // One deliverer per instance at a time decides on the rate (the lock lasts until this transaction marks the
            // delivery as sending), and messages being sent count too.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"notify:" + job.TenantId + ":" + job.EnvironmentId + ":" + instance.Key}, 0))", ct);
            var since = clock.UtcNow.AddMinutes(-1);
            var sentRecently = await db.NotificationDeliveries.CountAsync(x => x.ChannelKey == instance.Key && x.Id != d.Id && ((x.SentAt != null && x.SentAt > since) || (x.Status == DeliveryStatuses.Sending && x.UpdatedAt > since)), ct);
            if (sentRecently >= instance.RatePerMinute)
            {
                // Over the instance's rate: try again shortly without spending an attempt.
                await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "succeeded").SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
                await JobScheduler.ScheduleAsync(db, job.TenantId, job.EnvironmentId, new JobRequest(Kind, job.Payload.DeepClone().AsObject()) { DedupKey = $"{Kind}:{d.Id}", RunAt = clock.UtcNow.AddSeconds(20) }, ct);
                return null;
            }

            d.Status = DeliveryStatuses.Sending;
            d.Attempts++;
            d.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            var slug = await db.Tenants.Where(t => t.Id == job.TenantId).Select(t => t.Slug).FirstAsync(ct);
            var environment = await db.Environments.Where(e => e.Id == job.EnvironmentId).Select(e => e.Key).FirstAsync(ct);
            var declared = (JsonNode.Parse(instance.SecretRefs) as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var (references, replyTo) = d.ChannelType == NotificationChannels.Email ? await Mail.MailThreading.ForRecordAsync(db, configs, job.TenantId, job.EnvironmentId, d.EntityId, d.RecordId, ct) : ([], null);
            return new Send(d, settings, slug, environment, declared, references, replyTo);
        }, ct);

        if (send is null)
        {
            return;
        }

        var delivery = send.Delivery;
        var adapter = channels.First(c => c.Type == delivery.ChannelType);
        ChannelResult result;
        try
        {
            var message = new ChannelMessage(delivery.RecipientAddress ?? string.Empty, delivery.Subject, delivery.Body, null) { MessageId = delivery.MessageId, References = send.References, ReplyTo = send.ReplyTo };
            result = await adapter.SendAsync(message, send.Settings, new DeclaredSecrets(secrets.For(send.TenantSlug, send.EnvironmentKey), send.Declared), delivery.IdempotencyKey, ct);
        }
        catch (Exception)
        {
            result = ChannelResult.Unknown("adapter_error"); // never log the exception: it may carry provider responses
        }

        var retry = await job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            var d = await db.NotificationDeliveries.AsTracking().FirstAsync(x => x.Id == delivery.Id, ct);
            d.ProviderMessageId = result.ProviderMessageId ?? d.ProviderMessageId;
            var retryable = result.Outcome == ChannelResult.TransientOutcome || (result.Outcome == ChannelResult.UnknownOutcome && adapter.SupportsIdempotency);
            var status = result.Outcome switch
            {
                ChannelResult.SentOutcome => DeliveryStatuses.Sent,
                ChannelResult.PermanentOutcome => DeliveryStatuses.Failed,
                _ when retryable => DeliveryStatuses.Queued,
                _ => DeliveryStatuses.Ambiguous,
            };
            Finish(db, d, status, result.Code, result.Outcome, result.ProviderMessageId);
            await db.SaveChangesAsync(ct);
            return retryable;
        }, ct);

        if (retry)
        {
            throw new InvalidOperationException("Transient delivery failure; retrying with backoff.");
        }
    }

    /// <summary>Adapters read only the secret names the channel instance declares, so the channel page shows all it uses.</summary>
    private sealed class DeclaredSecrets(ISecretAccessor inner, IReadOnlySet<string> declared) : ISecretAccessor
    {
        public string? Get(string name) => declared.Contains(name) ? inner.Get(name) : null;
    }

    /// <summary>
    /// When retries run out, a queued delivery is failed; one caught mid-send is ambiguous, since the provider may have
    /// it (so a replay needs an explicit force on channels without idempotency).
    /// </summary>
    public Task OnAbandonedAsync(JobContext job, string reason, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            var id = Guid.Parse(job.Payload["delivery"]!.GetValue<string>());
            await db.NotificationDeliveries.Where(d => d.Id == id && d.Status == DeliveryStatuses.Sending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatuses.Ambiguous).SetProperty(d => d.StatusReason, "interrupted").SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return await db.NotificationDeliveries.Where(d => d.Id == id && d.Status == DeliveryStatuses.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatuses.Failed).SetProperty(d => d.StatusReason, "retries_exhausted").SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }, ct);

    private void Finish(KernelDbContext db, NotificationDeliveryRow d, string status, string? reason, string outcome, string? providerId = null)
    {
        d.Status = status;
        d.StatusReason = reason;
        d.UpdatedAt = clock.UtcNow;
        if (status == DeliveryStatuses.Sent)
        {
            d.SentAt = clock.UtcNow;
        }

        if (d.ChannelType == NotificationChannels.InApp || outcome is "captured" or "suppressed")
        {
            d.Attempts = Math.Max(d.Attempts, status == DeliveryStatuses.Sent ? 1 : 0);
        }

        db.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttemptRow
        {
            TenantId = d.TenantId,
            EnvironmentId = d.EnvironmentId,
            Id = Guid.NewGuid(),
            DeliveryId = d.Id,
            Attempt = Math.Max(d.Attempts, 1),
            Outcome = outcome,
            Code = reason,
            ProviderMessageId = providerId,
            At = clock.UtcNow,
        });
    }
}
