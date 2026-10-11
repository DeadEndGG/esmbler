using System.Text.Json.Nodes;
using Esmbler.Kernel.Alerts;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Alerts;

public sealed record AlertView(Guid Id, string Rule, string RuleLabel, string Status, string Severity, string Title, string? Entity, Guid? RecordId, string? OwnerGroup, string? Owner, string? Assignee, Guid? AssigneeId, int Level, int Occurrences, int Reopened, DateTimeOffset OpenedAt, DateTimeOffset LastOccurredAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, DateTimeOffset? ResolvedAt, string? Resolution, int Version, bool CanRespond);

public sealed record AlertActivityView(string Kind, string? Actor, int? Level, JsonObject? Details, DateTimeOffset At);

public sealed record AlertDetail(AlertView Alert, IReadOnlyList<AlertActivityView> Activity);

public sealed record AlertPage(IReadOnlyList<AlertView> Items, bool More);

public sealed record SuppressionView(Guid Id, string Reason, string? Rule, string? Entity, Guid? RecordId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string CreatedBy, DateTimeOffset? CancelledAt);

public sealed record SuppressionRequest(string Reason, string? Rule, string? Entity, Guid? RecordId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// People's side of alerts (ADR 0022): who sees which alert, and acknowledging, resolving and assigning them; maintenance
/// windows. People with <c>alerts.respond</c> (or <c>alerts.manage</c>, or permission administration) see every alert of the
/// environment; everyone else sees the alerts their groups own or that are theirs. Every alert about a record also needs
/// read access to it. Agents may read through MCP but never act: acknowledgment is a person's attestation.
/// </summary>
public sealed class AlertService(Database database, ConfigStore configs, AccessService access, IClock clock)
{
    public const int MaxPage = 100;
    public static readonly TimeSpan MaxSuppression = TimeSpan.FromDays(14);

    public Task<AlertPage> ListAsync(RequestContext ctx, string? status, bool mine, int limit, int offset, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms) = await PermsAsync(db, ctx, ct);
            var q = Visible(db.Alerts.AsNoTracking(), perms, ctx, mine, await SeesAllAsync(db, config, perms, ctx, ct));
            if (status is not null)
            {
                q = status == "active" ? q.Where(a => a.Status == AlertStatuses.Open || a.Status == AlertStatuses.Acknowledged) : q.Where(a => a.Status == status);
            }

            // Offsets count only alerts the person may see (record read is checked in memory), so pages neither skip
            // nor repeat, and their length says nothing about alerts on records the person cannot read.
            limit = Math.Clamp(limit, 1, MaxPage);
            offset = Math.Max(0, offset);
            var ordered = q.OrderByDescending(a => a.LastOccurredAt).ThenBy(a => a.Id);
            var visible = new List<AlertRow>();
            const int chunk = 200, scanCap = 5000;
            for (var scanned = 0; scanned < scanCap && visible.Count <= offset + limit; scanned += chunk)
            {
                var rows = await ordered.Skip(scanned).Take(chunk).ToListAsync(ct);
                visible.AddRange(await ReadableAsync(db, config, perms, rows, ct));
                if (rows.Count < chunk)
                {
                    break;
                }
            }

            var views = await ViewsAsync(db, config, perms, ctx, visible.Skip(offset).Take(limit).ToList(), ct);
            return new AlertPage(views, visible.Count > offset + limit);
        }, ct);

    /// <summary>Open alerts that are the person's to answer: owned by one of their groups, by them, or assigned to them.</summary>
    public Task<int> CountAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms) = await PermsAsync(db, ctx, ct);
            var rows = await Visible(db.Alerts.AsNoTracking(), perms, ctx, mine: true, seesAll: false).Where(a => a.Status == AlertStatuses.Open).OrderByDescending(a => a.OpenedAt).Take(200).ToListAsync(ct);
            return (await ReadableAsync(db, config, perms, rows, ct)).Count;
        }, ct);

    public Task<AlertDetail> GetAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms) = await PermsAsync(db, ctx, ct);
            var row = await VisibleOneAsync(db, config, perms, ctx, id, await SeesAllAsync(db, config, perms, ctx, ct), ct);
            var view = (await ViewsAsync(db, config, perms, ctx, [row], ct))[0];
            var activity = await db.AlertActivity.AsNoTracking().Where(x => x.AlertId == id).OrderBy(x => x.At).Take(200).ToListAsync(ct);
            var names = await NamesAsync(db, activity.Select(x => x.Actor), ct);
            return new AlertDetail(view, [.. activity.Select(x => new AlertActivityView(x.Kind, x.Actor is { } a ? names.GetValueOrDefault(a, "someone") : x.Agent is null ? null : "an AI agent", x.Level, x.Details is null ? null : JsonNode.Parse(x.Details) as JsonObject, x.At))]);
        }, ct);

    public Task<AlertView> AcknowledgeAsync(RequestContext ctx, Guid id, int? expectedVersion, CancellationToken ct) =>
        ActAsync(ctx, id, expectedVersion, "acknowledge", Sync((alert, now) =>
        {
            if (alert.Status == AlertStatuses.Acknowledged)
            {
                return null; // idempotent
            }

            RequireMove(alert, AlertStatuses.Acknowledged);
            var announced = alert.Status != AlertStatuses.Suppressed;
            alert.Status = AlertStatuses.Acknowledged;
            alert.AcknowledgedAt = now;
            alert.AcknowledgedBy = ctx.PrincipalId;
            alert.SuppressionId = null;

            // An alert nobody was told about is taken quietly.
            return new Outcome(announced ? EventTypes.AlertAcknowledged : null, "acknowledged", null);
        }), ct);

    public Task<AlertView> ResolveAsync(RequestContext ctx, Guid id, string? note, int? expectedVersion, CancellationToken ct) =>
        ActAsync(ctx, id, expectedVersion, "resolve", Sync((alert, now) =>
        {
            if (alert.Status == AlertStatuses.Resolved)
            {
                return null;
            }

            RequireMove(alert, AlertStatuses.Resolved);
            var announced = alert.Status != AlertStatuses.Suppressed;
            alert.Status = AlertStatuses.Resolved;
            alert.Resolution = "manual";
            alert.ResolvedAt = now;
            alert.ResolvedBy = ctx.PrincipalId;
            var details = string.IsNullOrWhiteSpace(note) ? null : new JsonObject { ["note"] = Clean(note, 1000) };
            return new Outcome(announced ? EventTypes.AlertResolved : null, "resolved", details);
        }), ct);

    public Task<AlertView> AssignAsync(RequestContext ctx, Guid id, Guid assignee, int? expectedVersion, CancellationToken ct) =>
        ActAsync(ctx, id, expectedVersion, "assign", async (db, alert, now, config) =>
        {
            if (alert.Status == AlertStatuses.Resolved)
            {
                throw new KernelException("alert_resolved", "This alert is resolved.", 409, id.ToString(), "Assign open alerts only.");
            }

            // The assignee must be an enabled person of the workspace who may read the alert's record.
            var person = await db.Principals.AsNoTracking().AnyAsync(p => p.Id == assignee && p.Kind == "user" && !p.Disabled, ct) && await db.Memberships.AnyAsync(m => m.TenantId == ctx.TenantId && m.PrincipalId == assignee, ct);
            if (!person)
            {
                throw new KernelException("invalid_assignee", "Assign the alert to an active person in this workspace.", 400, "assignee");
            }

            if (alert.RecordId is { } recordId && alert.EntityId is { } entityId && config.FindEntity(entityId) is { } entity)
            {
                var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
                var theirs = await access.ForAsync(db, new RequestContext { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, PrincipalId = assignee, Channel = "worker" }, config, ct);
                if (row is null || !theirs.CanRead(entity, JsonNode.Parse(row.Data)!.AsObject()))
                {
                    throw new KernelException("invalid_assignee", "That person cannot read the record this alert is about.", 400, "assignee", "Pick someone who works on the record.");
                }
            }

            if (alert.Assignee == assignee)
            {
                return (Outcome?)null;
            }

            alert.Assignee = assignee;
            return new Outcome(alert.Status == AlertStatuses.Suppressed ? null : EventTypes.AlertAssigned, "assigned", new JsonObject { ["assignee"] = assignee.ToString() });
        }, ct);

    // ------------------------------------------------------------------ maintenance windows

    public Task<IReadOnlyList<SuppressionView>> SuppressionsAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<SuppressionView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (_, perms) = await PermsAsync(db, ctx, ct);
            if (!CanRespond(perms))
            {
                throw KernelException.Forbidden("Maintenance windows need the alerts.respond or alerts.manage right.");
            }

            var since = clock.UtcNow.AddDays(-30);
            var rows = await db.AlertSuppressions.AsNoTracking().Where(s => s.EndsAt > since).OrderByDescending(s => s.StartsAt).Take(100).ToListAsync(ct);
            var names = await NamesAsync(db, rows.Select(r => (Guid?)r.CreatedBy), ct);
            return [.. rows.Select(r => new SuppressionView(r.Id, r.Reason, r.RuleId, r.EntityId, r.RecordId, r.StartsAt, r.EndsAt, names.GetValueOrDefault(r.CreatedBy, "someone"), r.CancelledAt))];
        }, ct);

    public Task<SuppressionView> SuppressAsync(RequestContext ctx, SuppressionRequest request, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var config = await RequireManageAsync(db, ctx, ct);
            var reason = Clean(request.Reason, 500);
            if (reason.Length == 0)
            {
                throw new KernelException("invalid_reason", "Say why alerts are silenced.", 400, "reason");
            }

            var now = clock.UtcNow;
            if (request.EndsAt <= request.StartsAt || request.EndsAt <= now || request.EndsAt - request.StartsAt > MaxSuppression || request.StartsAt < now.AddMinutes(-5))
            {
                throw new KernelException("invalid_window", "A maintenance window starts now or later, ends after it starts, and lasts at most 14 days.", 400, "endsAt");
            }

            if (request.Rule is { } rule && config.FindAlertRule(rule) is null)
            {
                throw KernelException.NotFound($"Alert rule '{rule}'");
            }

            if (request.Entity is { } entity && config.FindEntity(entity) is null)
            {
                throw KernelException.NotFound($"Entity '{entity}'");
            }

            var row = new AlertSuppressionRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = Guid.NewGuid(),
                Reason = reason,
                RuleId = request.Rule,
                EntityId = request.Entity,
                RecordId = request.RecordId,
                StartsAt = request.StartsAt,
                EndsAt = request.EndsAt,
                CreatedBy = ctx.PrincipalId,
                CreatedAt = now,
            };
            db.AlertSuppressions.Add(row);

            // When the window ends, the alerts it held back are announced if the problem is still there.
            await Events.JobScheduler.ScheduleAsync(db, ctx.TenantId, ctx.EnvironmentId, new Events.JobRequest(AlertUnsuppressJobHandler.JobKind, new JsonObject { ["window"] = row.Id.ToString() })
            {
                RunAt = row.EndsAt,
                DedupKey = AlertUnsuppressJobHandler.Key(row.Id),
            }, ct);
            AuditWriter.Add(db, ctx, "alert.suppress", "alert_suppression", row.Id.ToString(), null, new { row.RuleId, row.EntityId, row.RecordId, row.StartsAt, row.EndsAt });
            await db.SaveChangesAsync(ct);
            var name = await db.Principals.AsNoTracking().Where(p => p.Id == ctx.PrincipalId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
            return new SuppressionView(row.Id, row.Reason, row.RuleId, row.EntityId, row.RecordId, row.StartsAt, row.EndsAt, name ?? "you", null);
        }, ct);

    public Task CancelSuppressionAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var row = await db.AlertSuppressions.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw KernelException.NotFound("Maintenance window");
            if (row.CancelledAt is null)
            {
                row.CancelledAt = clock.UtcNow;
                row.CancelledBy = ctx.PrincipalId;
                await Events.JobScheduler.ScheduleAsync(db, ctx.TenantId, ctx.EnvironmentId, new Events.JobRequest(AlertUnsuppressJobHandler.JobKind, new JsonObject { ["window"] = id.ToString() })
                {
                    RunAt = clock.UtcNow,
                    DedupKey = AlertUnsuppressJobHandler.Key(id),
                    ReplaceExisting = true,
                }, ct);
                AuditWriter.Add(db, ctx, "alert.suppression_cancel", "alert_suppression", id.ToString(), null);
                await db.SaveChangesAsync(ct);
            }

            return true;
        }, ct);

    // ------------------------------------------------------------------ helpers

    private sealed record Outcome(string? Event, string Activity, JsonObject? Details);

    private static Func<KernelDbContext, AlertRow, DateTimeOffset, EffectiveConfig, Task<Outcome?>> Sync(Func<AlertRow, DateTimeOffset, Outcome?> change) =>
        (_, alert, now, _) => Task.FromResult(change(alert, now));

    private async Task<AlertView> ActAsync(RequestContext ctx, Guid id, int? expectedVersion, string action, Func<KernelDbContext, AlertRow, DateTimeOffset, EffectiveConfig, Task<Outcome?>> change, CancellationToken ct) =>
        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (ctx.AgentId is not null)
            {
                throw new KernelException("agent_not_allowed", "AI agents cannot acknowledge, resolve or assign alerts.", 403, id.ToString(), "Ask a person to respond to the alert.");
            }

            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true);
            var alert = (await db.Alerts.FromSqlInterpolated($"SELECT * FROM kernel.alerts WHERE id = {id} FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault() ?? throw KernelException.NotFound("Alert");
            if (!await IsVisibleAsync(db, artifact.Config, perms, ctx, alert, CanRespond(perms), ct))
            {
                throw KernelException.NotFound("Alert");
            }

            if (!CanRespond(perms) && !IsTheirs(alert, perms, ctx))
            {
                throw KernelException.Forbidden("Only the alert's owners, its assignee and people with alerts.respond answer alerts.");
            }

            if (expectedVersion is { } v && v != alert.Version)
            {
                throw new KernelException("version_conflict", "Someone changed this alert meanwhile.", 409, id.ToString(), "Reload it and try again.");
            }

            var now = clock.UtcNow;
            var outcome = await change(db, alert, now, artifact.Config);
            if (outcome is { } done)
            {
                alert.UpdatedAt = now;
                alert.Version++;
                AlertEvaluateJobHandler.Activity(db, alert, done.Activity, ctx, done.Details);
                if (done.Event is { } type)
                {
                    AlertEvaluateJobHandler.Publish(db, ctx, artifact.Revision, alert, type, null);
                }

                AuditWriter.Add(db, ctx, "alert." + action, "alert", id.ToString(), artifact.Revision, new { alert.RuleId, alert.Status, done.Details });
                await db.SaveChangesAsync(ct);
            }

            return (await ViewsAsync(db, artifact.Config, perms, ctx, [alert], ct))[0];
        }, ct);

    private static void RequireMove(AlertRow alert, string to)
    {
        if (!AlertLifecycle.CanMove(alert.Status, to))
        {
            throw new KernelException("invalid_alert_state", $"A {alert.Status} alert cannot be {to}.", 409, alert.Id.ToString());
        }
    }

    private async Task<(EffectiveConfig Config, EffectivePermissions Perms)> PermsAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        return (artifact.Config, await access.ForAsync(db, ctx, artifact.Config, ct));
    }

    private async Task<EffectiveConfig> RequireManageAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true);
        if (!perms.Can(PlatformActions.AlertsManage) && !perms.Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden(ctx.AgentId is null ? "Maintenance windows need the alerts.manage right." : "AI agents cannot silence alerts.");
        }

        return artifact.Config;
    }

    public static bool CanRespond(EffectivePermissions perms) =>
        perms.Can(PlatformActions.AlertsRespond) || perms.Can(PlatformActions.AlertsManage) || perms.Can(PlatformActions.PermissionsAdmin);

    private static bool IsTheirs(AlertRow alert, EffectivePermissions perms, RequestContext ctx) =>
        alert.Assignee == ctx.PrincipalId || alert.OwnerPrincipal == ctx.PrincipalId || (alert.OwnerGroup is { } g && perms.Facts.Groups.Contains(g));

    /// <summary>
    /// Whether every alert of the environment is visible: responders, and AI agents acting for one (agents lose the right
    /// to act, not to read; record access still follows the agent's scopes).
    /// </summary>
    private async Task<bool> SeesAllAsync(KernelDbContext db, EffectiveConfig config, EffectivePermissions perms, RequestContext ctx, CancellationToken ct) =>
        CanRespond(perms) || (ctx.AgentId is not null && CanRespond(await access.ForAsync(db, ctx with { AgentId = null, Scopes = null }, config, ct)));

    private static IQueryable<AlertRow> Visible(IQueryable<AlertRow> q, EffectivePermissions perms, RequestContext ctx, bool mine, bool seesAll)
    {
        var groups = perms.Facts.Groups.ToList();
        var me = ctx.PrincipalId;
        return seesAll && !mine
            ? q
            : q.Where(a => a.Assignee == me || a.OwnerPrincipal == me || (a.OwnerGroup != null && groups.Contains(a.OwnerGroup.Value)));
    }

    private static async Task<bool> IsVisibleAsync(KernelDbContext db, EffectiveConfig config, EffectivePermissions perms, RequestContext ctx, AlertRow alert, bool seesAll, CancellationToken ct) =>
        (seesAll || IsTheirs(alert, perms, ctx)) && (await ReadableAsync(db, config, perms, [alert], ct)).Count == 1;

    private static async Task<AlertRow> VisibleOneAsync(KernelDbContext db, EffectiveConfig config, EffectivePermissions perms, RequestContext ctx, Guid id, bool seesAll, CancellationToken ct)
    {
        var row = await db.Alerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return row is not null && await IsVisibleAsync(db, config, perms, ctx, row, seesAll, ct) ? row : throw KernelException.NotFound("Alert");
    }

    /// <summary>Alerts about records the person may not read are invisible to them, whatever their alert rights.</summary>
    private static async Task<List<AlertRow>> ReadableAsync(KernelDbContext db, EffectiveConfig config, EffectivePermissions perms, List<AlertRow> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.RecordId is not null).Select(r => r.RecordId!.Value).Distinct().ToList();
        var records = ids.Count == 0 ? [] : await db.Records.AsNoTracking().Where(r => ids.Contains(r.Id) && r.DeletedAt == null).ToDictionaryAsync(r => r.Id, ct);
        return [.. rows.Where(r => r.RecordId is not { } recordId
            || (records.TryGetValue(recordId, out var record) && config.FindEntity(record.EntityId) is { } entity && perms.CanRead(entity, JsonNode.Parse(record.Data)!.AsObject())))];
    }

    private static async Task<List<AlertView>> ViewsAsync(KernelDbContext db, EffectiveConfig config, EffectivePermissions perms, RequestContext ctx, List<AlertRow> rows, CancellationToken ct)
    {
        var names = await NamesAsync(db, rows.SelectMany(r => new[] { r.Assignee, r.OwnerPrincipal, r.AcknowledgedBy }), ct);
        var respond = CanRespond(perms) && ctx.AgentId is null;
        return [.. rows.Select(r => new AlertView(r.Id, r.RuleId, config.FindAlertRule(r.RuleId)?.Label ?? r.RuleId, r.Status, r.Severity, r.Title, r.EntityId, r.RecordId, r.OwnerGroupKey,
            r.OwnerPrincipal is { } o ? names.GetValueOrDefault(o) : null, r.Assignee is { } a ? names.GetValueOrDefault(a) : null, r.Assignee, r.Level, r.Occurrences, r.ReopenedCount, r.OpenedAt, r.LastOccurredAt,
            r.AcknowledgedAt, r.AcknowledgedBy is { } k ? names.GetValueOrDefault(k) : null, r.ResolvedAt, r.Resolution, r.Version, ctx.AgentId is null && (respond || IsTheirs(r, perms, ctx))))];
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(KernelDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Principals.AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    private static string Clean(string? s, int max)
    {
        var text = new string((s ?? string.Empty).Where(c => !char.IsControl(c) || c is '\n').ToArray()).Trim();
        return text.Length <= max ? text : text[..max];
    }
}
