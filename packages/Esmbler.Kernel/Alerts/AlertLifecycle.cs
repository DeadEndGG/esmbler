using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Alerts;

/// <summary>
/// The alert state machine and escalation timing (ADR 0022), free of storage: an alert opens at level 1, each escalation
/// step raises the level, the last step may repeat (at most <see cref="MaxRepeats"/> times), and acknowledgment stops steps
/// that wait for it.
/// </summary>
public static class AlertLifecycle
{
    public const int MaxRepeats = 20;

    /// <summary>When the alert at <paramref name="level"/> escalates next, and to which level; null when nothing is left.</summary>
    public static (DateTimeOffset At, int Level)? NextEscalation(AlertRuleSpec rule, DateTimeOffset openedAt, int level)
    {
        var index = level - 1;
        if (index < rule.Escalation.Count)
        {
            return (openedAt.AddMinutes(rule.Escalation[index].AfterMinutes), level + 1);
        }

        if (rule.Escalation.Count == 0 || rule.Escalation[^1].RepeatEveryMinutes is not { } every)
        {
            return null;
        }

        var repeat = index - rule.Escalation.Count + 1;
        return repeat > MaxRepeats ? null : (openedAt.AddMinutes(rule.Escalation[^1].AfterMinutes + ((long)every * repeat)), level + 1);
    }

    /// <summary>The step that raises an alert to <paramref name="level"/> (repeats use the last step).</summary>
    public static AlertEscalationStepSpec? StepFor(AlertRuleSpec rule, int level) =>
        rule.Escalation.Count == 0 || level < 2 ? null : rule.Escalation[Math.Min(level - 2, rule.Escalation.Count - 1)];

    /// <summary>Whether an escalation to <paramref name="level"/> applies to an alert in <paramref name="status"/>.</summary>
    public static bool Escalates(AlertRuleSpec rule, string status, int level) =>
        status == AlertStatuses.Open || (status == AlertStatuses.Acknowledged && StepFor(rule, level) is { UnlessAcknowledged: false });

    /// <summary>Whether any step after <paramref name="level"/> escalates even when the alert is acknowledged.</summary>
    public static bool LaterStepIgnoresAcknowledgment(AlertRuleSpec rule, int level) =>
        rule.Escalation.Skip(Math.Max(0, level - 1)).Any(s => !s.UnlessAcknowledged);

    /// <summary>A new match reopens a resolved alert within the rule's cool-down.</summary>
    public static bool Reopens(AlertRuleSpec rule, DateTimeOffset resolvedAt, DateTimeOffset now) =>
        rule.ReopenWithinMinutes > 0 && now - resolvedAt <= TimeSpan.FromMinutes(rule.ReopenWithinMinutes);

    /// <summary>Allowed moves: open and suppressed to acknowledged or resolved, acknowledged to resolved, suppressed to open.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (AlertStatuses.Open, AlertStatuses.Acknowledged or AlertStatuses.Resolved) => true,
        (AlertStatuses.Suppressed, AlertStatuses.Open or AlertStatuses.Acknowledged or AlertStatuses.Resolved) => true,
        (AlertStatuses.Acknowledged, AlertStatuses.Resolved) => true,
        (AlertStatuses.Resolved, AlertStatuses.Open) => true,
        _ => false,
    };
}
