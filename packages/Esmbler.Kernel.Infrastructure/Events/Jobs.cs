using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// A durable job kind (ADR 0005, REQ-EVT-005). Handlers run outside the claim transaction, open their own tenant
/// transactions through <see cref="JobContext.Database"/>, and must be idempotent: a job can run more than once
/// after a crash. Long jobs checkpoint and renew their lease.
/// </summary>
public interface IJobHandler
{
    string Kind { get; }

    /// <summary>Right the job's creator must still hold when it runs (re-checked every attempt), or null.</summary>
    string? RequiredAction { get; }

    Task RunAsync(JobContext job, CancellationToken ct);

    /// <summary>Called once when the job ends without succeeding (denied, dead-lettered, or failed terminally).</summary>
    Task OnAbandonedAsync(JobContext job, string reason, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Thrown by a handler for a failure that retrying cannot fix; the job fails without further attempts.</summary>
public sealed class JobTerminalException(string message) : Exception(message);

/// <summary>What a handler sees about its job, plus checkpoint and lease operations.</summary>
public sealed class JobContext(Database database, Guid id, Guid tenantId, Guid environmentId, string kind, int attempt, Guid? createdBy, JsonObject payload, JsonNode? checkpoint, string worker)
{
    public Database Database { get; } = database;

    public Guid Id { get; } = id;

    public Guid TenantId { get; } = tenantId;

    public Guid EnvironmentId { get; } = environmentId;

    public string Kind { get; } = kind;

    public int Attempt { get; } = attempt;

    public Guid? CreatedBy { get; } = createdBy;

    public JsonObject Payload { get; } = payload;

    /// <summary>Progress saved by an earlier attempt, if any.</summary>
    public JsonNode? Checkpoint { get; private set; } = checkpoint;

    public async Task SaveCheckpointAsync(JsonNode checkpoint, CancellationToken ct)
    {
        Checkpoint = checkpoint;
        var json = checkpoint.ToJsonString();
        await Database.InTenantAsync(TenantId, EnvironmentId, null, db => db.Jobs.Where(j => j.Id == Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Checkpoint, json).SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct), ct);
    }

    /// <summary>Extends this worker's lease so a long job is not reclaimed by another worker.</summary>
    public Task RenewLeaseAsync(TimeSpan lease, CancellationToken ct) =>
        Database.InTenantAsync(TenantId, EnvironmentId, null, db => db.Jobs.Where(j => j.Id == Id && j.LockedBy == worker).ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedUntil, DateTimeOffset.UtcNow + lease), ct), ct);
}

public sealed record JobRequest(string Kind, JsonObject Payload)
{
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>At most one live job per key; scheduling again keeps or replaces the live one (timers).</summary>
    public string? DedupKey { get; init; }

    public bool ReplaceExisting { get; init; }

    public Guid? CausationId { get; init; }

    public int Depth { get; init; }

    public Guid? CreatedBy { get; init; }

    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// Schedules jobs and timers inside the caller's tenant transaction, so a job exists exactly when the change that
/// needs it commits. Enforces the automation governors: causation depth and fan-out per event (ASM-029). A refused
/// job returns null and leaves an audit entry in the same transaction instead of failing the caller's change.
/// </summary>
public static class JobScheduler
{
    public const int MaxDepth = 8;
    public const int MaxFanOut = 100;

    /// <summary>Per-tenant-environment quota of waiting jobs (REQ-TEN-006); beyond it new work is refused and audited.</summary>
    public const int MaxPendingPerEnvironment = 50_000;

    public static async Task<Guid?> ScheduleAsync(KernelDbContext db, Guid tenantId, Guid environmentId, JobRequest request, CancellationToken ct)
    {
        if (request.Depth > MaxDepth)
        {
            AuditWriter.AddSystem(db, tenantId, environmentId, "automation.depth_exceeded", "job", request.Kind, new { request.Depth, request.CausationId });
            await db.SaveChangesAsync(ct);
            return null;
        }

        if (request.CausationId is { } cause && await db.Jobs.CountAsync(j => j.CausationId == cause, ct) >= MaxFanOut)
        {
            AuditWriter.AddSystem(db, tenantId, environmentId, "automation.fanout_exceeded", "job", request.Kind, new { cause });
            await db.SaveChangesAsync(ct);
            return null;
        }

        // Keyed timers (one live job per SLA timer, approval stage, …) are bounded by the objects they belong to and
        // must never be refused, or a deadline would silently stop firing; the quota caps open-ended work.
        if (request.DedupKey is null && await db.Jobs.CountAsync(j => j.Status == "pending", ct) >= MaxPendingPerEnvironment)
        {
            AuditWriter.AddSystem(db, tenantId, environmentId, "quota.jobs_exceeded", "job", request.Kind, new { limit = MaxPendingPerEnvironment });
            await db.SaveChangesAsync(ct);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (request.DedupKey is { } key)
        {
            var live = await db.Jobs.AsTracking().FirstOrDefaultAsync(j => j.DedupKey == key && (j.Status == "pending" || j.Status == "running"), ct);
            if (live is not null)
            {
                if (request.ReplaceExisting && live.Status == "pending")
                {
                    live.Payload = request.Payload.ToJsonString();
                    live.AvailableAt = request.RunAt ?? now;
                    live.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                }

                return live.Id;
            }
        }

        var row = new JobRow
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Kind = request.Kind,
            Payload = request.Payload.ToJsonString(),
            MaxAttempts = request.MaxAttempts,
            AvailableAt = request.RunAt ?? now,
            CreatedBy = request.CreatedBy,
            CreatedAt = now,
            UpdatedAt = now,
            DedupKey = request.DedupKey,
            CausationId = request.CausationId,
            Depth = request.Depth,
        };
        db.Jobs.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    /// <summary>Cancels the live job with this key (for example a timer whose condition no longer holds).</summary>
    public static Task<int> CancelAsync(KernelDbContext db, string dedupKey, CancellationToken ct) =>
        db.Jobs.Where(j => j.DedupKey == dedupKey && j.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "cancelled").SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
}

/// <summary>Runs module upgrades queued by <see cref="UpgradeService"/>.</summary>
public sealed class UpgradeJobHandler(UpgradeService upgrades) : IJobHandler
{
    public string Kind => UpgradeService.JobKind;

    public string? RequiredAction => PlatformActions.ModulesManage;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        upgrades.RunAsync(job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["upgradeId"]!.GetValue<string>()), ct);

    public Task OnAbandonedAsync(JobContext job, string reason, CancellationToken ct) =>
        upgrades.AbandonAsync(job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["upgradeId"]!.GetValue<string>()), reason, ct);
}
