using System.Text.Json;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>A durable, idempotent event consumer. Runs inside the event's tenant/environment transaction.</summary>
public interface IEventConsumer
{
    string Name { get; }

    bool Handles(EventEnvelope envelope);

    Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct);
}

/// <summary>
/// Transactional outbox dispatcher (ADR 0005, ADR 0017, REQ-EVT-002, REQ-EVT-009). Claims batches across tenants
/// through the privileged claim function, then runs each pending consumer of an event in its own tenant-context
/// transaction: consumers already recorded in the inbox are skipped (duplicates); a failing consumer is recorded in
/// <c>kernel.consumer_failures</c>, backs off and dead-letters after <see cref="MaxAttempts"/> on its own, while the
/// other consumers' work commits. The event is dispatched once every consumer has succeeded or is dead for it.
/// </summary>
public sealed class OutboxDispatcher(Database database, IEnumerable<IEventConsumer> consumers, ILogger<OutboxDispatcher> logger)
{
    public const int MaxAttempts = 10;
    private const int LeaseSeconds = 60;
    private readonly IReadOnlyList<IEventConsumer> _consumers = consumers.ToList();
    private readonly string _worker = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var claimed = await database.AnonymousAsync(db => db.Database.SqlQuery<Claimed>($"SELECT id AS \"Id\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", attempts AS \"Attempts\" FROM kernel.claim_outbox({_worker}, 50, {LeaseSeconds})").ToListAsync(ct), ct);
        foreach (var item in claimed)
        {
            // Renew this event's lease before working on it; if another worker took it over after our lease expired
            // (a slow batch), leave it to them.
            var held = await database.InTenantAsync(item.TenantId, item.EnvironmentId, null, db => db.Outbox.Where(o => o.Id == item.Id && o.LockedBy == _worker && o.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LockedUntil, DateTimeOffset.UtcNow.AddSeconds(LeaseSeconds)), ct), ct);
            if (held == 0)
            {
                continue;
            }

            EventEnvelope envelope;
            List<IEventConsumer> pending;
            try
            {
                (envelope, pending) = await database.InTenantAsync(item.TenantId, item.EnvironmentId, null, async db =>
                {
                    var row = await db.Outbox.AsNoTracking().FirstAsync(o => o.Id == item.Id, ct);
                    var env = EventWriter.Read(row);
                    var done = await db.Inbox.Where(i => i.EventId == env.EventId).Select(i => i.Consumer).ToListAsync(ct);
                    var dead = await db.ConsumerFailures.Where(f => f.EventId == env.EventId && f.Dead).Select(f => f.Consumer).ToListAsync(ct);
                    return (env, _consumers.Where(c => c.Handles(env) && !done.Contains(c.Name) && !dead.Contains(c.Name)).ToList());
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The event itself cannot be read: back off and dead-letter the event as a whole.
                await FailEventAsync(item, ex.Message, item.Attempts >= MaxAttempts, item.Attempts, ct);
                continue;
            }

            // Each consumer runs in its own transaction (ADR 0017): one failing consumer retries and dead-letters on
            // its own, and never rolls back or holds back the others.
            var retryAttempts = 0;
            string? retryError = null;
            foreach (var consumer in pending)
            {
                try
                {
                    await database.InTenantAsync(item.TenantId, item.EnvironmentId, null, async db =>
                    {
                        await consumer.HandleAsync(envelope, db, ct);
                        db.Inbox.Add(new InboxRow { Consumer = consumer.Name, EventId = envelope.EventId, TenantId = item.TenantId, EnvironmentId = item.EnvironmentId, ProcessedAt = DateTimeOffset.UtcNow });
                        await db.ConsumerFailures.Where(f => f.Consumer == consumer.Name && f.EventId == envelope.EventId).ExecuteDeleteAsync(ct);
                        await db.SaveChangesAsync(ct);
                        return 0;
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Consumer {Consumer} failed on event {Event}", consumer.Name, envelope.EventId);
                    var (attempts, dead) = await RecordFailureAsync(item, envelope, consumer.Name, ex.Message, ct);
                    if (!dead)
                    {
                        retryAttempts = Math.Max(retryAttempts, attempts);
                        retryError = $"{consumer.Name}: {Truncate(ex.Message)}";
                    }
                }
            }

            if (retryError is not null)
            {
                await FailEventAsync(item, retryError, dead: false, retryAttempts, ct);
            }
            else
            {
                await database.InTenantAsync(item.TenantId, item.EnvironmentId, null, db => db.Outbox.Where(o => o.Id == item.Id && o.LockedBy == _worker).ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.Status, "dispatched").SetProperty(o => o.DispatchedAt, DateTimeOffset.UtcNow).SetProperty(o => o.LockedUntil, (DateTimeOffset?)null).SetProperty(o => o.LastError, (string?)null), ct), ct);
            }
        }

        return claimed.Count;
    }

    /// <summary>Records a consumer's failure on an event; after <see cref="MaxAttempts"/> that consumer is dead for it.</summary>
    private Task<(int Attempts, bool Dead)> RecordFailureAsync(Claimed item, EventEnvelope envelope, string consumer, string error, CancellationToken ct) =>
        database.InTenantAsync(item.TenantId, item.EnvironmentId, null, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var row = await db.ConsumerFailures.AsTracking().FirstOrDefaultAsync(f => f.Consumer == consumer && f.EventId == envelope.EventId, ct);
            if (row is null)
            {
                row = new ConsumerFailureRow { TenantId = item.TenantId, EnvironmentId = item.EnvironmentId, Id = Guid.NewGuid(), Consumer = consumer, EventId = envelope.EventId, EventType = envelope.Type, FirstFailedAt = now };
                db.ConsumerFailures.Add(row);
            }

            row.Attempts++;
            row.Dead = row.Attempts >= MaxAttempts;
            row.LastError = Truncate(error);
            row.LastFailedAt = now;
            await db.SaveChangesAsync(ct);
            return (row.Attempts, row.Dead);
        }, ct);

    private Task FailEventAsync(Claimed item, string error, bool dead, int attempts, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));
        return database.InTenantAsync(item.TenantId, item.EnvironmentId, null, db => db.Outbox.Where(o => o.Id == item.Id && o.LockedBy == _worker).ExecuteUpdateAsync(
            s => s.SetProperty(o => o.Status, dead ? "dead" : "pending").SetProperty(o => o.AvailableAt, DateTimeOffset.UtcNow + delay)
                .SetProperty(o => o.LastError, Truncate(error)).SetProperty(o => o.LockedUntil, (DateTimeOffset?)null),
            ct), ct);
    }

    private static string Truncate(string message) => message.Length > 1000 ? message[..1000] : message;

    /// <summary>Removes dispatched events (and their inbox marks) older than the replay window (ADR 0017).</summary>
    public Task<int> PurgeAsync(int days, CancellationToken ct) =>
        database.AnonymousAsync(db => db.Database.SqlQuery<int>($"SELECT kernel.purge_outbox({days}) AS \"Value\"").FirstAsync(ct), ct);

    private sealed record Claimed(Guid Id, Guid TenantId, Guid EnvironmentId, int Attempts);
}

/// <summary>Runs the dispatcher continuously in the worker host.</summary>
public sealed class OutboxDispatcherService(OutboxDispatcher dispatcher, ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    /// <summary>Replay window for dispatched events; audit is the long-term record.</summary>
    public const int RetentionDays = 14;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = DateTimeOffset.UtcNow.AddMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextPurge)
                {
                    var purged = await dispatcher.PurgeAsync(RetentionDays, stoppingToken);
                    logger.LogInformation("Outbox retention removed {Count} dispatched events", purged);
                    nextPurge = DateTimeOffset.UtcNow.AddHours(1);
                }

                var count = await dispatcher.DispatchOnceAsync(stoppingToken);
                await Task.Delay(count > 0 ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch loop failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
