using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Services;

/// <summary>
/// Audit and retention housekeeping (ADR 0022, REQ-AUD-001, KI-024), all through narrow definer functions: seal new
/// audit entries into each workspace's hash chain, verify chains daily, and purge what retention lets go (audit beyond the
/// policy, final notification deliveries after 90 days, read inbox items after 30 and unread ones after 365, resolved
/// alerts after a year), never during a legal hold.
/// </summary>
public sealed class AuditMaintenance(Database database, ILogger<AuditMaintenance> logger)
{
    public const int DeliveryRetentionDays = 90;
    public const int InboxReadRetentionDays = 30;
    public const int InboxUnreadRetentionDays = 365;
    public const int AlertRetentionDays = 365;

    public sealed class PurgedRow
    {
        public Guid TenantId { get; set; }

        public string Kind { get; set; } = "";

        public int Removed { get; set; }
    }

    public sealed class VerifyRow
    {
        public int Checked { get; set; }

        public long VerifiedThrough { get; set; }

        public long? BrokenAt { get; set; }

        public bool Complete { get; set; }
    }

    public Task<int> SealAsync(int batch, CancellationToken ct) =>
        database.AnonymousAsync(db => db.Database.SqlQuery<int>($"SELECT kernel.seal_audit({batch}) AS \"Value\"").FirstAsync(ct), ct);

    /// <summary>Seals until nothing is left (tests and catch-up).</summary>
    public async Task<int> SealAllAsync(CancellationToken ct)
    {
        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            var n = await SealAsync(1000, ct);
            total += n;
            if (n == 0)
            {
                break;
            }
        }

        return total;
    }

    public Task<List<PurgedRow>> PurgeAsync(CancellationToken ct) =>
        database.AnonymousAsync(db => db.Database.SqlQuery<PurgedRow>($"""
            SELECT tenant_id AS "TenantId", kind AS "Kind", removed AS "Removed"
            FROM kernel.purge_retention({DeliveryRetentionDays}, {InboxReadRetentionDays}, {InboxUnreadRetentionDays}, {AlertRetentionDays}, 5000)
            """).ToListAsync(ct), ct);

    /// <summary>Verifies the chains not verified in the last day; a break is recorded as a workspace-wide failed audit entry.</summary>
    public async Task<int> VerifyDueAsync(CancellationToken ct)
    {
        var due = await database.AnonymousAsync(db => db.Database.SqlQuery<Guid>($"SELECT t AS \"Value\" FROM kernel.audit_verification_due(50) t").ToListAsync(ct), ct);
        foreach (var tenant in due)
        {
            await VerifyAsync(tenant, ct);
        }

        return due.Count;
    }

    public Task<VerifyRow> VerifyAsync(Guid tenant, CancellationToken ct) =>
        database.InTenantWideAsync(tenant, null, async db =>
        {
            var knownBreak = await db.AuditChainHeads.AsNoTracking().Where(h => h.TenantId == tenant).Select(h => h.BrokenAtSeq).FirstOrDefaultAsync(ct);
            VerifyRow step;
            var guard = 0;
            do
            {
                step = await db.Database.SqlQuery<VerifyRow>($"""SELECT checked AS "Checked", verified_through AS "VerifiedThrough", broken_at AS "BrokenAt", complete AS "Complete" FROM kernel.verify_audit_chain(50000)""").FirstAsync(ct);
            }
            while (!step.Complete && ++guard < 100);

            // Report a break once, when it is first found (or moves), not on every verification.
            if (step.BrokenAt is { } at && at != knownBreak)
            {
                logger.LogWarning("Audit chain of workspace {Tenant} is broken at link {Seq}", tenant, at);
                db.AuditLog.Add(new AuditRow
                {
                    TenantId = tenant,
                    EnvironmentId = null,
                    At = DateTimeOffset.UtcNow,
                    Channel = "system",
                    Operation = "audit.chain_broken",
                    TargetType = "audit_log",
                    Outcome = "failed",
                    Details = $$"""{"brokenAt":{{at}}}""",
                });
                await db.SaveChangesAsync(ct);
            }

            return step;
        }, ct);
}

/// <summary>Runs <see cref="AuditMaintenance"/> in the worker: sealing every few seconds, retention hourly, verification daily.</summary>
public sealed class AuditMaintenanceService(AuditMaintenance maintenance, ILogger<AuditMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = DateTimeOffset.UtcNow.AddMinutes(10);
        var nextVerify = DateTimeOffset.UtcNow.AddMinutes(15);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sealedNow = await maintenance.SealAsync(1000, stoppingToken);
                if (DateTimeOffset.UtcNow >= nextPurge)
                {
                    foreach (var p in await maintenance.PurgeAsync(stoppingToken))
                    {
                        logger.LogInformation("Retention removed {Count} {Kind} of workspace {Tenant}", p.Removed, p.Kind, p.TenantId);
                    }

                    nextPurge = DateTimeOffset.UtcNow.AddHours(1);
                }

                if (DateTimeOffset.UtcNow >= nextVerify)
                {
                    await maintenance.VerifyDueAsync(stoppingToken);
                    nextVerify = DateTimeOffset.UtcNow.AddHours(1);
                }

                await Task.Delay(sealedNow >= 1000 ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit maintenance failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
