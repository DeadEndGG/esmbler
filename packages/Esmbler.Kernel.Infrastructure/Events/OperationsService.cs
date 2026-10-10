using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Events;

public sealed record QueueStats(string Queue, int Pending, int Running, int Dead, int Failed, double? OldestPendingSeconds);

public sealed record DeadLetter(string Queue, Guid Id, string Kind, int Attempts, string? LastError, DateTimeOffset At);

/// <summary>
/// Operator view of the workspace's background work (REQ-EVT-005, ASM-029): queue depth and age, dead letters, and
/// audited replay. Replay only requeues; the job is re-authorized as its creator when it runs again.
/// </summary>
public sealed class OperationsService(Database database, AccessService access, ConfigStore configs)
{
    public Task<IReadOnlyList<QueueStats>> StatsAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<QueueStats>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAsync(db, ctx, PlatformActions.AuditRead, ct);
            var now = DateTimeOffset.UtcNow;
            var jobs = await db.Jobs.GroupBy(j => j.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var oldestJob = await db.Jobs.Where(j => j.Status == "pending" && j.AvailableAt <= now).MinAsync(j => (DateTimeOffset?)j.AvailableAt, ct);
            var events = await db.Outbox.GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var oldestEvent = await db.Outbox.Where(o => o.Status == "pending" && o.AvailableAt <= now).MinAsync(o => (DateTimeOffset?)o.AvailableAt, ct);
            var deadConsumers = await db.ConsumerFailures.CountAsync(f => f.Dead, ct);
            return
            [
                new QueueStats("jobs", jobs.GetValueOrDefault("pending"), jobs.GetValueOrDefault("running"), jobs.GetValueOrDefault("dead"), jobs.GetValueOrDefault("failed"), oldestJob is { } j ? (now - j).TotalSeconds : null),
                new QueueStats("events", events.GetValueOrDefault("pending"), 0, events.GetValueOrDefault("dead"), deadConsumers, oldestEvent is { } e ? (now - e).TotalSeconds : null),
            ];
        }, ct);

    public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<DeadLetter>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAsync(db, ctx, PlatformActions.AuditRead, ct);
            var jobs = await db.Jobs.Where(j => j.Status == "dead" || j.Status == "failed").OrderByDescending(j => j.UpdatedAt).Take(100)
                .Select(j => new DeadLetter("jobs", j.Id, j.Kind, j.Attempts, j.LastError, j.UpdatedAt)).ToListAsync(ct);
            var events = await db.Outbox.Where(o => o.Status == "dead").OrderByDescending(o => o.CreatedAt).Take(100)
                .Select(o => new DeadLetter("events", o.Id, o.Type, o.Attempts, o.LastError, o.CreatedAt)).ToListAsync(ct);
            var consumers = await db.ConsumerFailures.Where(f => f.Dead).OrderByDescending(f => f.LastFailedAt).Take(100)
                .Select(f => new DeadLetter("consumers", f.Id, f.Consumer + " ← " + f.EventType, f.Attempts, f.LastError, f.LastFailedAt)).ToListAsync(ct);
            return jobs.Concat(events).Concat(consumers).OrderByDescending(d => d.At).ToList();
        }, ct);

    public Task ReplayAsync(RequestContext ctx, string queue, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            // Replaying work is an administrative act: it can repeat side effects (handlers are idempotent, but still).
            await RequireAsync(db, ctx, PlatformActions.PermissionsAdmin, ct);
            var now = DateTimeOffset.UtcNow;
            object? details = null;
            var changed = queue switch
            {
                "jobs" => await db.Jobs.Where(j => j.Id == id && (j.Status == "dead" || j.Status == "failed"))
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "pending").SetProperty(j => j.Attempts, 0).SetProperty(j => j.AvailableAt, now).SetProperty(j => j.LastError, (string?)null).SetProperty(j => j.UpdatedAt, now), ct),
                "events" => await db.Outbox.Where(o => o.Id == id && o.Status == "dead")
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "pending").SetProperty(o => o.Attempts, 0).SetProperty(o => o.AvailableAt, now).SetProperty(o => o.LastError, (string?)null), ct),
                "consumers" => await ReplayConsumerAsync(db, id, now, d => details = d, ct),
                _ => throw new KernelException("invalid_queue", "Queue must be 'jobs', 'events' or 'consumers'.", 400, queue),
            };
            if (changed == 0)
            {
                throw KernelException.NotFound("Dead-lettered item");
            }

            AuditWriter.Add(db, ctx, "operations.replay", queue, id.ToString(), null, details);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    /// <summary>Re-runs one dead consumer for one event: its failure resets and the event goes back to pending (other consumers are deduplicated).</summary>
    private static async Task<int> ReplayConsumerAsync(KernelDbContext db, Guid id, DateTimeOffset now, Action<object> describe, CancellationToken ct)
    {
        var failure = await db.ConsumerFailures.AsTracking().FirstOrDefaultAsync(f => f.Id == id && f.Dead, ct);
        if (failure is null || !await db.Outbox.AnyAsync(o => o.Id == failure.EventId, ct))
        {
            return 0;
        }

        describe(new { consumer = failure.Consumer, eventId = failure.EventId, eventType = failure.EventType });
        failure.Dead = false;
        failure.Attempts = 0;
        await db.SaveChangesAsync(ct);
        await db.Outbox.Where(o => o.Id == failure.EventId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "pending").SetProperty(o => o.AvailableAt, now).SetProperty(o => o.LockedUntil, (DateTimeOffset?)null), ct);
        return 1;
    }

    private async Task RequireAsync(KernelDbContext db, RequestContext ctx, string action, CancellationToken ct)
    {
        var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, published.Config, ct, fresh: action == PlatformActions.PermissionsAdmin);
        if (!perms.Can(action) && !perms.Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden("Background operations are visible to administrators.");
        }
    }
}
