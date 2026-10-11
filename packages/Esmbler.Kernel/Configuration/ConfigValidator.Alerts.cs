using Esmbler.Expressions;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>Alert rule checks (ADR 0022).</summary>
public sealed partial class ConfigValidator
{
    private const int MaxAlertTitle = 200;
    private const int MaxEscalationSteps = 10;
    private const int MaxEscalationMinutes = 10_080;

    /// <summary>The entity whose records a subscription's events are about.</summary>
    internal static EntitySpec? SubscriptionEntity(SpecCatalog config, SubscriptionSpec sub)
    {
        var entityId = sub.Entity ?? config.FindEventType(sub.Event)?.SubjectEntity;
        return entityId is null ? null : config.FindEntity(entityId);
    }

    private static void ValidateAlertRules(SpecCatalog config, List<Diagnostic> d)
    {
        foreach (var rule in config.AlertRules)
        {
            var trigger = config.FindSubscription(rule.Trigger);
            if (trigger is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Alert rule '{rule.Label}' uses unknown subscription '{rule.Trigger}'.", rule.Id, Expression.Suggest(rule.Trigger, config.Subscriptions.Select(s => s.Id))));
            }
            else if (trigger.Target != SubscriptionTargets.Alert)
            {
                d.Add(Diagnostic.Error("config.bad_target", $"Alert rule '{rule.Label}' is triggered by subscription '{trigger.Label}', which targets {trigger.Target}.", rule.Id, "Use a subscription with target alert."));
            }

            if (trigger is not null && trigger.Event.StartsWith("platform.alert.", StringComparison.Ordinal))
            {
                d.Add(Diagnostic.Error("config.alert_loop", $"Alert rule '{rule.Label}' is triggered by alert events.", rule.Id, "Raise alerts from the events that cause them; route alert events to people with notification routes."));
            }

            if (rule.ResolveOn.Count > 5)
            {
                d.Add(Diagnostic.Error("config.too_large", $"Alert rule '{rule.Label}' resolves on more than 5 subscriptions.", rule.Id));
            }

            foreach (var id in rule.ResolveOn)
            {
                if (config.FindSubscription(id) is not { } resolver)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Alert rule '{rule.Label}' resolves on unknown subscription '{id}'.", rule.Id, Expression.Suggest(id, config.Subscriptions.Select(s => s.Id))));
                }
                else if (resolver.Target != SubscriptionTargets.Alert)
                {
                    d.Add(Diagnostic.Error("config.bad_target", $"Alert rule '{rule.Label}' resolves on subscription '{resolver.Label}', which targets {resolver.Target}.", rule.Id, "Use a subscription with target alert."));
                }
            }

            foreach (var severity in new[] { rule.MinSeverity, rule.Severity }.OfType<string>().Where(s => !EventSeverities.All.Contains(s)))
            {
                d.Add(Diagnostic.Error("config.bad_severity", $"Alert rule '{rule.Label}' uses unknown severity '{severity}'.", rule.Id, $"Use one of: {string.Join(", ", EventSeverities.All)}."));
            }

            if (rule.ReopenWithinMinutes is < 0 or > MaxEscalationMinutes)
            {
                d.Add(Diagnostic.Error("config.alert_bad_escalation", $"Alert rule '{rule.Label}' reopens within {rule.ReopenWithinMinutes} minutes.", rule.Id, $"Use 0 to {MaxEscalationMinutes} minutes."));
            }

            ValidateEscalation(rule, d);
            if (trigger is null)
            {
                continue;
            }

            var type = config.FindEventType(trigger.Event);
            var entity = SubscriptionEntity(config, trigger);
            var hidden = entity is null ? [] : HiddenFields(config, entity);
            var restricted = type?.Sensitivity == EventSensitivities.Restricted;

            // Titles and keys are rendered once and shown to everyone who sees the alert: no hidden values.
            void CheckReads(IEnumerable<string> references, string what)
            {
                if (references.Where(r => r.StartsWith("record.", StringComparison.Ordinal)).Select(r => r["record.".Length..]).FirstOrDefault(hidden.Contains) is { } secret)
                {
                    d.Add(Diagnostic.Error("config.alert_hidden_field", $"The {what} of alert rule '{rule.Label}' reads '{secret}', which some people may not see.", rule.Id, "Use fields everyone who reads the record can see."));
                }

                if (restricted && references.Any(r => r.StartsWith("event.", StringComparison.Ordinal)))
                {
                    d.Add(Diagnostic.Error("config.restricted_event_text", $"The {what} of alert rule '{rule.Label}' reads values of restricted event '{type!.Id}'.", rule.Id, "Use record fields everyone who reads the record can see."));
                }
            }

            if (!string.IsNullOrWhiteSpace(rule.DedupKey) && type is not null)
            {
                try
                {
                    var key = Expression.Compile(rule.DedupKey, EntitySchema.ForSubscription(entity, type));
                    CheckReads(key.References, "key");

                    // Resolving events about no record find their alerts by the key, computed on the resolving event.
                    foreach (var resolver in rule.ResolveOn.Select(config.FindSubscription).OfType<SubscriptionSpec>())
                    {
                        if (config.FindEventType(resolver.Event) is { } resolveType && SubscriptionEntity(config, resolver) is null)
                        {
                            Expression.Compile(rule.DedupKey, EntitySchema.ForSubscription(null, resolveType));
                        }
                    }
                }
                catch (ExpressionException ex)
                {
                    d.Add(Diagnostic.Error("config.alert_bad_dedup", $"The key of alert rule '{rule.Label}' is invalid: {ex.Message}", rule.Id, ex.Suggestion ?? "Use an expression over event and record, e.g. record.id + ':' + event.target."));
                }
            }

            // Title: plain text with known placeholders, at most 200 characters.
            if (string.IsNullOrWhiteSpace(rule.Title) || rule.Title.Length > MaxAlertTitle)
            {
                d.Add(Diagnostic.Error("config.too_large", $"The title of alert rule '{rule.Label}' needs 1 to {MaxAlertTitle} characters.", rule.Id));
            }

            if (RawHtml().IsMatch(rule.Title))
            {
                d.Add(Diagnostic.Error("config.raw_html", $"The title of alert rule '{rule.Label}' contains HTML.", rule.Id, "Titles are plain text."));
            }

            var placeholders = AnyPlaceholder().Matches(rule.Title).Select(m => m.Groups[1].Value).ToList();
            foreach (var name in placeholders)
            {
                var known = name == "record.number"
                    || (name.StartsWith("record.", StringComparison.Ordinal) && entity?.FindField(name["record.".Length..]) is not null)
                    || (name.StartsWith("event.", StringComparison.Ordinal) && type?.Fields.ContainsKey(name["event.".Length..]) == true);
                if (!known)
                {
                    d.Add(Diagnostic.Error("config.bad_placeholder", $"The title of alert rule '{rule.Label}' uses unknown placeholder '{{{{{name}}}}}'.", rule.Id, "Use {{record.<field>}}, {{record.number}} or {{event.<field>}} of the trigger's event."));
                }
            }

            CheckReads(placeholders, "title");

            if (rule.Owner is { } owner)
            {
                var problem = owner.Kind switch
                {
                    AlertOwnerKinds.Group => GroupKey().IsMatch(owner.Ref) ? null : $"'{owner.Ref}' is not a group key",
                    AlertOwnerKinds.User => Guid.TryParse(owner.Ref, out _) ? null : $"'{owner.Ref}' is not a person's ID",
                    AlertOwnerKinds.Field => entity?.FindField(owner.Ref) is { Type: FieldTypes.User or FieldTypes.Group or FieldTypes.Principals } ? null : $"'{owner.Ref}' is not a user or group field of {entity?.Label ?? "the event's record"}",
                    _ => $"kind '{owner.Kind}' is unknown",
                };
                if (problem is not null)
                {
                    d.Add(Diagnostic.Error("config.alert_bad_owner", $"Alert rule '{rule.Label}': {problem}.", rule.Id, $"Owner kinds: {string.Join(", ", AlertOwnerKinds.All)}."));
                }
                else if (owner.Kind == AlertOwnerKinds.Field && hidden.Contains(owner.Ref))
                {
                    d.Add(Diagnostic.Error("config.alert_hidden_field", $"Alert rule '{rule.Label}' takes its owner from '{owner.Ref}', which some people may not see.", rule.Id, "Use a field everyone who reads the record can see."));
                }
            }
        }
    }

    private static void ValidateEscalation(AlertRuleSpec rule, List<Diagnostic> d)
    {
        if (rule.Escalation.Count > MaxEscalationSteps)
        {
            d.Add(Diagnostic.Error("config.alert_bad_escalation", $"Alert rule '{rule.Label}' has more than {MaxEscalationSteps} escalation steps.", rule.Id));
        }

        var previous = 0;
        for (var i = 0; i < rule.Escalation.Count; i++)
        {
            var step = rule.Escalation[i];
            if (step.AfterMinutes < 1 || step.AfterMinutes > MaxEscalationMinutes || step.AfterMinutes <= previous)
            {
                d.Add(Diagnostic.Error("config.alert_bad_escalation", $"Escalation step {i + 1} of alert rule '{rule.Label}' must come 1 to {MaxEscalationMinutes} minutes after the alert opened, later than the step before.", rule.Id));
            }

            if (step.RepeatEveryMinutes is { } repeat && (i != rule.Escalation.Count - 1 || repeat < 5 || repeat > MaxEscalationMinutes))
            {
                d.Add(Diagnostic.Error("config.alert_bad_escalation", $"Escalation step {i + 1} of alert rule '{rule.Label}' repeats; only the last step may, every 5 to {MaxEscalationMinutes} minutes.", rule.Id));
            }

            previous = step.AfterMinutes;
        }
    }
}
