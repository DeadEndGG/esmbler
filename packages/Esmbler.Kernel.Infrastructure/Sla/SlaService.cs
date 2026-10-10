using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Sla;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Sla;

/// <summary>A record's SLA timer as the record page shows it.</summary>
public sealed record SlaTimerView(
    Guid Id,
    string PolicyId,
    string PolicyLabel,
    string TargetId,
    string Label,
    int Cycle,
    string Status,
    string? Outcome,
    int GoalMinutes,
    string? Calendar,
    string? TimeZone,
    DateTimeOffset StartedAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? BreachedAt,
    DateTimeOffset? StoppedAt,
    long ElapsedSeconds,
    long RemainingSeconds,
    bool AtRisk);

/// <summary>
/// SLA timers (ADR 0018, REQ-BIZ-005, REQ-MIG-004). <see cref="ApplyAsync"/> runs inside every record write
/// transaction, after the record row is written and while it is locked, so timers change exactly with the record and in
/// revision order. One durable job per running timer (<c>sla.check</c>, dedup key per timer) fires at its next warning
/// or due time and publishes <c>platform.sla.warning</c> and <c>platform.sla.breached</c>. Timers keep their snapshot.
/// </summary>
public sealed class SlaService(Database database, ConfigStore configs, AccessService access, IClock clock)
{
    public const string JobKind = "sla.check";
    public const string Source = "platform.sla";

    internal async Task ApplyAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, int configRevision, EntitySpec entity, Guid recordId, int revision, Dictionary<string, object?> variables, bool isCreate, Guid? causation, CancellationToken ct)
    {
        var rows = await db.SlaTimers.AsTracking().Where(t => t.RecordId == recordId).ToListAsync(ct);
        if (rows.Count == 0 && !config.SlaPolicies.Any(p => p.Enabled && p.Entity == entity.Id))
        {
            return;
        }

        var timers = rows.Select(ToTimer).ToList();
        var signals = SlaEngine.Apply(config, entity, variables, isCreate, timers, clock.UtcNow, configRevision);
        await SaveAsync(db, ctx, entity.Id, recordId, revision, configRevision, rows, timers, signals, causation, ct);
    }

    /// <summary>
    /// For platform writes outside the record engine (restore, workflow migration): the same evaluation from the
    /// record's data. <paramref name="startAsCreate"/> starts on-create targets again (a restored record).
    /// </summary>
    internal Task ApplyDataAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, int configRevision, EntitySpec entity, Guid recordId, int revision, JsonObject data, JsonObject? prior, bool startAsCreate, CancellationToken ct) =>
        ApplyAsync(db, ctx, config, configRevision, entity, recordId, revision, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["record"] = Expressions.Values.MapFromJson(data),
            ["prior"] = Expressions.Values.MapFromJson(prior),
            ["now"] = Kernel.Records.FieldNormalizer.FormatInstant(clock.UtcNow),
        }, startAsCreate, null, ct);

    /// <summary>Cancels a deleted record's open timers (a restored record starts fresh ones on its next write).</summary>
    internal async Task CancelAsync(KernelDbContext db, Guid recordId, CancellationToken ct)
    {
        var open = await db.SlaTimers.AsTracking().Where(t => t.RecordId == recordId && (t.Status == SlaStatuses.Running || t.Status == SlaStatuses.Paused)).ToListAsync(ct);
        foreach (var row in open)
        {
            row.Status = SlaStatuses.Cancelled;
            row.DueAt = null;
            row.WarnAt = null;
            row.SegmentStartedAt = null;
            row.UpdatedAt = clock.UtcNow;
            await JobScheduler.CancelAsync(db, JobKey(row.Id), ct);
        }
    }

    /// <summary>The scheduled check of one timer: publishes a warning or breach whose time has come, or reschedules.</summary>
    internal async Task CheckAsync(KernelDbContext db, Guid tenantId, Guid environmentId, Guid timerId, Guid jobId, CancellationToken ct)
    {
        // This job leaves the live set now, so the next check can take the timer's dedup key in this transaction.
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "succeeded").SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
        var recordId = await db.SlaTimers.Where(t => t.Id == timerId).Select(t => (Guid?)t.RecordId).FirstOrDefaultAsync(ct);
        if (recordId is null)
        {
            return;
        }

        // Lock the record first, then read the timer: a concurrent write either committed before (and is seen here) or
        // waits and then finds the next check pending, which it can move.
        var record = (await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE id = {recordId} AND deleted_at IS NULL FOR UPDATE").AsNoTracking().ToListAsync(ct)).FirstOrDefault();
        var row = await db.SlaTimers.AsTracking().FirstAsync(t => t.Id == timerId, ct);
        if (record is null || row.Status != SlaStatuses.Running)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var timer = ToTimer(row);
        var signals = SlaEngine.Check(timer, clock.UtcNow);
        var ctx = new RequestContext { TenantId = tenantId, EnvironmentId = environmentId, PrincipalId = Guid.Empty, Channel = "worker", CorrelationId = Guid.NewGuid() };
        var configRevision = await db.ActiveConfig.Select(a => (int?)a.Revision).FirstOrDefaultAsync(ct);
        await SaveAsync(db, ctx, row.EntityId, row.RecordId, record.Revision, configRevision, [row], [timer], signals, null, ct, force: true);
    }

    /// <summary>The record's timers, for people who can read the record.</summary>
    public Task<IReadOnlyList<SlaTimerView>> ForRecordAsync(RequestContext ctx, string entityId, Guid recordId, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<SlaTimerView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var entity = artifact.Config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
            var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId && r.EntityId == entityId && r.DeletedAt == null, ct) ?? throw KernelException.NotFound("Record");
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            if (!perms.CanRead(entity, JsonNode.Parse(row.Data)!.AsObject()))
            {
                throw KernelException.NotFound("Record");
            }

            var now = clock.UtcNow;
            var timers = await db.SlaTimers.AsNoTracking().Where(t => t.RecordId == recordId && t.Status != SlaStatuses.Cancelled).OrderBy(t => t.StartedAt).ToListAsync(ct);
            return timers.GroupBy(t => (t.PolicyId, t.TargetId)).Select(g => g.MaxBy(t => t.Cycle)!).Select(t => View(ToTimer(t), now)).ToList();
        }, ct);

    public static string JobKey(Guid timerId) => $"sla:{timerId:N}";

    private static SlaTimerView View(SlaTimer t, DateTimeOffset now)
    {
        var remaining = (long)SlaEngine.Remaining(t, now).TotalSeconds;
        var measured = (long)SlaEngine.Measured(t, now).TotalSeconds;
        var atRisk = t.Status is SlaStatuses.Running or SlaStatuses.Paused && t.BreachedAt is null && (t.WarnedAt is not null || (t.Snapshot.Target.WarnAtPercent is { } p && measured * 100 >= t.GoalSeconds * p));
        return new SlaTimerView(
            t.Id,
            t.PolicyId,
            t.Snapshot.PolicyLabel,
            t.TargetId,
            t.Snapshot.Target.Label,
            t.Cycle,
            t.Status,
            t.Outcome,
            t.Snapshot.GoalMinutes,
            t.Snapshot.Calendar?.Label,
            t.Snapshot.Calendar?.TimeZone,
            t.StartedAt,
            t.DueAt,
            t.BreachedAt,
            t.StoppedAt,
            measured,
            remaining,
            atRisk);
    }

    private async Task SaveAsync(KernelDbContext db, RequestContext ctx, string entityId, Guid recordId, int revision, int? configRevision, List<SlaTimerRow> rows, List<SlaTimer> timers, List<SlaSignal> signals, Guid? causation, CancellationToken ct, bool force = false)
    {
        var now = clock.UtcNow;
        foreach (var timer in timers.Where(t => t.Changed || force))
        {
            var row = rows.Find(r => r.Id == timer.Id);
            if (row is null)
            {
                row = new SlaTimerRow { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, Id = timer.Id, RecordId = recordId, EntityId = entityId, PolicyId = timer.PolicyId, TargetId = timer.TargetId, Cycle = timer.Cycle, ConfigRevision = timer.ConfigRevision, Snapshot = JsonSerializer.Serialize(timer.Snapshot, SpecJson.Options), GoalSeconds = timer.GoalSeconds, StartedAt = timer.StartedAt };
                db.SlaTimers.Add(row);
            }

            row.Status = timer.Status;
            row.ElapsedSeconds = timer.ElapsedSeconds;
            row.SegmentStartedAt = timer.SegmentStartedAt;
            row.DueAt = timer.DueAt;
            row.WarnAt = timer.WarnAt;
            row.WarnedAt = timer.WarnedAt;
            row.BreachedAt = timer.BreachedAt;
            row.StoppedAt = timer.StoppedAt;
            row.Outcome = timer.Outcome;
            row.UpdatedAt = now;
            await ScheduleAsync(db, ctx, timer, ct);
        }

        foreach (var signal in signals)
        {
            var breached = signal.Kind == SlaOutcomes.Breached;
            EventWriter.AddPlatform(
                db,
                ctx,
                breached ? EventTypes.SlaBreached : EventTypes.SlaWarning,
                Source,
                entityId,
                recordId,
                revision,
                configRevision,
                new JsonObject
                {
                    ["policy"] = signal.Timer.PolicyId,
                    ["target"] = signal.Timer.TargetId,
                    ["goal_minutes"] = signal.Timer.Snapshot.GoalMinutes,
                    ["due_at"] = (signal.Timer.BreachedAt ?? signal.Timer.DueAt)?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                    ["timer"] = signal.Timer.Id.ToString(),
                },
                breached ? EventSeverities.High : EventSeverities.Medium,
                causation);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>One live check per timer, at its next warning or due time; none once it is paused, stopped or breached.</summary>
    private static async Task ScheduleAsync(KernelDbContext db, RequestContext ctx, SlaTimer timer, CancellationToken ct)
    {
        DateTimeOffset? next = timer.Status != SlaStatuses.Running || timer.BreachedAt is not null ? null
            : timer.WarnedAt is null && timer.WarnAt is { } warn ? warn
            : timer.DueAt;
        if (next is null)
        {
            await JobScheduler.CancelAsync(db, JobKey(timer.Id), ct);
            return;
        }

        await JobScheduler.ScheduleAsync(db, ctx.TenantId, ctx.EnvironmentId, new JobRequest(JobKind, new JsonObject { ["timer"] = timer.Id.ToString() })
        {
            RunAt = next,
            DedupKey = JobKey(timer.Id),
            ReplaceExisting = true,
            MaxAttempts = 5,
        }, ct);
    }

    private static SlaTimer ToTimer(SlaTimerRow row) => new()
    {
        Id = row.Id,
        PolicyId = row.PolicyId,
        TargetId = row.TargetId,
        Cycle = row.Cycle,
        ConfigRevision = row.ConfigRevision,
        Snapshot = JsonSerializer.Deserialize<SlaSnapshot>(row.Snapshot, SpecJson.Options)!,
        Status = row.Status,
        StartedAt = row.StartedAt,
        ElapsedSeconds = row.ElapsedSeconds,
        SegmentStartedAt = row.SegmentStartedAt,
        DueAt = row.DueAt,
        WarnAt = row.WarnAt,
        WarnedAt = row.WarnedAt,
        BreachedAt = row.BreachedAt,
        StoppedAt = row.StoppedAt,
        Outcome = row.Outcome,
    };
}

/// <summary>Fires at an SLA timer's warning or due time (ADR 0018). A platform timer set by a published policy; it acts for nobody.</summary>
public sealed class SlaCheckJobHandler(SlaService sla) : IJobHandler
{
    public string Kind => SlaService.JobKind;

    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            await sla.CheckAsync(db, job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["timer"]!.GetValue<string>()), job.Id, ct);
            return 0;
        }, ct);
}
