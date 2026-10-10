using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Notifications;

public sealed record InboxItem(Guid Id, string Subject, string Body, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, string? Severity, string? Entity, Guid? RecordId);

public sealed record InboxPage(IReadOnlyList<InboxItem> Items, int Unread);

public sealed record PreferenceView(string Route, string Label, string Subscription, IReadOnlyList<string> Channels, bool Mandatory, IReadOnlyList<string> Muted);

public sealed record ChannelView(string Key, string Type, string Label, string Mode, JsonObject Settings, IReadOnlyDictionary<string, bool> Secrets, int RatePerMinute, bool HostAllowed, DateTimeOffset UpdatedAt);

/// <summary>Which channel types an environment can deliver on, without settings or secrets (for builders and agents).</summary>
public sealed record ChannelSummary(string Type, string Mode, bool HostAllowed, bool SecretsConfigured);

public sealed record SaveChannelRequest(string Key, string Type, string Label, string Mode, JsonObject? Settings, IReadOnlyList<string>? SecretRefs, int? RatePerMinute);

public sealed record DeliveryView(Guid Id, Guid EventId, string EventType, string Route, string Channel, string? Recipient, bool External, bool Mandatory, string Status, string? Reason, int Attempts, string? Subject, string? Body, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, Guid? ReplayOf);

public sealed record PreviewRecipient(string Recipient, bool External, IReadOnlyList<string> Channels, string? Suppressed, string Subject, string Body);

public sealed record NotificationPreview(string Route, string Template, string Profile, bool Mandatory, IReadOnlyList<PreviewRecipient> Recipients, string? Note);

/// <summary>
/// The notification service's API (ADR 0021, REQ-NOT-001..006): each person's inbox and preferences (only their own),
/// channel instances of the environment (settings and secret names only), delivery inspection and replay, test sends,
/// and previews that resolve and render without sending or writing anything.
/// </summary>
public sealed class NotificationService(Database database, ConfigStore configs, AccessService access, PreviewService preview, ISecretStore secrets, NotificationOptions options, IClock clock)
{
    private const int PageSize = 30;
    private static readonly string[] SecretLikeKeys = ["password", "secret", "token", "apikey", "api_key", "credential"];

    // ------------------------------------------------------------------ inbox (the signed-in person only)

    public Task<InboxPage> InboxAsync(RequestContext ctx, bool unreadOnly, DateTimeOffset? before, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            RefuseAgents(ctx);
            var mine = db.Notifications.AsNoTracking().Where(n => n.Recipient == ctx.PrincipalId);
            var page = mine.Where(n => !unreadOnly || n.ReadAt == null);
            if (before is { } b)
            {
                page = page.Where(n => n.CreatedAt < b);
            }

            var items = await page.OrderByDescending(n => n.CreatedAt).Take(PageSize)
                .Select(n => new InboxItem(n.Id, n.Subject, n.Body, n.CreatedAt, n.ReadAt, n.Severity, n.EntityId, n.RecordId)).ToListAsync(ct);
            return new InboxPage(items, await mine.CountAsync(n => n.ReadAt == null, ct));
        }, ct);

    public Task<int> UnreadAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, db =>
        {
            RefuseAgents(ctx);
            return db.Notifications.CountAsync(n => n.Recipient == ctx.PrincipalId && n.ReadAt == null, ct);
        }, ct);

    /// <summary>Marks one of the person's own items read (or all of them when <paramref name="id"/> is null).</summary>
    public Task<int> MarkReadAsync(RequestContext ctx, Guid? id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            RefuseAgents(ctx);
            var now = clock.UtcNow;
            var updated = await db.Notifications.Where(n => n.Recipient == ctx.PrincipalId && n.ReadAt == null && (id == null || n.Id == id))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
            if (id is not null && updated == 0 && !await db.Notifications.AnyAsync(n => n.Id == id && n.Recipient == ctx.PrincipalId, ct))
            {
                throw KernelException.NotFound("Notification");
            }

            return updated;
        }, ct);

    // ------------------------------------------------------------------ preferences (the user layer)

    public Task<IReadOnlyList<PreferenceView>> PreferencesAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<PreferenceView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            RefuseAgents(ctx);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var mutes = await db.NotificationPreferences.AsNoTracking().Where(p => p.PrincipalId == ctx.PrincipalId && p.Muted).ToListAsync(ct);
            return [.. artifact.Config.NotificationRoutes.Where(r => r.Enabled).Select(r => new PreferenceView(
                r.Id,
                r.Label,
                artifact.Config.FindSubscription(r.Subscription)?.Label ?? r.Subscription,
                artifact.Config.FindNotificationProfile(r.Profile)?.Channels ?? [],
                r.Mandatory,
                [.. mutes.Where(m => m.RouteId == r.Id).Select(m => m.ChannelType)]))];
        }, ct);

    /// <summary>Mutes or unmutes a route (channel "*") or one of its channels for the signed-in person. Mandatory routes refuse.</summary>
    public Task SetPreferenceAsync(RequestContext ctx, string routeId, string channel, bool muted, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            RefuseAgents(ctx);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var route = artifact.Config.FindNotificationRoute(routeId) ?? throw KernelException.NotFound($"Notification route '{routeId}'");
            if (channel != "*" && !NotificationChannels.All.Contains(channel))
            {
                throw new KernelException("invalid_channel", $"'{channel}' is not a channel.", 400, "channel", "Use *, in_app, email or sms.");
            }

            if (route.Mandatory && muted)
            {
                throw new KernelException("mandatory_route", $"\"{route.Label}\" is mandatory and cannot be muted.", 409, routeId, "Ask an administrator if you should not receive it.");
            }

            var row = await db.NotificationPreferences.AsTracking().FirstOrDefaultAsync(p => p.PrincipalId == ctx.PrincipalId && p.RouteId == routeId && p.ChannelType == channel, ct);
            if (row is null && muted)
            {
                db.NotificationPreferences.Add(new NotificationPreferenceRow { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, PrincipalId = ctx.PrincipalId, RouteId = routeId, ChannelType = channel, Muted = true, UpdatedAt = clock.UtcNow });
            }
            else if (row is not null && !muted)
            {
                db.NotificationPreferences.Remove(row);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    // ------------------------------------------------------------------ channel instances (environment settings)

    public Task<IReadOnlyList<ChannelView>> ChannelsAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<ChannelView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var slug = await db.Tenants.Where(t => t.Id == ctx.TenantId).Select(t => t.Slug).FirstAsync(ct);
            var accessor = secrets.For(slug);
            var rows = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);
            return [.. rows.Select(r => View(r, accessor))];
        }, ct);

    /// <summary>
    /// The channel instances of the environment as type, mode and readiness only (no settings, hosts or secret names), for
    /// people who build configuration and for AI agents planning routes.
    /// </summary>
    public Task<IReadOnlyList<ChannelSummary>> ChannelSummaryAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<ChannelSummary>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            if (!perms.Can(PlatformActions.ConfigEdit) && !CanManage(perms))
            {
                throw KernelException.Forbidden("Listing notification channels needs configuration or notification management rights.");
            }

            var slug = await db.Tenants.Where(t => t.Id == ctx.TenantId).Select(t => t.Slug).FirstAsync(ct);
            var accessor = secrets.For(slug);
            var rows = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);
            return [.. rows.Select(r => View(r, accessor)).Select(v => new ChannelSummary(v.Type, v.Mode, v.HostAllowed, v.Secrets.Values.All(x => x)))];
        }, ct);

    /// <summary>
    /// Creates or changes a channel instance. Settings hold no secrets (secret references only); live mode needs an
    /// allow-listed host, and in a sandbox it needs permission administration (sandboxes capture by default).
    /// </summary>
    public Task<ChannelView> SaveChannelAsync(RequestContext ctx, SaveChannelRequest request, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var perms = await RequireManageAsync(db, ctx, ct);
            if (request.Type is not (NotificationChannels.Email or NotificationChannels.Sms))
            {
                throw new KernelException("invalid_channel", $"'{request.Type}' is not a channel type you can configure.", 400, "type", "Use email or sms; the in-app channel needs no settings.");
            }

            if (request.Mode is not ("live" or "capture" or "disabled"))
            {
                throw new KernelException("invalid_mode", $"'{request.Mode}' is not a mode.", 400, "mode", "Use live, capture or disabled.");
            }

            var settings = request.Settings ?? [];
            if (settings.Select(p => p.Key.ToLowerInvariant()).FirstOrDefault(k => SecretLikeKeys.Any(k.Contains) && !k.EndsWith("ref", StringComparison.Ordinal)) is { } secretKey)
            {
                throw new KernelException("secret_in_settings", $"Setting '{secretKey}' looks like a secret.", 400, secretKey, "Store secrets in the secret store and list their names under secretRefs.");
            }

            var refs = request.SecretRefs ?? [];
            if (refs.FirstOrDefault(r => !EnvironmentSecretStore.IsValidName(r)) is { } badRef)
            {
                throw new KernelException("invalid_secret_ref", $"'{badRef}' is not a secret name.", 400, "secretRefs", "Use upper-case names like SMTP_PASSWORD.");
            }

            var host = settings["host"]?.GetValue<string>() ?? (request.Type == NotificationChannels.Sms ? "api.twilio.com" : null);
            if (request.Mode == "live" && !options.IsAllowed(request.Type, host))
            {
                throw new KernelException("host_not_allowed", $"'{host}' is not an allowed {request.Type} host on this platform.", 400, "settings.host", "Ask a platform operator to allow it, or keep the channel in capture mode.");
            }

            var kind = await db.Environments.Where(e => e.Id == ctx.EnvironmentId).Select(e => e.Kind).FirstAsync(ct);
            if (request.Mode == "live" && kind != "production" && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden("Sending for real from a sandbox needs permission administration.", suggestion: "Keep sandbox channels in capture mode; captured messages are visible under Notifications.");
            }

            var row = await db.NotificationChannels.AsTracking().FirstOrDefaultAsync(c => c.Key == request.Key, ct);
            if (row is null)
            {
                row = new NotificationChannelRow { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, Id = Guid.NewGuid(), Key = request.Key, CreatedAt = clock.UtcNow };
                db.NotificationChannels.Add(row);
            }
            else
            {
                row.Version++;
            }

            row.Type = request.Type;
            row.Label = string.IsNullOrWhiteSpace(request.Label) ? request.Key : request.Label.Trim();
            row.Mode = request.Mode;
            row.Settings = settings.ToJsonString();
            row.SecretRefs = new JsonArray([.. refs.Select(r => JsonValue.Create(r))]).ToJsonString();
            row.RatePerMinute = Math.Clamp(request.RatePerMinute ?? row.RatePerMinute, 1, 10_000);
            row.UpdatedBy = ctx.PrincipalId;
            row.UpdatedAt = clock.UtcNow;
            // Audit names what changed, never values of secrets (there are none here) or addresses.
            AuditWriter.Add(db, ctx, "notification.channel.save", "notification_channel", row.Key, null, new { row.Type, row.Mode, secretRefs = refs, row.RatePerMinute });
            await db.SaveChangesAsync(ct);
            var slug = await db.Tenants.Where(t => t.Id == ctx.TenantId).Select(t => t.Slug).FirstAsync(ct);
            return View(row, secrets.For(slug));
        }, ct);

    // ------------------------------------------------------------------ deliveries: inspection, replay, test send

    public Task<IReadOnlyList<DeliveryView>> DeliveriesAsync(RequestContext ctx, string? status, Guid? eventId, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<DeliveryView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            var manage = CanManage(perms);
            if (!manage && !perms.Can(PlatformActions.AuditRead))
            {
                throw KernelException.Forbidden("Delivery history needs notification management or audit access.");
            }

            var q = db.NotificationDeliveries.AsNoTracking();
            if (status is not null)
            {
                q = q.Where(d => d.Status == status);
            }

            if (eventId is not null)
            {
                q = q.Where(d => d.EventId == eventId);
            }

            var rows = await q.OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ct);
            var names = await db.Principals.AsNoTracking().Where(p => rows.Select(r => r.RecipientPrincipal).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            // Text (rendered for the recipient, who may see more than the caller) only for people who manage notifications;
            // addresses are masked for everyone.
            return [.. rows.Select(d => new DeliveryView(d.Id, d.EventId, d.EventType, d.RouteId, d.ChannelType,
                d.RecipientPrincipal is { } p ? names.GetValueOrDefault(p, "a person") : Mask(d.RecipientAddress),
                d.External, d.Mandatory, d.Status, d.StatusReason, d.Attempts, manage ? d.Subject : null, manage ? d.Body : null, d.CreatedAt, d.SentAt, d.ReplayOf))];
        }, ct);

    /// <summary>
    /// Sends a delivery again (audited). Failed and ambiguous deliveries are requeued with their idempotency key; a sent
    /// one is copied to a new delivery only with <paramref name="force"/>, so nobody is told twice by accident.
    /// </summary>
    public Task<Guid> ReplayAsync(RequestContext ctx, Guid id, bool force, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var d = await db.NotificationDeliveries.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw KernelException.NotFound("Delivery");
            Guid target;
            if (d.Status is DeliveryStatuses.Failed or DeliveryStatuses.Ambiguous)
            {
                d.Status = DeliveryStatuses.Queued;
                d.StatusReason = "replayed";
                d.UpdatedAt = clock.UtcNow;
                target = d.Id;
            }
            else if (force && d.Status is DeliveryStatuses.Sent or DeliveryStatuses.Captured or DeliveryStatuses.Suppressed)
            {
                var copies = await db.NotificationDeliveries.CountAsync(x => x.ReplayOf == d.Id, ct);
                var copy = new NotificationDeliveryRow
                {
                    TenantId = d.TenantId,
                    EnvironmentId = d.EnvironmentId,
                    Id = Guid.NewGuid(),
                    EventId = d.EventId,
                    EventType = d.EventType,
                    SubscriptionId = d.SubscriptionId,
                    RouteId = d.RouteId,
                    TemplateId = d.TemplateId,
                    ProfileId = d.ProfileId,
                    ChannelType = d.ChannelType,
                    ChannelKey = d.ChannelKey,
                    RecipientPrincipal = d.RecipientPrincipal,
                    RecipientAddress = d.RecipientAddress,
                    External = d.External,
                    Mandatory = d.Mandatory,
                    Severity = d.Severity,
                    EntityId = d.EntityId,
                    RecordId = d.RecordId,
                    Status = DeliveryStatuses.Queued,
                    StatusReason = "replayed",
                    IdempotencyKey = $"{d.IdempotencyKey}:replay:{copies + 1}",
                    Subject = d.Subject,
                    Body = d.Body,
                    ConfigRevision = d.ConfigRevision,
                    ReplayOf = d.Id,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow,
                };
                db.NotificationDeliveries.Add(copy);
                target = copy.Id;
            }
            else
            {
                throw new KernelException("not_replayable", $"A {d.Status} delivery is not replayed{(DeliveryStatuses.IsFinal(d.Status) ? " without force" : string.Empty)}.", 409, id.ToString(),
                    DeliveryStatuses.IsFinal(d.Status) ? "Pass force to send it again (the recipient gets it twice)." : "It is still being delivered.");
            }

            AuditWriter.Add(db, ctx, "notification.replay", "notification_delivery", id.ToString(), null, new { target, force, d.Status });
            await db.SaveChangesAsync(ct);
            await JobScheduler.ScheduleAsync(db, ctx.TenantId, ctx.EnvironmentId, new JobRequest(NotifyResolveJobHandler.DeliverKind, new JsonObject { ["delivery"] = target.ToString() }) { DedupKey = $"{NotifyResolveJobHandler.DeliverKind}:{target}" }, ct);
            return target;
        }, ct);

    /// <summary>Sends a test message to the person themselves through a channel instance, following its mode (audited).</summary>
    public Task<Guid> TestSendAsync(RequestContext ctx, string channelKey, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var instance = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Key == channelKey, ct) ?? throw KernelException.NotFound($"Channel '{channelKey}'");
            var me = await db.Principals.AsNoTracking().FirstAsync(p => p.Id == ctx.PrincipalId, ct);
            if (instance.Type == NotificationChannels.Sms)
            {
                throw new KernelException("no_test_address", "Test texts need a phone number, and people have none on file yet.", 400, channelKey, "Send a test email, or check the captured messages of a route preview.");
            }

            var id = Guid.NewGuid();
            db.NotificationDeliveries.Add(new NotificationDeliveryRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = id,
                EventId = id,
                EventType = "platform.notification.test",
                SubscriptionId = "test",
                RouteId = "test",
                TemplateId = "test",
                ProfileId = "test",
                ChannelType = instance.Type,
                ChannelKey = instance.Key,
                RecipientPrincipal = ctx.PrincipalId,
                RecipientAddress = me.Email,
                Status = DeliveryStatuses.Queued,
                IdempotencyKey = $"test:{id}",
                Subject = "Esmbler test message",
                Body = $"This is a test of the \"{instance.Label}\" channel. If you can read it, it works.",
                ConfigRevision = 0,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
            });
            AuditWriter.Add(db, ctx, "notification.test_send", "notification_channel", channelKey, null, new { instance.Mode });
            await db.SaveChangesAsync(ct);
            await JobScheduler.ScheduleAsync(db, ctx.TenantId, ctx.EnvironmentId, new JobRequest(NotifyResolveJobHandler.DeliverKind, new JsonObject { ["delivery"] = id.ToString() }) { DedupKey = $"{NotifyResolveJobHandler.DeliverKind}:{id}" }, ct);
            return id;
        }, ct);

    // ------------------------------------------------------------------ preview (no sends, no writes)

    /// <summary>
    /// Resolves and renders a route of the working configuration (the open draft, if any) for a record, without sending
    /// or writing anything (REQ-NOT-006). Text shows only what both the recipient and the previewing person may see.
    /// </summary>
    public async Task<NotificationPreview> PreviewAsync(RequestContext ctx, string routeId, Guid? recordId, CancellationToken ct)
    {
        var (config, viewer, _, _) = await preview.WorkingConfigAsync(ctx, ct);
        if (!viewer.Can(PlatformActions.ConfigEdit) && !CanManage(viewer))
        {
            throw KernelException.Forbidden("Previewing notifications needs configuration or notification management rights.");
        }

        var route = config.FindNotificationRoute(routeId) ?? throw KernelException.NotFound($"Notification route '{routeId}'");
        var sub = config.FindSubscription(route.Subscription) ?? throw KernelException.NotFound($"Subscription '{route.Subscription}'");
        var template = config.FindNotificationTemplate(route.Template) ?? throw KernelException.NotFound($"Template '{route.Template}'");
        var profile = config.FindNotificationProfile(route.Profile) ?? throw KernelException.NotFound($"Profile '{route.Profile}'");
        var type = config.FindEventType(sub.Event);
        var entityId = sub.Entity ?? type?.SubjectEntity;
        var entity = entityId is null ? null : config.FindEntity(entityId);
        return await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            JsonObject? record = null;
            string? number = null;
            if (recordId is { } rid && entity is not null)
            {
                var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.EntityId == entity.Id && r.DeletedAt == null, ct) ?? throw KernelException.NotFound("Record");
                record = JsonNode.Parse(row.Data)!.AsObject();
                if (!viewer.CanRead(entity, record))
                {
                    throw KernelException.NotFound("Record");
                }

                number = entity.Fields.Find(f => f.Type == FieldTypes.AutoNumber) is { } nf && record[nf.Key] is JsonValue nv ? nv.GetValue<string>() : null;
            }

            var envelope = new Sdk.Events.EventEnvelope
            {
                EventId = Guid.Empty,
                Type = sub.Event,
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Entity = entity?.Id,
                RecordId = recordId,
                OccurredAt = clock.UtcNow,
                CorrelationId = Guid.Empty,
                Actor = "preview",
                Payload = SamplePayload(type),
            };
            var targets = await NotifyResolveJobHandler.TargetsAsync(db, route, entity, record, envelope, ct);
            var mutes = await db.NotificationPreferences.AsNoTracking().Where(p => p.RouteId == route.Id && p.Muted).ToListAsync(ct);
            var link = entity is not null && recordId is { } id ? $"{options.WebUrl.TrimEnd('/')}/records/{entity.Id}/{id}" : options.WebUrl;
            var results = new List<PreviewRecipient>();
            foreach (var t in targets.Take(20))
            {
                EffectivePermissions? perms = null;
                string? suppressed = null;
                if (t.Principal is { } p)
                {
                    try
                    {
                        perms = await access.ForAsync(db, new RequestContext { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, PrincipalId = p, Channel = "worker" }, config, ct);
                        suppressed = entity is not null && record is not null && !perms.CanRead(entity, record) ? "cannot read the record" : null;
                    }
                    catch (KernelException)
                    {
                        suppressed = "not a member";
                    }

                    if (suppressed is null && !route.Mandatory && mutes.Any(m => m.PrincipalId == p && m.ChannelType == "*"))
                    {
                        suppressed = "muted";
                    }
                }

                var channels = profile.Channels.Where(c => NotifyResolveJobHandler.Reachable(c, t)).ToList();
                if (profile.Delivery == NotificationDelivery.Fallback)
                {
                    channels = channels.Take(1).ToList();
                }

                var (subject, body) = NotificationRenderer.Render(template, channels.FirstOrDefault() ?? NotificationChannels.InApp, entity, record, number, envelope.Payload,
                    (type?.Sensitivity) == EventSensitivities.Public, link, new RenderScope(perms, t.External, t.Name) { Viewer = viewer });
                results.Add(new PreviewRecipient(t.Name ?? Mask(t.Address) ?? "someone", t.External, channels, suppressed ?? (channels.Count == 0 ? "no reachable channel" : null), subject, body));
            }

            return new NotificationPreview(route.Id, template.Id, profile.Id, route.Mandatory, results,
                record is null ? "No record chosen: record fields render empty and recipients from record fields are not shown." : targets.Count > 20 ? $"Showing 20 of {targets.Count} recipients." : null);
        }, ct);
    }

    private static JsonObject SamplePayload(EventTypeSpec? type)
    {
        var payload = new JsonObject();
        foreach (var (key, field) in type?.Fields ?? [])
        {
            payload[key] = field.Type switch
            {
                EventFieldTypes.Number => 1,
                EventFieldTypes.Boolean => true,
                EventFieldTypes.List => new JsonArray(),
                _ => $"<{key}>",
            };
        }

        return payload;
    }

    // ------------------------------------------------------------------ helpers

    private static bool CanManage(EffectivePermissions perms) => perms.Can(PlatformActions.NotificationsManage) || perms.Can(PlatformActions.PermissionsAdmin);

    private async Task<EffectivePermissions> RequireManageAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true);
        if (!CanManage(perms))
        {
            throw KernelException.Forbidden(ctx.AgentId is null ? "Managing notification channels and deliveries needs the notifications.manage right." : "AI agents cannot manage notification channels or deliveries.");
        }

        return perms;
    }

    private static void RefuseAgents(RequestContext ctx)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents have no inbox.");
        }
    }

    private ChannelView View(NotificationChannelRow r, Sdk.Notifications.ISecretAccessor accessor)
    {
        var settings = JsonNode.Parse(r.Settings) as JsonObject ?? [];
        var refs = (JsonNode.Parse(r.SecretRefs) as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>();
        var host = settings["host"]?.GetValue<string>() ?? (r.Type == NotificationChannels.Sms ? "api.twilio.com" : null);
        // Only whether each secret is configured, never its value.
        return new ChannelView(r.Key, r.Type, r.Label, r.Mode, settings, refs.ToDictionary(n => n, n => accessor.Get(n) is not null), r.RatePerMinute, options.IsAllowed(r.Type, host), r.UpdatedAt);
    }

    private static string? Mask(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return address;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 1 ? $"{address[0]}…{address[at..]}" : address.Length > 4 ? $"…{address[^4..]}" : "…";
    }
}
