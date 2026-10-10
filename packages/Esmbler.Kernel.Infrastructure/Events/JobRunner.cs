using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Durable job runner (ADR 0005, REQ-EVT-005). Claims jobs fairly across tenants (one running job per tenant),
/// leases them, and records the outcome. Handlers checkpoint their own progress so a reclaimed job resumes.
/// Before each attempt the job is re-authorized as the person who queued it: a job never outlives its creator's
/// rights (REQ-AUTHZ-006, REQ-ACC-07c), and a denied job fails closed without retrying.
/// </summary>
public sealed class JobRunner(Database database, UpgradeService upgrades, AccessService access, ConfigStore configs, ILogger<JobRunner> logger)
{
    /// <summary>The right each job kind needs from the principal who queued it.</summary>
    private static readonly Dictionary<string, string> RequiredAction = new(StringComparer.Ordinal)
    {
        [UpgradeService.JobKind] = PlatformActions.ModulesManage,
    };

    private readonly string _worker = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await database.AnonymousAsync(db => db.Database.SqlQuery<Claimed>($"SELECT id AS \"Id\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", kind AS \"Kind\", attempts AS \"Attempts\" FROM kernel.claim_jobs({_worker}, 10, 300)").ToListAsync(ct), ct);
        foreach (var job in claimed)
        {
            string status;
            string? error = null;
            var terminal = false;
            string? payloadText = null;
            try
            {
                var (payload, createdBy) = await database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
                {
                    var row = await db.Jobs.Where(j => j.Id == job.Id).Select(j => new { j.Payload, j.CreatedBy }).FirstAsync(ct);
                    return (row.Payload, row.CreatedBy);
                }, ct);
                payloadText = payload;
                var denial = await ReauthorizeAsync(job, createdBy, ct);
                if (denial is not null)
                {
                    terminal = true;
                    throw new KernelException("job_unauthorized", denial, 403);
                }

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

            if (terminal && job.Kind == UpgradeService.JobKind && payloadText is not null)
            {
                await upgrades.AbandonAsync(job.TenantId, job.EnvironmentId, Guid.Parse(JsonNode.Parse(payloadText)!["upgradeId"]!.GetValue<string>()), error ?? "Not authorized.", ct);
            }

            await database.InTenantAsync(job.TenantId, job.EnvironmentId, null, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, j => status == "failed" && !terminal && j.Attempts < j.MaxAttempts ? "pending" : status)
                    .SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(5 * Math.Pow(2, job.Attempts)))
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow),
                ct), ct);
        }

        return claimed.Count;
    }

    private async Task<string?> ReauthorizeAsync(Claimed job, Guid? createdBy, CancellationToken ct)
    {
        if (!RequiredAction.TryGetValue(job.Kind, out var action))
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
