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
/// Transactional outbox dispatcher (ADR 0005, REQ-EVT-002). Claims batches across tenants through the privileged
/// claim function, then processes each event in its own tenant-context transaction: consumers already recorded in
/// the inbox are skipped (duplicates), the rest run and are recorded atomically. Failures back off exponentially
/// and dead-letter after <see cref="MaxAttempts"/>.
/// </summary>
public sealed class OutboxDispatcher(Database database, IEnumerable<IEventConsumer> consumers, ILogger<OutboxDispatcher> logger)
{
    public const int MaxAttempts = 10;
    private readonly IReadOnlyList<IEventConsumer> _consumers = consumers.ToList();
    private readonly string _worker = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var claimed = await database.AnonymousAsync(db => db.Database.SqlQuery<Claimed>($"SELECT id AS \"Id\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", attempts AS \"Attempts\" FROM kernel.claim_outbox({_worker}, 50, 60)").ToListAsync(ct), ct);
        foreach (var item in claimed)
        {
            try
            {
                await database.InTenantAsync(item.TenantId, item.EnvironmentId, null, async db =>
                {
                    var row = await db.Outbox.FirstAsync(o => o.Id == item.Id, ct);
                    var envelope = row.Type == EventTypes.ConfigPublished ? null : JsonSerializer.Deserialize<EventEnvelope>(row.Event, SpecJson.Options);
                    if (envelope is not null)
                    {
                        foreach (var consumer in _consumers.Where(c => c.Handles(envelope)))
                        {
                            if (await db.Inbox.AnyAsync(i => i.Consumer == consumer.Name && i.EventId == envelope.EventId, ct))
                            {
                                continue;
                            }

                            await consumer.HandleAsync(envelope, db, ct);
                            db.Inbox.Add(new InboxRow { Consumer = consumer.Name, EventId = envelope.EventId, TenantId = item.TenantId, EnvironmentId = item.EnvironmentId, ProcessedAt = DateTimeOffset.UtcNow });
                            await db.SaveChangesAsync(ct);
                        }
                    }

                    await db.Outbox.Where(o => o.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "dispatched").SetProperty(o => o.DispatchedAt, DateTimeOffset.UtcNow).SetProperty(o => o.LockedUntil, (DateTimeOffset?)null), ct);
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Event {Event} failed (attempt {Attempt})", item.Id, item.Attempts);
                var dead = item.Attempts >= MaxAttempts;
                var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, item.Attempts - 1)));
                var message = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                await database.InTenantAsync(item.TenantId, item.EnvironmentId, null, db => db.Outbox.Where(o => o.Id == item.Id).ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.Status, dead ? "dead" : "pending").SetProperty(o => o.AvailableAt, DateTimeOffset.UtcNow + delay)
                        .SetProperty(o => o.LastError, message).SetProperty(o => o.LockedUntil, (DateTimeOffset?)null),
                    ct), ct);
            }
        }

        return claimed.Count;
    }

    private sealed record Claimed(Guid Id, Guid TenantId, Guid EnvironmentId, int Attempts);
}

/// <summary>Runs the dispatcher continuously in the worker host.</summary>
public sealed class OutboxDispatcherService(OutboxDispatcher dispatcher, ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
