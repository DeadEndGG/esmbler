using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Durable job runner (ADR 0005, REQ-EVT-005). Claims jobs fairly across tenants (one running job per tenant),
/// leases them, and records the outcome. Handlers checkpoint their own progress so a reclaimed job resumes.
/// </summary>
public sealed class JobRunner(Database database, UpgradeService upgrades, ILogger<JobRunner> logger)
{
    private readonly string _worker = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await database.AnonymousAsync(db => db.Database.SqlQuery<Claimed>($"SELECT id AS \"Id\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", kind AS \"Kind\", attempts AS \"Attempts\" FROM kernel.claim_jobs({_worker}, 10, 300)").ToListAsync(ct), ct);
        foreach (var job in claimed)
        {
            string status;
            string? error = null;
            try
            {
                var payload = await database.InTenantAsync(job.TenantId, job.EnvironmentId, null, db => db.Jobs.Where(j => j.Id == job.Id).Select(j => j.Payload).FirstAsync(ct), ct);
                switch (job.Kind)
                {
                    case UpgradeService.JobKind:
                        await upgrades.RunAsync(job.TenantId, job.EnvironmentId, Guid.Parse(JsonNode.Parse(payload)!["upgradeId"]!.GetValue<string>()), ct);
                        break;
                    default:
                        throw new InvalidOperationException($"No handler for job kind '{job.Kind}'.");
                }

                status = "succeeded";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Job {Job} ({Kind}) failed", job.Id, job.Kind);
                status = "failed";
                error = ex.Message;
            }

            await database.InTenantAsync(job.TenantId, job.EnvironmentId, null, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, j => status == "failed" && j.Attempts < j.MaxAttempts ? "pending" : status)
                    .SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(5 * Math.Pow(2, job.Attempts)))
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow),
                ct), ct);
        }

        return claimed.Count;
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
