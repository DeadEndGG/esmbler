using System.Collections.Concurrent;
using Esmbler.Expressions;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Sla;

/// <summary>What a timer measures, frozen when it starts (REQ-MIG-004): later policy or calendar edits never change it.</summary>
public sealed class SlaSnapshot
{
    public required string PolicyId { get; set; }

    public required string PolicyLabel { get; set; }

    public required SlaTargetSpec Target { get; set; }

    public required int GoalMinutes { get; set; }

    /// <summary>The business calendar the goal is measured in; null for elapsed time.</summary>
    public BusinessCalendarSpec? Calendar { get; set; }
}

public static class SlaStatuses
{
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Stopped = "stopped";
    public const string Cancelled = "cancelled";
}

public static class SlaOutcomes
{
    public const string Met = "met";
    public const string Breached = "breached";
}

/// <summary>One SLA timer of a record (the persisted state, mapped by the infrastructure).</summary>
public sealed class SlaTimer
{
    public required Guid Id { get; init; }

    public required string PolicyId { get; init; }

    public required string TargetId { get; init; }

    /// <summary>Increments when a reopened record restarts the target.</summary>
    public int Cycle { get; init; }

    public required SlaSnapshot Snapshot { get; init; }

    /// <summary>Configuration revision the timer started under (breach reports group by it).</summary>
    public int? ConfigRevision { get; init; }

    public string Status { get; set; } = SlaStatuses.Running;

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Measured seconds before the current running segment.</summary>
    public long ElapsedSeconds { get; set; }

    /// <summary>When the current running segment began; null while paused or stopped.</summary>
    public DateTimeOffset? SegmentStartedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public DateTimeOffset? WarnAt { get; set; }

    public DateTimeOffset? WarnedAt { get; set; }

    public DateTimeOffset? BreachedAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    public string? Outcome { get; set; }

    public bool IsNew { get; init; }

    public bool Changed { get; set; }

    public long GoalSeconds => Snapshot.GoalMinutes * 60L;
}

/// <summary>A notable moment of a timer, published as a platform event (<c>platform.sla.warning</c>, <c>.breached</c>).</summary>
public sealed record SlaSignal(SlaTimer Timer, string Kind);

/// <summary>
/// The SLA engine (ADR 0018, REQ-BIZ-005): after each write of a record, starts, pauses, resumes, stops and restarts
/// the record's timers from conditions over the record, and detects warnings and breaches. Pure: the caller loads and
/// saves timers and schedules the warning and breach checks at <see cref="SlaTimer.WarnAt"/> and <see cref="SlaTimer.DueAt"/>.
/// New timers follow the current policies; running timers follow the snapshot they started with.
/// </summary>
public static class SlaEngine
{
    public static List<SlaSignal> Apply(SpecCatalog config, EntitySpec entity, Dictionary<string, object?> variables, bool isCreate, List<SlaTimer> timers, DateTimeOffset now, int? configRevision = null)
    {
        var signals = new List<SlaSignal>();

        // Existing timers move on their snapshot's conditions. A deadline that passed before this write is a breach
        // at the deadline, whatever the write does next (pause, stop).
        foreach (var timer in Latest(timers).ToList())
        {
            Check(timer, now, signals);
            var target = timer.Snapshot.Target;
            var stop = Holds(entity, target.Stop, variables);
            var pause = Holds(entity, target.Pause, variables);
            switch (timer.Status)
            {
                case SlaStatuses.Running when stop:
                    Accrue(timer, now);
                    StopAt(timer, now, signals);
                    break;
                case SlaStatuses.Running when pause:
                    Accrue(timer, now);
                    timer.Status = SlaStatuses.Paused;
                    Schedule(timer);
                    break;
                case SlaStatuses.Paused when stop:
                    StopAt(timer, now, signals);
                    break;
                case SlaStatuses.Paused when !pause:
                    timer.Status = SlaStatuses.Running;
                    timer.SegmentStartedAt = now;
                    Schedule(timer);
                    timer.Changed = true;
                    break;
                case SlaStatuses.Stopped when !stop && target.OnReopen == SlaReopen.Resume:
                    timer.Status = pause ? SlaStatuses.Paused : SlaStatuses.Running;
                    timer.SegmentStartedAt = pause ? null : now;
                    timer.StoppedAt = null;
                    timer.Outcome = null;
                    Schedule(timer);
                    timer.Changed = true;
                    break;
                case SlaStatuses.Stopped when !stop && target.OnReopen == SlaReopen.Restart:
                    if (Start(config, entity, variables, timer.PolicyId, target.Id, NextCycle(timers, timer.PolicyId, target.Id), now, configRevision) is { } restarted)
                    {
                        timers.Add(restarted);
                    }

                    break;
            }

            Check(timer, now, signals);
        }

        // Targets without a live timer start when their start condition holds and their stop condition does not. A
        // target whose timers were all cancelled (a deleted, then restored record) starts again in a new cycle.
        foreach (var policy in config.SlaPolicies.Where(p => p.Entity == entity.Id && Applies(p, entity, variables)))
        {
            foreach (var target in policy.Targets)
            {
                if (timers.Any(t => t.PolicyId == policy.Id && t.TargetId == target.Id && t.Status != SlaStatuses.Cancelled))
                {
                    continue;
                }

                var starts = target.Start is null ? isCreate : Holds(entity, target.Start, variables);
                if (starts && !Holds(entity, target.Stop, variables) && Start(config, entity, variables, policy.Id, target.Id, NextCycle(timers, policy.Id, target.Id), now, configRevision) is { } timer)
                {
                    timers.Add(timer);
                    Check(timer, now, signals);
                }
            }
        }

        return signals;
    }

    /// <summary>A scheduled check: publishes the warning or the breach once their time has come; reschedules otherwise.</summary>
    public static List<SlaSignal> Check(SlaTimer timer, DateTimeOffset now)
    {
        var signals = new List<SlaSignal>();
        Check(timer, now, signals);
        return signals;
    }

    /// <summary>Remaining measured time, or overrun as a negative value; for a running timer as of <paramref name="now"/>.</summary>
    public static TimeSpan Remaining(SlaTimer timer, DateTimeOffset now) =>
        TimeSpan.FromSeconds(timer.GoalSeconds) - Measured(timer, now);

    /// <summary>Measured time so far (business or elapsed).</summary>
    public static TimeSpan Measured(SlaTimer timer, DateTimeOffset now)
    {
        var measured = TimeSpan.FromSeconds(timer.ElapsedSeconds);
        if (timer.Status == SlaStatuses.Running && timer.SegmentStartedAt is { } segment)
        {
            measured += BusinessTime.For(timer.Snapshot.Calendar).Between(segment, now);
        }

        return measured;
    }

    private static IEnumerable<SlaTimer> Latest(List<SlaTimer> timers) =>
        timers.Where(t => t.Status != SlaStatuses.Cancelled).GroupBy(t => (t.PolicyId, t.TargetId)).Select(g => g.MaxBy(t => t.Cycle)!);

    private static bool Applies(SlaPolicySpec policy, EntitySpec entity, Dictionary<string, object?> variables) =>
        policy.Enabled && (policy.When is null || Holds(entity, policy.When, variables));

    /// <summary>The next cycle of a target: one past every earlier timer of it, cancelled ones included (cycles are unique).</summary>
    private static int NextCycle(List<SlaTimer> timers, string policyId, string targetId) =>
        timers.Where(t => t.PolicyId == policyId && t.TargetId == targetId).Select(t => t.Cycle + 1).DefaultIfEmpty(0).Max();

    private static SlaTimer? Start(SpecCatalog config, EntitySpec entity, Dictionary<string, object?> variables, string policyId, string targetId, int cycle, DateTimeOffset now, int? configRevision)
    {
        // New timers, restarts included, follow the current policy: a disabled or no longer applicable policy starts none.
        var policy = config.SlaPolicies.Find(p => p.Id == policyId);
        var target = policy?.FindTarget(targetId);
        if (policy is null || target is null || !Applies(policy, entity, variables))
        {
            return null;
        }

        var goal = target.Goals.FirstOrDefault(g => g.When is null || Holds(entity, g.When, variables));
        if (goal is null)
        {
            return null;
        }

        var calendarId = goal.Calendar ?? target.Calendar;
        var calendar = calendarId is null or SlaCalendars.AroundTheClock ? null : config.BusinessCalendars.Find(c => c.Id == calendarId);
        var pause = Holds(entity, target.Pause, variables);
        var timer = new SlaTimer
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            TargetId = target.Id,
            Cycle = cycle,
            ConfigRevision = configRevision,
            Snapshot = new SlaSnapshot { PolicyId = policy.Id, PolicyLabel = policy.Label, Target = Sdk.Specs.SpecJson.Clone(target), GoalMinutes = goal.Minutes, Calendar = calendar is null ? null : Sdk.Specs.SpecJson.Clone(calendar) },
            Status = pause ? SlaStatuses.Paused : SlaStatuses.Running,
            StartedAt = now,
            SegmentStartedAt = pause ? null : now,
            IsNew = true,
            Changed = true,
        };
        Schedule(timer);
        return timer;
    }

    private static void Accrue(SlaTimer timer, DateTimeOffset now)
    {
        if (timer.SegmentStartedAt is { } segment)
        {
            timer.ElapsedSeconds += (long)BusinessTime.For(timer.Snapshot.Calendar).Between(segment, now).TotalSeconds;
        }

        timer.SegmentStartedAt = null;
        timer.Changed = true;
    }

    private static void StopAt(SlaTimer timer, DateTimeOffset now, List<SlaSignal> signals)
    {
        timer.Status = SlaStatuses.Stopped;
        timer.StoppedAt = now;
        timer.SegmentStartedAt = null;
        if (timer.BreachedAt is null && timer.ElapsedSeconds > timer.GoalSeconds)
        {
            timer.BreachedAt = timer.DueAt is { } due && due <= now ? due : now;
            signals.Add(new SlaSignal(timer, SlaOutcomes.Breached));
        }

        timer.Outcome = timer.BreachedAt is null ? SlaOutcomes.Met : SlaOutcomes.Breached;
        timer.DueAt = null;
        timer.WarnAt = null;
        timer.Changed = true;
    }

    /// <summary>Due and warning instants for a running timer; none while paused or stopped.</summary>
    private static void Schedule(SlaTimer timer)
    {
        if (timer.Status != SlaStatuses.Running || timer.SegmentStartedAt is not { } segment)
        {
            timer.DueAt = null;
            timer.WarnAt = null;
            return;
        }

        var time = BusinessTime.For(timer.Snapshot.Calendar);
        var left = TimeSpan.FromSeconds(Math.Max(0, timer.GoalSeconds - timer.ElapsedSeconds));
        timer.DueAt = time.Add(segment, left);
        timer.WarnAt = null;
        if (timer.Snapshot.Target.WarnAtPercent is { } percent && timer.WarnedAt is null)
        {
            var threshold = timer.GoalSeconds * percent / 100;
            if (timer.ElapsedSeconds < threshold)
            {
                timer.WarnAt = time.Add(segment, TimeSpan.FromSeconds(threshold - timer.ElapsedSeconds));
            }
        }
    }

    private static void Check(SlaTimer timer, DateTimeOffset now, List<SlaSignal> signals)
    {
        if (timer.Status != SlaStatuses.Running)
        {
            if (timer.Status == SlaStatuses.Paused && timer.BreachedAt is null && timer.ElapsedSeconds > timer.GoalSeconds)
            {
                timer.BreachedAt = now;
                timer.Changed = true;
                signals.Add(new SlaSignal(timer, SlaOutcomes.Breached));
            }

            return;
        }

        if (timer.BreachedAt is null && timer.DueAt is { } due && due <= now)
        {
            timer.BreachedAt = due;
            timer.Changed = true;
            signals.Add(new SlaSignal(timer, SlaOutcomes.Breached));
        }
        else if (timer.WarnedAt is null && timer.BreachedAt is null && timer.WarnAt is { } warn && warn <= now)
        {
            timer.WarnedAt = now;
            timer.WarnAt = null;
            timer.Changed = true;
            signals.Add(new SlaSignal(timer, "warning"));
        }
    }

    private static readonly ConcurrentDictionary<string, CompiledExpression> Compiled = new(StringComparer.Ordinal);

    /// <summary>
    /// Evaluates a condition without the entity schema: snapshot conditions must keep working after a field they name is
    /// removed (it reads as null), and the validator already type-checked them against the schema they were written for.
    /// </summary>
    private static bool Holds(EntitySpec entity, string? condition, Dictionary<string, object?> variables)
    {
        _ = entity;
        if (string.IsNullOrWhiteSpace(condition))
        {
            return false;
        }

        try
        {
            if (!Compiled.TryGetValue(condition, out var compiled))
            {
                compiled = Expression.Compile(condition);
                if (Compiled.Count < 10_000)
                {
                    Compiled[condition] = compiled;
                }
            }

            return compiled.EvaluateBool(variables);
        }
        catch (ExpressionException)
        {
            return false; // the validator checks conditions; a value that cannot be evaluated never starts, pauses or stops a timer
        }
    }
}
