using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Durable job runner (ADR 0005, REQ-EVT-005/006). Claims jobs fairly across tenants (one running job per tenant per
/// round), leases them, and dispatches to the <see cref="IJobHandler"/> for their kind. Before each attempt the job
/// is re-authorized as the person who queued it, so a job never outlives its creator's rights (REQ-AUTHZ-006); a
/// denied job fails closed without retrying. Other failures back off with jitter and dead-letter after their
/// maximum attempts, where an operator can inspect and replay them.
/// </summary>
public sealed class JobRunner(Database database, IEnumerable<IJobHandler> handlers, AccessService access, ConfigStore configs, ILogger<JobRunner> logger)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, IJobHandler> byKind = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
    private readonly string worker = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var leaseSeconds = (int)Lease.TotalSeconds;
        var claimed = await database.AnonymousAsync(db => db.Database.SqlQuery<Claimed>($"SELECT id AS \"Id\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", kind AS \"Kind\", attempts AS \"Attempts\" FROM kernel.claim_jobs({worker}, 10, {leaseSeconds})").ToListAsync(ct), ct);
        foreach (var job in claimed)
        {
            await RunOneAsync(job, ct);
        }

        return claimed.Count;
    }

    private async Task RunOneAsync(Claimed job, CancellationToken ct)
    {
        var row = await database.InTenantAsync(job.TenantId, job.EnvironmentId, null, db => db.Jobs.FirstAsync(j => j.Id == job.Id, ct), ct);
        var context = new JobContext(database, job.Id, job.TenantId, job.EnvironmentId, job.Kind, job.Attempts, row.CreatedBy, JsonNode.Parse(row.Payload) as JsonObject ?? [], row.Checkpoint is null ? null : JsonNode.Parse(row.Checkpoint), worker);
        string status;
        string? error = null;
        byKind.TryGetValue(job.Kind, out var handler);
        try
        {
            if (handler is null)
            {
                throw new JobTerminalException($"No handler for job kind '{job.Kind}'.");
            }

            if (await ReauthorizeAsync(job, row.CreatedBy, handler.RequiredAction, ct) is { } denial)
            {
                throw new JobTerminalException(denial);
            }

            await handler.RunAsync(context, ct);
            status = "succeeded";
        }
        catch (JobTerminalException ex)
        {
            status = "failed";
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Job {Job} ({Kind}) failed on attempt {Attempt}", job.Id, job.Kind, job.Attempts);
            status = job.Attempts >= row.MaxAttempts ? "dead" : "pending";
            error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }

        if (status is "failed" or "dead" && handler is not null)
        {
            try
            {
                await handler.OnAbandonedAsync(context, error ?? "Abandoned.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Abandon hook for job {Job} failed", job.Id);
            }
        }

        var availableAt = status == "pending" ? DateTimeOffset.UtcNow + Backoff(job.Attempts) : DateTimeOffset.UtcNow;
        await database.InTenantAsync(job.TenantId, job.EnvironmentId, null, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(
            s => s.SetProperty(j => j.Status, status)
                .SetProperty(j => j.AvailableAt, availableAt)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow),
            ct), ct);
    }

    /// <summary>min(5 s × 2^(attempt−1), 15 min) with ±20% jitter so retries from many jobs don't align.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(seconds * (0.8 + (Random.Shared.NextDouble() * 0.4)));
    }

    private async Task<string?> ReauthorizeAsync(Claimed job, Guid? createdBy, string? action, CancellationToken ct)
    {
        if (action is null)
        {
            return null;
        }

        if (createdBy is null)
        {
            return "The job has no owner, so it can't be authorized.";
        }

        var ctx = new RequestContext { TenantId = job.TenantId, EnvironmentId = job.EnvironmentId, PrincipalId = createdBy.Value, Channel = "job", CorrelationId = job.Id };
        try
        {
            var allowed = await database.InTenantAsync(job.TenantId, job.EnvironmentId, createdBy, async db =>
            {
                var published = await configs.GetActiveAsync(db, job.TenantId, job.EnvironmentId, ct);
                var perms = await access.ForAsync(db, ctx, published.Config, ct, fresh: true);
                return perms.Can(action);
            }, ct);
            return allowed ? null : $"The person who started this job no longer has the {action} right, so it was stopped.";
        }
        catch (KernelException ex) when (ex.Status == 403)
        {
            return $"The person who started this job can no longer act in this workspace ({ex.Message}).";
        }
    }

    private sealed record Claimed(Guid Id, Guid TenantId, Guid EnvironmentId, string Kind, int Attempts);
}

public sealed class JobRunnerService(JobRunner runner, ILogger<JobRunnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await runner.RunOnceAsync(stoppingToken);
                await Task.Delay(count > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job loop failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
