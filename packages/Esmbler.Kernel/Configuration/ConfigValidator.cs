using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Expressions;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Registry;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>
/// Validates a composed configuration before it may become an artifact: schema types, referenced IDs,
/// expression types and cost, layout containment and props, workflow consistency, rule conflicts and cycles,
/// permission references. Every diagnostic says what is wrong, where, and a valid alternative.
/// </summary>
public sealed partial class ConfigValidator(IElementRegistry registry, IPropsValidator propsValidator)
{
    public IReadOnlyList<Diagnostic> Validate(SpecCatalog config)
    {
        var d = new List<Diagnostic>();
        ValidateUniqueness(config, d);
        foreach (var entity in config.Entities)
        {
            ValidateEntity(config, entity, d);
        }

        foreach (var list in config.ChoiceLists)
        {
            foreach (var dup in list.Options.GroupBy(o => o.Id).Where(g => g.Count() > 1))
            {
                d.Add(Diagnostic.Error("config.duplicate_option", $"Choice list '{list.Id}' has option '{dup.Key}' more than once.", list.Id));
            }

            if (list.Options.Count == 0)
            {
                d.Add(Diagnostic.Error("config.empty_choice_list", $"Choice list '{list.Id}' has no options.", list.Id, "Add at least one option."));
            }
        }

        foreach (var rel in config.Relationships)
        {
            var entity = config.FindEntity(rel.Entity);
            var field = entity?.FindField(rel.Field);
            if (entity is null || field is null || field.Type != FieldTypes.Lookup)
            {
                d.Add(Diagnostic.Error("config.bad_relationship", $"Relationship '{rel.Id}' must reference a lookup field on an existing entity ({rel.Entity}/{rel.Field}).", rel.Id));
            }
            else if (field.Target != rel.Target)
            {
                d.Add(Diagnostic.Error("config.bad_relationship", $"Relationship '{rel.Id}' targets {rel.Target} but field {rel.Field} looks up {field.Target}.", rel.Id));
            }
        }

        ValidateRules(config, d);
        foreach (var entity in config.Entities.Where(e => e.Request is not null))
        {
            ValidateRequestBinding(config, entity, entity.Request!, d);
        }

        foreach (var entity in config.Entities.Where(e => e.Request is null))
        {
            foreach (var f in entity.Fields.Where(f => f.Type is FieldTypes.CatalogItem or FieldTypes.Variables or FieldTypes.Principals))
            {
                d.Add(Diagnostic.Error("config.bad_field_type", $"Field '{f.Key}' of {entity.Label} has type {f.Type}, which only entities that receive requests can use.", $"{entity.Id}/{f.Key}", "Use another type, or add the field to a request entity."));
            }
        }

        foreach (var item in config.CatalogItems)
        {
            ValidateCatalogItem(config, item, d);
        }

        foreach (var policy in config.ApprovalPolicies)
        {
            ValidateApprovalPolicy(config, policy, d);
        }

        foreach (var eventType in config.EventTypes)
        {
            ValidateEventType(config, eventType, d);
        }

        foreach (var calendar in config.BusinessCalendars)
        {
            ValidateBusinessCalendar(calendar, d);
        }

        foreach (var sla in config.SlaPolicies)
        {
            ValidateSlaPolicy(config, sla, d);
        }

        foreach (var metric in config.Metrics)
        {
            ValidateMetric(config, metric, d);
        }

        foreach (var wf in config.Workflows)
        {
            ValidateWorkflow(config, wf, d);
        }

        foreach (var view in config.Views)
        {
            ValidateView(config, view, d);
        }

        ValidatePermissions(config, d);
        foreach (var ar in config.AssignmentRules)
        {
            var entity = config.FindEntity(ar.Entity);
            if (entity is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Assignment rule '{ar.Id}' references unknown entity '{ar.Entity}'.", ar.Id));
                continue;
            }

            CheckExpression(entity, ar.When, ar.Id, "condition", d);
        }

        ValidateNotifications(config, d);
        ValidateAlertRules(config, d);
        return d;
    }

    private static void ValidateUniqueness(SpecCatalog config, List<Diagnostic> d)
    {
        void Check(IEnumerable<string> ids, string kind)
        {
            foreach (var dup in ids.GroupBy(i => i).Where(g => g.Count() > 1))
            {
                d.Add(Diagnostic.Error("config.duplicate_id", $"Two {kind}s share the ID '{dup.Key}'.", dup.Key));
            }
        }

        Check(config.Entities.Select(e => e.Id), "entity");
        Check(config.BusinessCalendars.Select(c => c.Id), "business calendar");
        Check(config.SlaPolicies.Select(p => p.Id), "SLA policy");
        Check(config.Metrics.Select(m => m.Id), "metric");
        Check(config.NotificationTemplates.Select(t => t.Id), "notification template");
        Check(config.Subscriptions.Select(s => s.Id), "subscription");
        Check(config.NotificationProfiles.Select(p => p.Id), "notification profile");
        Check(config.NotificationRoutes.Select(r => r.Id), "notification route");
        Check(config.AlertRules.Select(r => r.Id), "alert rule");
        Check(config.ChoiceLists.Select(c => c.Id), "choice list");
        Check(config.Rules.Select(r => r.Id), "rule");
        Check(config.Views.Select(v => v.Id), "view");
        Check(config.Workflows.Select(w => w.Id), "workflow");
        Check(config.Roles.Select(r => r.Id), "role");
        Check(config.PermissionSets.Select(p => p.Id), "permission set");
        Check(config.Relationships.Select(r => r.Id), "relationship");
        Check(config.CatalogItems.Select(c => c.Id), "catalog item");
        Check(config.ApprovalPolicies.Select(p => p.Id), "approval policy");
        Check(config.EventTypes.Select(e => e.Id), "event type");
    }

    private void ValidateEntity(SpecCatalog config, EntitySpec entity, List<Diagnostic> d)
    {
        if (!Namespaces.IsValidId(entity.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{entity.Id}' is not a valid entity ID.", entity.Id));
        }

        foreach (var dup in entity.Fields.GroupBy(f => f.Key).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_field", $"{entity.Label} has two fields with key '{dup.Key}'.", $"{entity.Id}/{dup.Key}"));
        }

        if (entity.FindField(entity.TitleField) is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"{entity.Label}'s title field '{entity.TitleField}' does not exist.", entity.Id));
        }

        foreach (var field in entity.Fields)
        {
            var target = $"{entity.Id}/{field.Key}";
            if (!Namespaces.IsValidFieldKey(field.Key))
            {
                d.Add(Diagnostic.Error("config.invalid_key", $"'{field.Key}' is not a valid field key.", target, Namespaces.TenantKeyFromLabel(field.Label)));
            }

            if (registry.FieldType(field.Type) is null && !FieldTypes.All.Contains(field.Type))
            {
                var suggestion = Expression.Suggest(field.Type, FieldTypes.All);
                d.Add(Diagnostic.Error("config.unknown_field_type", $"Field '{field.Label}' has unknown type '{field.Type}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), target, suggestion));
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 80)
            {
                d.Add(Diagnostic.Error("config.bad_label", $"Field '{field.Key}' needs a label of 1–80 characters.", target));
            }

            if (FieldTypes.UsesChoiceList(field.Type))
            {
                if (field.ChoiceList is null)
                {
                    d.Add(Diagnostic.Error("config.missing_choice_list", $"{field.Type.Replace('_', ' ')} field '{field.Label}' needs a choice list.", target, "Create one with add_choice_list, or reference an existing list."));
                }
                else if (config.FindChoiceList(field.ChoiceList) is not { } list)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Field '{field.Label}' uses unknown choice list '{field.ChoiceList}'.", target, Expression.Suggest(field.ChoiceList, config.ChoiceLists.Select(c => c.Id))));
                }
                else if (field.Default is JsonValue dv && dv.TryGetValue<string>(out var defaultOption) && list.Find(defaultOption) is null)
                {
                    d.Add(Diagnostic.Error("config.bad_default", $"Default '{defaultOption}' of '{field.Label}' is not an option of {list.Label}.", target, Expression.Suggest(defaultOption, list.Options.Select(o => o.Id))));
                }
            }

            if (field.Type == FieldTypes.Lookup && (field.Target is null || config.FindEntity(field.Target) is null))
            {
                d.Add(Diagnostic.Error("config.bad_lookup", $"Lookup '{field.Label}' must target an existing entity.", target, string.Join(", ", config.Entities.Select(e => e.Id))));
            }

            if (field.Type == FieldTypes.AutoNumber && string.IsNullOrEmpty(field.Prefix))
            {
                d.Add(Diagnostic.Error("config.missing_prefix", $"Auto-number '{field.Label}' needs a prefix such as \"TCK\".", target));
            }

            if (field.MaxLength is { } ml && (ml < 1 || ml > MaxLengthFor(field.Type)))
            {
                d.Add(Diagnostic.Error("config.bad_constraint", $"maxLength for '{field.Label}' must be between 1 and {MaxLengthFor(field.Type)}.", target));
            }

            if (field.Min is { } min && field.Max is { } max && min > max)
            {
                d.Add(Diagnostic.Error("config.bad_constraint", $"'{field.Label}' has min {min} greater than max {max}.", target));
            }

            if (field.Pattern is not null)
            {
                try
                {
                    _ = new Regex(field.Pattern, RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException)
                {
                    d.Add(Diagnostic.Error("config.bad_pattern", $"'{field.Pattern}' on '{field.Label}' is not a valid pattern.", target));
                }
            }

            if (field.Required && field.Hidden && field.Default is null && !field.System)
            {
                d.Add(Diagnostic.Error(
                    "config.required_hidden",
                    $"'{field.Label}' is required but hidden, so nobody could fill it in.",
                    target,
                    "Give it a default value, make it optional, or show it."));
            }
        }

        foreach (var access in entity.AccessFields)
        {
            if (entity.FindField(access.Field) is not { Type: FieldTypes.User or FieldTypes.Group or FieldTypes.Principals })
            {
                d.Add(Diagnostic.Error("config.bad_access_field", $"Access field '{access.Field}' on {entity.Label} must be a user, group or principals field.", entity.Id));
            }
        }
    }

    private static int MaxLengthFor(string type) => type switch
    {
        FieldTypes.ShortText => 500,
        FieldTypes.Email => 254,
        FieldTypes.LongText => 10000,
        FieldTypes.RichText => 50000,
        _ => 500,
    };

    private static void ValidateRules(SpecCatalog config, List<Diagnostic> d)
    {
        var setters = new Dictionary<string, List<(string Rule, IReadOnlySet<string> Reads)>>(StringComparer.Ordinal);
        foreach (var rule in config.Rules)
        {
            var entity = config.FindEntity(rule.Entity);
            if (entity is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Rule '{rule.Id}' references unknown entity '{rule.Entity}'.", rule.Id));
                continue;
            }

            if (rule.Kind is not (RuleKinds.Validation or RuleKinds.Conditional))
            {
                d.Add(Diagnostic.Error("config.bad_rule_kind", $"Rule '{rule.Id}' kind must be validation or conditional.", rule.Id));
                continue;
            }

            if (rule.Scope is not (RuleScopes.Data or RuleScopes.Form))
            {
                d.Add(Diagnostic.Error("config.bad_rule_scope", $"Rule '{rule.Id}' scope must be data or form.", rule.Id));
            }

            if (rule.Scope == RuleScopes.Form && (rule.View is null || config.FindView(rule.View) is null))
            {
                d.Add(Diagnostic.Error("config.bad_rule_view", $"Form rule '{rule.Id}' must name an existing view.", rule.Id, string.Join(", ", config.Views.Where(v => v.Entity == rule.Entity).Select(v => v.Id))));
            }

            CheckExpression(entity, rule.When, rule.Id, "condition", d);
            if (rule.Kind == RuleKinds.Validation)
            {
                if (string.IsNullOrWhiteSpace(rule.Assert))
                {
                    d.Add(Diagnostic.Error("config.missing_assert", $"Validation rule '{rule.Id}' needs an assert expression.", rule.Id, "e.g. record.title.size() >= 5"));
                }
                else
                {
                    CheckExpression(entity, rule.Assert, rule.Id, "assert", d);
                }

                if (string.IsNullOrWhiteSpace(rule.Message))
                {
                    d.Add(Diagnostic.Error("config.missing_message", $"Validation rule '{rule.Id}' needs a message telling people how to fix the value.", rule.Id));
                }
            }
            else if (rule.Actions.Count == 0)
            {
                d.Add(Diagnostic.Error("config.no_actions", $"Conditional rule '{rule.Id}' has no actions.", rule.Id, "Add require, show, hide, read_only, set_value, restrict_options, or message."));
            }

            foreach (var action in rule.Actions)
            {
                if (!RuleActionTypes.All.Contains(action.Type))
                {
                    d.Add(Diagnostic.Error("config.bad_action", $"Rule '{rule.Id}' has unknown action '{action.Type}'.", rule.Id, Expression.Suggest(action.Type, RuleActionTypes.All)));
                    continue;
                }

                if (action.Type == RuleActionTypes.EmitEvent)
                {
                    if (rule.Scope != RuleScopes.Data)
                    {
                        d.Add(Diagnostic.Error("config.emit_needs_data_scope", $"Rule '{rule.Id}' publishes an event but is {rule.Scope}-scoped; only data rules run on the server.", rule.Id, "Make it a data rule."));
                    }

                    if (action.Emit is null)
                    {
                        d.Add(Diagnostic.Error("config.missing_event", $"emit_event in rule '{rule.Id}' needs an event to publish.", rule.Id));
                    }
                    else
                    {
                        ValidateEmit(config, entity, action.Emit, rule.Id, d);
                    }

                    continue;
                }

                if (action.Type == RuleActionTypes.Message)
                {
                    if (string.IsNullOrWhiteSpace(action.Message))
                    {
                        d.Add(Diagnostic.Error("config.missing_message", $"Message action in rule '{rule.Id}' needs text.", rule.Id));
                    }

                    continue;
                }

                var field = action.Field is null ? null : entity.FindField(action.Field);
                if (field is null)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Rule '{rule.Id}' action {action.Type} targets unknown field '{action.Field}'.", rule.Id, action.Field is null ? null : Expression.Suggest(action.Field, entity.Fields.Select(f => f.Key))));
                    continue;
                }

                if (field.System && action.Type is RuleActionTypes.SetValue or RuleActionTypes.Require)
                {
                    d.Add(Diagnostic.Error("config.system_field", $"Rule '{rule.Id}' cannot {action.Type} the system field '{field.Label}'.", rule.Id));
                }

                if (action.Type == RuleActionTypes.Require && rule.Scope == RuleScopes.Data && field.Hidden && field.Default is null)
                {
                    d.Add(Diagnostic.Error("config.required_hidden", $"Rule '{rule.Id}' requires '{field.Label}', which is hidden.", rule.Id, "Show the field or give it a default."));
                }

                if (action.Type == RuleActionTypes.RestrictOptions)
                {
                    var list = field.ChoiceList is null ? null : config.FindChoiceList(field.ChoiceList);
                    if (list is null)
                    {
                        d.Add(Diagnostic.Error("config.bad_action", $"restrict_options in '{rule.Id}' needs a choice field.", rule.Id));
                    }
                    else
                    {
                        foreach (var o in action.Options ?? [])
                        {
                            if (list.Find(o) is null)
                            {
                                d.Add(Diagnostic.Error("config.not_found", $"Rule '{rule.Id}' allows unknown option '{o}' of {list.Label}.", rule.Id, Expression.Suggest(o, list.Options.Select(x => x.Id))));
                            }
                        }
                    }
                }

                if (action.Type == RuleActionTypes.SetValue)
                {
                    if (string.IsNullOrWhiteSpace(action.Value))
                    {
                        d.Add(Diagnostic.Error("config.missing_value", $"set_value in '{rule.Id}' needs a value expression.", rule.Id, "Text values are quoted: 'high'."));
                        continue;
                    }

                    var compiled = CheckExpression(entity, action.Value, rule.Id, "value", d);
                    if (compiled is not null && rule.Scope == RuleScopes.Data && rule.Enabled)
                    {
                        var reads = new HashSet<string>(compiled.References.Concat(ExpressionRefs(rule.When)), StringComparer.Ordinal);
                        if (!setters.TryGetValue(field.Key, out var list))
                        {
                            setters[field.Key] = list = [];
                        }

                        list.Add((rule.Id, reads));
                    }
                }
            }
        }

        foreach (var (field, rules) in setters.Where(s => s.Value.Count > 1))
        {
            d.Add(Diagnostic.Warning("config.rule_overlap", $"Rules {string.Join(" and ", rules.Select(r => r.Rule))} all set '{field}'; the one with the highest order wins.", rules[0].Rule));
        }

        DetectSetValueCycles(setters, d);
    }

    private static IEnumerable<string> ExpressionRefs(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            return [];
        }

        try
        {
            return Expression.Compile(expr).References;
        }
        catch (ExpressionException)
        {
            return [];
        }
    }

    private static void DetectSetValueCycles(Dictionary<string, List<(string Rule, IReadOnlySet<string> Reads)>> setters, List<Diagnostic> d)
    {
        // field -> fields its setter rules read
        var graph = setters.ToDictionary(
            s => s.Key,
            s => s.Value.SelectMany(r => r.Reads).Where(x => x.StartsWith("record.", StringComparison.Ordinal)).Select(x => x[7..]).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var start in graph.Keys)
        {
            var path = new Stack<string>();
            if (Visit(start))
            {
                var cycle = string.Join(" → ", path.Reverse());
                d.Add(Diagnostic.Error("config.rule_cycle", $"Rules that set values form a cycle: {cycle}.", setters[start][0].Rule, "Break the cycle so no field's value depends on itself."));
                return;
            }

            bool Visit(string field)
            {
                if (state.GetValueOrDefault(field) == 1)
                {
                    path.Push(field);
                    return true;
                }

                if (state.GetValueOrDefault(field) == 2 || !graph.TryGetValue(field, out var deps))
                {
                    return false;
                }

                state[field] = 1;
                path.Push(field);
                foreach (var dep in deps)
                {
                    if (Visit(dep))
                    {
                        return true;
                    }
                }

                path.Pop();
                state[field] = 2;
                return false;
            }
        }
    }

    private static void ValidateWorkflow(SpecCatalog config, WorkflowSpec wf, List<Diagnostic> d)
    {
        var entity = config.FindEntity(wf.Entity);
        if (entity is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"Workflow '{wf.Id}' references unknown entity '{wf.Entity}'.", wf.Id));
            return;
        }

        var stateField = entity.FindField(wf.StateField);
        if (stateField is not { Type: FieldTypes.Status })
        {
            d.Add(Diagnostic.Error("config.bad_state_field", $"Workflow '{wf.Id}' state field '{wf.StateField}' must be a status field.", wf.Id));
            return;
        }

        var list = stateField.ChoiceList is null ? null : config.FindChoiceList(stateField.ChoiceList);
        var states = wf.States.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (list is not null)
        {
            foreach (var s in states.Where(s => list.Find(s) is null))
            {
                d.Add(Diagnostic.Error("config.state_not_option", $"Workflow state '{s}' is not an option of {list.Label}.", wf.Id));
            }
        }

        if (!states.Contains(wf.InitialState))
        {
            d.Add(Diagnostic.Error("config.not_found", $"Workflow '{wf.Id}' initial state '{wf.InitialState}' is not one of its states.", wf.Id));
        }

        foreach (var t in wf.Transitions)
        {
            foreach (var s in t.From.Append(t.To).Where(s => s != "*" && !states.Contains(s)))
            {
                d.Add(Diagnostic.Error("config.not_found", $"Transition '{t.Id}' uses unknown state '{s}'.", wf.Id, Expression.Suggest(s, states)));
            }

            CheckExpression(entity, t.Guard, $"{wf.Id}/{t.Id}", "guard", d);
            foreach (var emit in t.Emits)
            {
                ValidateEmit(config, entity, emit, $"{wf.Id}/{t.Id}", d);
            }

            foreach (var f in t.RequiredFields.Where(f => entity.FindField(f) is null))
            {
                d.Add(Diagnostic.Error("config.not_found", $"Transition '{t.Id}' requires unknown field '{f}'.", wf.Id));
            }

            foreach (var (f, expr) in t.Set)
            {
                if (entity.FindField(f) is null)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Transition '{t.Id}' sets unknown field '{f}'.", wf.Id));
                }

                CheckExpression(entity, expr, $"{wf.Id}/{t.Id}", "value", d);
            }

            foreach (var role in t.Roles.Where(r => !config.Roles.Exists(x => x.Id == r)))
            {
                d.Add(Diagnostic.Error("config.not_found", $"Transition '{t.Id}' references unknown role '{role}'.", wf.Id));
            }
        }

        foreach (var dup in wf.Transitions.GroupBy(t => t.Id).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_id", $"Workflow '{wf.Id}' has two transitions '{dup.Key}'.", wf.Id));
        }

        foreach (var s in wf.States.Where(s => !StateCategoryNames.Contains(s.Category)))
        {
            d.Add(Diagnostic.Error("config.bad_state_category", $"State '{s.Id}' of '{wf.Id}' has unknown category '{s.Category}'.", wf.Id, string.Join(", ", StateCategoryNames)));
        }

        var policy = wf.ActiveInstances;
        if (!ActiveInstancePolicies.All.Contains(policy.Policy))
        {
            d.Add(Diagnostic.Error("config.bad_policy", $"Workflow '{wf.Id}' active-instance policy '{policy.Policy}' is unknown.", wf.Id, "Use pin, migrate, or cancel."));
        }

        foreach (var (from, to) in policy.StateMap.Where(m => !states.Contains(m.Value)))
        {
            d.Add(Diagnostic.Error("config.not_found", $"Workflow '{wf.Id}' maps '{from}' to unknown state '{to}'.", wf.Id, Expression.Suggest(to, states)));
        }

        if (policy.Policy == ActiveInstancePolicies.Cancel && wf.FindState(policy.CancelState ?? string.Empty) is not { Category: StateCategories.Cancelled })
        {
            d.Add(Diagnostic.Error("config.bad_policy", $"Workflow '{wf.Id}' cancels running instances but cancelState '{policy.CancelState}' is not a cancelled-category state.", wf.Id, string.Join(", ", wf.States.Where(s => s.Category == StateCategories.Cancelled).Select(s => s.Id))));
        }
    }

    private static readonly string[] StateCategoryNames =
        [StateCategories.New, StateCategories.Active, StateCategories.Pending, StateCategories.Resolved, StateCategories.Closed, StateCategories.Cancelled];

    private static void ValidateRequestBinding(SpecCatalog config, EntitySpec entity, RequestBindingSpec binding, List<Diagnostic> d)
    {
        void Need(EntitySpec e, string? key, Func<string, bool> typeOk, string expected)
        {
            if (key is null)
            {
                return;
            }

            if (e.FindField(key) is not { } f || !typeOk(f.Type))
            {
                d.Add(Diagnostic.Error("config.bad_request_binding", $"The request binding of {entity.Label} needs {e.Label} field '{key}' of type {expected}.", entity.Id));
            }
        }

        Need(entity, binding.CatalogItemField, t => t == FieldTypes.CatalogItem, FieldTypes.CatalogItem);
        Need(entity, binding.VariablesField, t => t == FieldTypes.Variables, FieldTypes.Variables);
        Need(entity, binding.OutcomeField, t => t == FieldTypes.SingleSelect, FieldTypes.SingleSelect);
        foreach (var key in binding.LockedFields.Where(k => entity.FindField(k) is null))
        {
            d.Add(Diagnostic.Error("config.not_found", $"Locked field '{key}' is not a field of {entity.Label}.", entity.Id, Expression.Suggest(key, entity.Fields.Select(f => f.Key))));
        }

        var task = config.FindEntity(binding.TaskEntity);
        if (task is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"The request binding of {entity.Label} names unknown task entity '{binding.TaskEntity}'.", entity.Id));
            return;
        }

        if (task.FindField(binding.TaskRequestField) is not { Type: FieldTypes.Lookup } link || link.Target != entity.Id)
        {
            d.Add(Diagnostic.Error("config.bad_request_binding", $"{task.Label} field '{binding.TaskRequestField}' must be a lookup to {entity.Label}.", entity.Id));
        }

        Need(task, binding.TaskKeyField, FieldTypes.IsText, "short_text");
        Need(task, binding.TaskTitleField, FieldTypes.IsText, "short_text");
        Need(task, binding.TaskDescriptionField, FieldTypes.IsText, "text");
        Need(task, binding.TaskGroupField, t => t == FieldTypes.Group, FieldTypes.Group);

        var requestFlow = config.WorkflowFor(entity.Id);
        var taskFlow = config.WorkflowFor(task.Id);
        if (requestFlow is null || taskFlow is null)
        {
            d.Add(Diagnostic.Error("config.bad_request_binding", $"{entity.Label} and {task.Label} both need a workflow for fulfillment.", entity.Id));
            return;
        }

        foreach (var state in binding.StartStates.Where(s => requestFlow.FindState(s) is null))
        {
            d.Add(Diagnostic.Error("config.not_found", $"Fulfillment start state '{state}' is not a state of {requestFlow.Id}.", entity.Id));
        }

        if (binding.StartStates.Count == 0)
        {
            d.Add(Diagnostic.Error("config.bad_request_binding", $"The request binding of {entity.Label} needs at least one start state.", entity.Id));
        }

        void NeedTransition(WorkflowSpec wf, string id, string role)
        {
            if (wf.FindTransition(id) is not { } t)
            {
                d.Add(Diagnostic.Error("config.not_found", $"The {role} transition '{id}' is not in {wf.Id}.", entity.Id));
            }
            else if (!t.System)
            {
                d.Add(Diagnostic.Warning("config.not_system", $"The {role} transition '{id}' should be a system transition so people cannot fire it by hand.", entity.Id));
            }
        }

        NeedTransition(requestFlow, binding.CompleteTransition, "completion");
        if (binding.Approval is { } approval)
        {
            ValidateApprovalBinding(config, entity, approval, requestFlow, d);
        }

        NeedTransition(taskFlow, binding.TaskReleaseTransition, "task release");
        if (taskFlow.FindTransition(binding.TaskReleaseTransition) is { } release && release.To == taskFlow.InitialState)
        {
            d.Add(Diagnostic.Error("config.bad_request_binding", $"The task release transition '{release.Id}' must leave the initial state '{taskFlow.InitialState}'.", entity.Id));
        }

        // A migration that moves running requests out of fulfillment would strand their tasks (ADR 0015).
        var policy = requestFlow.ActiveInstances;
        if (policy.Policy == ActiveInstancePolicies.Migrate)
        {
            foreach (var (from, to) in policy.StateMap.Where(m => binding.StartStates.Contains(m.Key) && !binding.StartStates.Contains(m.Value) && requestFlow.FindState(m.Value)?.Category is not StateCategories.Cancelled))
            {
                d.Add(Diagnostic.Error("config.strands_fulfillment", $"Migrating {entity.PluralLabel.ToLowerInvariant()} from '{from}' to '{to}' would leave their fulfillment tasks without a request in fulfillment.", requestFlow.Id, $"Map '{from}' to one of: {string.Join(", ", binding.StartStates)}, or to a cancelled state."));
            }
        }
        if (taskFlow.FindTransition(binding.TaskCancelTransition) is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"The task cancel transition '{binding.TaskCancelTransition}' is not in {taskFlow.Id}.", entity.Id));
        }
    }

    /// <summary>Per catalog item: keeps forms usable and bounds fulfillment fan-out (each reconcile reads every task).</summary>
    private const int MaxVariables = 50;

    private const int MaxTasks = 50;

    private static void ValidateApprovalBinding(SpecCatalog config, EntitySpec entity, ApprovalBindingSpec approval, WorkflowSpec flow, List<Diagnostic> d)
    {
        if (flow.FindState(approval.State) is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"Approval state '{approval.State}' is not a state of {flow.Id}.", entity.Id));
        }

        void Need(string id, string role, Func<WorkflowTransition, bool> shape, string expected)
        {
            if (flow.FindTransition(id) is not { } t)
            {
                d.Add(Diagnostic.Error("config.not_found", $"The {role} transition '{id}' is not in {flow.Id}.", entity.Id));
            }
            else if (!t.System)
            {
                d.Add(Diagnostic.Error("config.not_system", $"The {role} transition '{id}' must be a system transition so only the approval engine fires it.", entity.Id));
            }
            else if (!shape(t))
            {
                d.Add(Diagnostic.Error("config.bad_approval_binding", $"The {role} transition '{id}' must {expected}.", entity.Id));
            }
        }

        Need(approval.RequestTransition, "approval request", t => t.From.Contains(flow.InitialState) && t.To == approval.State, $"go from '{flow.InitialState}' to '{approval.State}'");
        Need(approval.ApprovedTransition, "approved", t => t.From.Contains(approval.State) && t.To != approval.State, $"leave '{approval.State}'");
        Need(approval.DeclinedTransition, "declined", t => t.From.Contains(approval.State) && flow.FindState(t.To)?.Category == StateCategories.Cancelled, $"go from '{approval.State}' to a cancelled-category state");

        if (entity.FindField(approval.ApproversField) is not { Type: FieldTypes.Principals } field)
        {
            d.Add(Diagnostic.Error("config.bad_approval_binding", $"{entity.Label} needs a '{approval.ApproversField}' field of type principals for its approvers.", entity.Id));
        }
        else if (!field.ReadOnly)
        {
            d.Add(Diagnostic.Error("config.bad_approval_binding", $"The approvers field '{field.Key}' must be read-only; only the approval engine writes it.", $"{entity.Id}/{field.Key}"));
        }

        if (!entity.AccessFields.Exists(a => a.Field == approval.ApproversField && !a.CanEdit))
        {
            d.Add(Diagnostic.Warning("config.approvers_not_related", $"Approvers of {entity.PluralLabel.ToLowerInvariant()} cannot open them: '{approval.ApproversField}' is not a read-only access field.", entity.Id, $"Add an access field {{ field: {approval.ApproversField}, relation: approver, canEdit: false }}."));
        }

        // A migration must keep requests in the approval state there, or cancel them (pending approvals are preserved or explicitly cancelled).
        if (flow.ActiveInstances.Policy == ActiveInstancePolicies.Migrate
            && flow.ActiveInstances.StateMap.TryGetValue(approval.State, out var target)
            && target != approval.State
            && flow.FindState(target)?.Category != StateCategories.Cancelled)
        {
            d.Add(Diagnostic.Error("config.strands_approvals", $"Migrating {entity.PluralLabel.ToLowerInvariant()} from '{approval.State}' to '{target}' would abandon their pending approvals.", flow.Id, $"Keep '{approval.State}' or map it to a cancelled state."));
        }

        // Nothing migrates into the approval state (it would wait for an approval that never started), and nothing
        // migrates out of intake except to a cancelled state (it would skip approval).
        if (flow.ActiveInstances.Policy == ActiveInstancePolicies.Migrate)
        {
            foreach (var (from, to) in flow.ActiveInstances.StateMap.Where(m => m.Key != approval.State && m.Value == approval.State))
            {
                d.Add(Diagnostic.Error("config.strands_approvals", $"Migrating {entity.PluralLabel.ToLowerInvariant()} from '{from}' into '{to}' would leave them waiting for an approval that never started.", flow.Id, $"Map '{from}' to another state."));
            }

            foreach (var (from, to) in flow.ActiveInstances.StateMap.Where(m => m.Key == flow.InitialState && m.Value != flow.InitialState && flow.FindState(m.Value)?.Category != StateCategories.Cancelled))
            {
                d.Add(Diagnostic.Error("config.skips_approval", $"Migrating {entity.PluralLabel.ToLowerInvariant()} from '{from}' to '{to}' would skip their approval.", flow.Id, $"Keep '{from}' or map it to a cancelled state."));
            }
        }

        foreach (var key in approval.RequesterFields.Where(k => entity.FindField(k)?.Type != FieldTypes.User))
        {
            d.Add(Diagnostic.Error("config.bad_approval_binding", $"Requester field '{key}' of {entity.Label} must be a user field.", entity.Id));
        }

        // The approvers list is written by the approval engine alone: no rule, transition or default may set it.
        foreach (var rule in config.Rules.Where(r => r.Entity == entity.Id))
        {
            if (rule.Actions.Exists(a => a.Field == approval.ApproversField && a.Type == RuleActionTypes.SetValue))
            {
                d.Add(Diagnostic.Error("config.platform_field", $"Rule '{rule.Id}' sets '{approval.ApproversField}', which only the approval engine writes.", rule.Id));
            }
        }

        if (flow.Transitions.Exists(t => t.Set.ContainsKey(approval.ApproversField)))
        {
            d.Add(Diagnostic.Error("config.platform_field", $"A transition of {flow.Id} sets '{approval.ApproversField}', which only the approval engine writes.", flow.Id));
        }

        if (entity.FindField(approval.ApproversField)?.Default is not null)
        {
            d.Add(Diagnostic.Error("config.platform_field", $"'{approval.ApproversField}' cannot have a default; only the approval engine writes it.", $"{entity.Id}/{approval.ApproversField}"));
        }

    }

    /// <summary>Event type ID prefixes only the platform declares and publishes (ADR 0017).</summary>
    private static readonly string[] PlatformEventPrefixes = ["platform.", "record.", "config.", "activity.", "draft."];

    private static bool IsPlatformEvent(SpecCatalog config, string typeId) =>
        PlatformEventPrefixes.Any(p => typeId.StartsWith(p, StringComparison.Ordinal)) || LayerModule(config, typeId) == PlatformModule.ModuleId;

    /// <summary>The module that contributed an element ("core.tickets"), "tenant", or null without provenance.</summary>
    private static string? LayerModule(SpecCatalog config, string key) =>
        config is EffectiveConfig effective && effective.Provenance.TryGetValue(key, out var layer)
            ? layer.Contains('@', StringComparison.Ordinal) ? layer[..layer.IndexOf('@', StringComparison.Ordinal)] : layer
            : null;

    /// <summary>
    /// Module notification elements live in the module's namespace, and a module's routes use only its own subscriptions,
    /// templates and profiles or the platform's (ADR 0012 data boundaries, ADR 0021).
    /// </summary>
    private static void ValidateNotificationOwnership(SpecCatalog config, List<Diagnostic> d)
    {
        bool Foreign(string id, out string module) =>
            (module = LayerModule(config, id)!) is not null && module != Composer.TenantLayer && module != PlatformModule.ModuleId && !id.StartsWith(module + ".", StringComparison.Ordinal);

        foreach (var id in config.Subscriptions.Select(x => x.Id).Concat(config.NotificationTemplates.Select(x => x.Id)).Concat(config.NotificationProfiles.Select(x => x.Id)).Concat(config.NotificationRoutes.Select(x => x.Id)).Concat(config.AlertRules.Select(x => x.Id)))
        {
            if (Foreign(id, out var module))
            {
                d.Add(Diagnostic.Error("config.foreign_notification_element", $"'{id}' is declared by {module} outside its namespace.", id, $"Name it {module}.<name>."));
            }
        }

        foreach (var (elementId, uses) in config.NotificationRoutes.Select(r => (r.Id, new[] { r.Subscription, r.Template, r.Profile }))
                     .Concat(config.AlertRules.Select(r => (r.Id, new[] { r.Trigger }.Concat(r.ResolveOn).ToArray()))))
        {
            if (LayerModule(config, elementId) is not { } owner || owner == Composer.TenantLayer || owner == PlatformModule.ModuleId)
            {
                continue;
            }

            foreach (var used in uses)
            {
                if (LayerModule(config, used) is { } usedOwner && usedOwner != owner && usedOwner != PlatformModule.ModuleId && usedOwner != Composer.TenantLayer)
                {
                    d.Add(Diagnostic.Error("config.foreign_notification_element", $"'{elementId}' of {owner} uses '{used}', which belongs to {usedOwner}.", elementId, $"Use {owner}'s own or the platform's subscriptions, templates and profiles."));
                }
            }
        }
    }

    private static void ValidateEventType(SpecCatalog config, EventTypeSpec type, List<Diagnostic> d)
    {
        var module = LayerModule(config, type.Id);
        if (PlatformEventPrefixes.Any(p => type.Id.StartsWith(p, StringComparison.Ordinal)))
        {
            if (module is not null && module != PlatformModule.ModuleId)
            {
                d.Add(Diagnostic.Error("config.reserved_event_type", $"'{type.Id}' uses a namespace only the platform publishes in.", type.Id, "Name it after your module or tenant, e.g. tenant__vendor_contract_expiring."));
            }
        }
        else if (!Namespaces.IsValidId(type.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{type.Id}' is not a valid event type ID.", type.Id, "Use <module>.<entity>.<what_happened>, e.g. core.tickets.ticket.escalated."));
        }
        else if (module is not null && module != Composer.TenantLayer && module != PlatformModule.ModuleId && !type.Id.StartsWith(module + ".", StringComparison.Ordinal))
        {
            // A module declares events only in its own namespace, so nobody can publish in another module's name.
            d.Add(Diagnostic.Error("config.foreign_event_type", $"Event type '{type.Id}' is declared by {module} outside its namespace.", type.Id, $"Use {module}.<entity>.<what_happened>."));
        }

        if (type.Version < 1)
        {
            d.Add(Diagnostic.Error("config.bad_version", $"Event type '{type.Id}' needs a version of 1 or more.", type.Id));
        }

        if (!EventSeverities.All.Contains(type.Severity))
        {
            d.Add(Diagnostic.Error("config.bad_severity", $"Event type '{type.Id}' has severity '{type.Severity}'.", type.Id, string.Join(", ", EventSeverities.All)));
        }

        if (!EventSensitivities.All.Contains(type.Sensitivity))
        {
            d.Add(Diagnostic.Error("config.bad_sensitivity", $"Event type '{type.Id}' has sensitivity '{type.Sensitivity}'.", type.Id, string.Join(", ", EventSensitivities.All)));
        }

        if (type.SubjectEntity is { } subject && config.FindEntity(subject) is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"Event type '{type.Id}' is about unknown entity '{subject}'.", type.Id, Expression.Suggest(subject, config.Entities.Select(e => e.Id))));
        }

        foreach (var (key, field) in type.Fields)
        {
            if (!Namespaces.IsValidFieldKey(key))
            {
                d.Add(Diagnostic.Error("config.invalid_key", $"Payload field '{key}' of '{type.Id}' must be snake_case.", type.Id));
            }

            if (!EventFieldTypes.All.Contains(field.Type))
            {
                d.Add(Diagnostic.Error("config.bad_field_type", $"Payload field '{key}' of '{type.Id}' has type '{field.Type}'.", type.Id, string.Join(", ", EventFieldTypes.All)));
            }
        }

        if (type.Fields.Count > 30)
        {
            d.Add(Diagnostic.Error("config.too_large", $"Event type '{type.Id}' has {type.Fields.Count} payload fields; keep payloads small (30 at most) and let subscribers read the record.", type.Id));
        }
    }

    /// <summary>An emission names a known event type, fills its required fields, and uses only its fields, with valid expressions.</summary>
    private static void ValidateEmit(SpecCatalog config, EntitySpec entity, EventEmitSpec emit, string target, List<Diagnostic> d)
    {
        if (config.FindEventType(emit.Type) is not { } type)
        {
            d.Add(Diagnostic.Error("config.not_found", $"'{target}' publishes unknown event type '{emit.Type}'.", target, Expression.Suggest(emit.Type, config.EventTypes.Select(e => e.Id)) ?? "Add it with add_event_type."));
            return;
        }

        if (IsPlatformEvent(config, type.Id))
        {
            d.Add(Diagnostic.Error("config.platform_event", $"'{target}' publishes '{type.Id}', which only the platform publishes.", target, "Declare your own event type with add_event_type."));
            return;
        }

        // A module publishes only its own event types; tenant types and tenant emitters may be combined with anything
        // non-platform (the event's source is then "tenant").
        if (LayerModule(config, target) is { } emitterModule && emitterModule != Composer.TenantLayer && LayerModule(config, type.Id) is { } owner && owner != Composer.TenantLayer && owner != emitterModule)
        {
            d.Add(Diagnostic.Error("config.foreign_event", $"'{target}' from {emitterModule} publishes '{type.Id}', which belongs to {owner}.", target, $"Declare the event in {emitterModule}."));
        }

        if (type.SubjectEntity is { } subject && subject != entity.Id)
        {
            d.Add(Diagnostic.Error("config.bad_event_subject", $"'{target}' publishes '{type.Id}', which is about {subject}, not {entity.Id}.", target));
        }

        // Fields some people may not see stay out of payloads unless the event type is restricted (ADR 0017).
        var hidden = config.PermissionSets.SelectMany(p => p.Fields).Where(f => f.Entity == entity.Id && f.Access == FieldAccessLevels.Hidden).Select(f => f.Field).ToHashSet(StringComparer.Ordinal);

        foreach (var key in emit.Payload.Keys.Where(k => !type.Fields.ContainsKey(k)))
        {
            d.Add(Diagnostic.Error("config.unknown_payload_field", $"'{target}' sets payload field '{key}', which '{type.Id}' does not declare.", target, Expression.Suggest(key, type.Fields.Keys)));
        }

        foreach (var key in type.Fields.Where(f => f.Value.Required && !emit.Payload.ContainsKey(f.Key)).Select(f => f.Key))
        {
            d.Add(Diagnostic.Error("config.missing_payload_field", $"'{target}' publishes '{type.Id}' without its required field '{key}'.", target));
        }

        foreach (var (key, expression) in emit.Payload)
        {
            var compiled = CheckExpression(entity, expression, target, $"payload {key}", d);
            if (compiled is null)
            {
                continue;
            }

            var reads = compiled.References.Where(r => r.StartsWith("record.", StringComparison.Ordinal) || r.StartsWith("prior.", StringComparison.Ordinal)).Select(r => r[(r.IndexOf('.', StringComparison.Ordinal) + 1)..]);
            if (type.Sensitivity != EventSensitivities.Restricted && reads.FirstOrDefault(hidden.Contains) is { } secret)
            {
                d.Add(Diagnostic.Error("config.payload_hidden_field", $"'{target}' puts '{secret}' in '{type.Id}', but some people may not see that field.", target, "Leave it out (subscribers can read the record), or mark the event type restricted."));
            }

            // A payload value that is a plain field must have the declared type.
            var direct = System.Text.RegularExpressions.Regex.Match(expression.Trim(), "^(?:record|prior)\\.([a-z0-9_]+)$");
            if (direct.Success && type.Fields.TryGetValue(key, out var declared) && entity.Fields.Find(f => f.Key == direct.Groups[1].Value) is { } field && PayloadType(field.Type) != declared.Type)
            {
                d.Add(Diagnostic.Error("config.payload_type_mismatch", $"'{target}' puts {field.Type} field '{field.Key}' in payload field '{key}' of '{type.Id}', which is a {declared.Type}.", target, $"Use a {PayloadType(field.Type)} field, or change the payload field's type."));
            }
        }

        CheckExpression(entity, emit.Severity, target, "severity", d);
    }

    private static void ValidateBusinessCalendar(BusinessCalendarSpec calendar, List<Diagnostic> d)
    {
        if (!Namespaces.IsValidId(calendar.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{calendar.Id}' is not a valid business calendar ID.", calendar.Id));
        }

        if (string.IsNullOrWhiteSpace(calendar.Label) || calendar.Label.Length > 80)
        {
            d.Add(Diagnostic.Error("config.bad_label", $"Business calendar '{calendar.Id}' needs a label of 1–80 characters.", calendar.Id));
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(calendar.TimeZone, out _) || !calendar.TimeZone.Contains('/', StringComparison.Ordinal) && calendar.TimeZone != "UTC")
        {
            d.Add(Diagnostic.Error("config.bad_time_zone", $"Business calendar '{calendar.Label}' has time zone '{calendar.TimeZone}', which is not an IANA time zone.", calendar.Id, "Use a name such as Europe/London, America/New_York or Asia/Tokyo."));
        }

        var intervals = new List<(DayOfWeek Day, TimeSpan Start, TimeSpan End)>();
        foreach (var hours in calendar.Week)
        {
            if (!Weekdays.All.Contains(hours.Day))
            {
                d.Add(Diagnostic.Error("config.bad_day", $"Business calendar '{calendar.Label}' has unknown weekday '{hours.Day}'.", calendar.Id, string.Join(", ", Weekdays.All)));
                continue;
            }

            try
            {
                var start = Sla.BusinessTime.ParseTime(hours.Start);
                var end = Sla.BusinessTime.ParseTime(hours.End);
                if (end <= start)
                {
                    d.Add(Diagnostic.Error("config.bad_hours", $"Business calendar '{calendar.Label}': on {hours.Day}, {hours.End} is not after {hours.Start}.", calendar.Id, "Split hours that cross midnight into two intervals."));
                    continue;
                }

                if (end - start < TimeSpan.FromMinutes(15))
                {
                    d.Add(Diagnostic.Error("config.bad_hours", $"Business calendar '{calendar.Label}': on {hours.Day}, {hours.Start}–{hours.End} is shorter than 15 minutes.", calendar.Id));
                    continue;
                }

                intervals.Add((Weekdays.ToDayOfWeek(hours.Day), start, end));
            }
            catch (FormatException ex)
            {
                d.Add(Diagnostic.Error("config.bad_hours", $"Business calendar '{calendar.Label}': {ex.Message}", calendar.Id, "Use HH:mm, e.g. 08:30."));
            }
        }

        foreach (var day in intervals.GroupBy(i => i.Day))
        {
            if (day.Count() > 4)
            {
                d.Add(Diagnostic.Error("config.bad_hours", $"Business calendar '{calendar.Label}' has {day.Count()} intervals on {day.Key}; use at most 4 per day.", calendar.Id));
            }

            var sorted = day.OrderBy(i => i.Start).ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Start < sorted[i - 1].End)
                {
                    d.Add(Diagnostic.Error("config.bad_hours", $"Business calendar '{calendar.Label}' has overlapping hours on {day.Key}.", calendar.Id));
                }
            }
        }

        if (calendar.Week.Count == 0)
        {
            d.Add(Diagnostic.Error("config.bad_hours", $"Business calendar '{calendar.Label}' has no working hours.", calendar.Id, "Add at least one interval, e.g. mon 09:00–17:00."));
        }

        foreach (var holiday in calendar.Holidays.Where(h => !DateOnly.TryParseExact(h.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)))
        {
            d.Add(Diagnostic.Error("config.bad_date", $"Business calendar '{calendar.Label}' has holiday '{holiday.Date}', which is not a date.", calendar.Id, "Use YYYY-MM-DD."));
        }

        if (calendar.Holidays.Any(h => h.Label is { Length: > 80 }))
        {
            d.Add(Diagnostic.Error("config.bad_label", $"Business calendar '{calendar.Label}' has a holiday label longer than 80 characters.", calendar.Id));
        }

        if (calendar.Holidays.Count > 400)
        {
            d.Add(Diagnostic.Error("config.too_large", $"Business calendar '{calendar.Label}' has {calendar.Holidays.Count} holidays; keep at most 400 (a few years).", calendar.Id));
        }
    }

    private static void ValidateSlaPolicy(SpecCatalog config, SlaPolicySpec policy, List<Diagnostic> d)
    {
        if (!Namespaces.IsValidId(policy.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{policy.Id}' is not a valid SLA policy ID.", policy.Id));
        }

        if (string.IsNullOrWhiteSpace(policy.Label) || policy.Label.Length > 80)
        {
            d.Add(Diagnostic.Error("config.bad_label", $"SLA policy '{policy.Id}' needs a label of 1–80 characters.", policy.Id));
        }

        if (config.FindEntity(policy.Entity) is not { } entity)
        {
            d.Add(Diagnostic.Error("config.not_found", $"SLA policy '{policy.Label}' applies to unknown entity '{policy.Entity}'.", policy.Id, Expression.Suggest(policy.Entity, config.Entities.Select(e => e.Id))));
            return;
        }

        CheckExpression(entity, policy.When, policy.Id, "condition", d);
        if (config.SlaPolicies.Count(p => p.Entity == policy.Entity) > 10 && config.SlaPolicies.First(p => p.Entity == policy.Entity) == policy)
        {
            d.Add(Diagnostic.Error("config.too_large", $"{entity.Label} has more than 10 SLA policies; combine them with conditions on goals.", policy.Id));
        }

        // Conditions decide which goal applies and whether a clock runs, which every reader of the record sees: they
        // must not read fields some people may not see (ADR 0018).
        var hidden = config.PermissionSets.SelectMany(p => p.Fields).Where(f => f.Entity == entity.Id && f.Access == FieldAccessLevels.Hidden).Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        void CheckHidden(string? condition, string where)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return;
            }

            try
            {
                var secret = Expression.Compile(condition).References.Select(r => r[(r.IndexOf('.', StringComparison.Ordinal) + 1)..]).FirstOrDefault(hidden.Contains);
                if (secret is not null)
                {
                    d.Add(Diagnostic.Error("config.sla_hidden_field", $"'{where}' reads '{secret}', which some people may not see; anyone who reads the record sees which goal applies.", where, "Use a field everyone who reads the record can see."));
                }
            }
            catch (ExpressionException)
            {
                // Reported by CheckExpression.
            }
        }

        CheckHidden(policy.When, policy.Id);
        foreach (var target in policy.Targets)
        {
            foreach (var condition in new[] { target.Start, target.Stop, target.Pause }.Concat(target.Goals.Select(g => g.When)))
            {
                CheckHidden(condition, $"{policy.Id}/{target.Id}");
            }
        }

        if (policy.Targets.Count is 0 or > 5)
        {
            d.Add(Diagnostic.Error("config.bad_targets", $"SLA policy '{policy.Label}' needs 1–5 targets, e.g. response and resolution.", policy.Id));
        }

        foreach (var dup in policy.Targets.GroupBy(t => t.Id).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_id", $"SLA policy '{policy.Label}' has two targets '{dup.Key}'.", policy.Id));
        }

        foreach (var target in policy.Targets)
        {
            var where = $"{policy.Id}/{target.Id}";
            if (!Namespaces.IsValidFieldKey(target.Id))
            {
                d.Add(Diagnostic.Error("config.invalid_key", $"SLA target '{target.Id}' of '{policy.Label}' must be snake_case.", where));
            }

            CheckExpression(entity, target.Start, where, "start condition", d);
            CheckExpression(entity, target.Pause, where, "pause condition", d);
            if (string.IsNullOrWhiteSpace(target.Stop))
            {
                d.Add(Diagnostic.Error("config.missing_stop", $"SLA target '{target.Label}' of '{policy.Label}' needs a stop condition.", where, "e.g. record.status in ['resolved', 'closed']"));
            }
            else
            {
                CheckExpression(entity, target.Stop, where, "stop condition", d);
            }

            if (!SlaReopen.All.Contains(target.OnReopen))
            {
                d.Add(Diagnostic.Error("config.bad_reopen", $"SLA target '{target.Label}' has onReopen '{target.OnReopen}'.", where, string.Join(", ", SlaReopen.All)));
            }

            if (target.WarnAtPercent is { } warn && warn is < 1 or > 99)
            {
                d.Add(Diagnostic.Error("config.bad_warning", $"SLA target '{target.Label}' warns at {warn}%; use 1–99.", where));
            }

            CheckCalendar(config, target.Calendar, where, d);
            if (target.Goals.Count == 0)
            {
                d.Add(Diagnostic.Error("config.missing_goal", $"SLA target '{target.Label}' of '{policy.Label}' has no goal.", where, "Add a goal, e.g. 480 minutes, or one per priority."));
            }

            foreach (var goal in target.Goals)
            {
                CheckExpression(entity, goal.When, where, "goal condition", d);
                CheckCalendar(config, goal.Calendar, where, d);
                if (goal.Minutes is < 1 or > 525_600)
                {
                    d.Add(Diagnostic.Error("config.bad_goal", $"SLA target '{target.Label}' has a goal of {goal.Minutes} minutes; use 1 minute to 1 year.", where));
                }

                // A goal must fit within a year of the calendar's working time, or the clock could never come due.
                if ((goal.Calendar ?? target.Calendar) is { } calendarId && calendarId != SlaCalendars.AroundTheClock && config.FindBusinessCalendar(calendarId) is { } cal)
                {
                    var weekly = cal.Week.Sum(w =>
                    {
                        try
                        {
                            return Math.Max(0, (Sla.BusinessTime.ParseTime(w.End) - Sla.BusinessTime.ParseTime(w.Start)).TotalMinutes);
                        }
                        catch (FormatException)
                        {
                            return 0;
                        }
                    });
                    if (goal.Minutes > weekly * 52)
                    {
                        d.Add(Diagnostic.Error("config.goal_unreachable", $"SLA target '{target.Label}' has a goal of {goal.Minutes} business minutes, more than a year of '{cal.Label}' ({weekly * 52:0} minutes).", where, "Shorten the goal or measure it around the clock (24x7)."));
                    }
                }
            }

            if (target.Goals.Count > 0 && target.Goals[^1].When is not null)
            {
                d.Add(Diagnostic.Warning("config.goal_gap", $"No goal of SLA target '{target.Label}' applies when none of the conditions holds; those records get no timer.", where, "Add a last goal without a condition."));
            }
        }
    }

    private static readonly HashSet<string> GroupableTypes = [FieldTypes.SingleSelect, FieldTypes.Status, FieldTypes.Priority, FieldTypes.User, FieldTypes.Group, FieldTypes.Toggle];
    private static readonly HashSet<string> DateTypes = [FieldTypes.Date, FieldTypes.DateTime];

    /// <summary>Whether a field can group a metric or the explorer (one value per record, compared with ==).</summary>
    public static bool IsGroupable(FieldSpec field) => GroupableTypes.Contains(field.Type);

    private static void ValidateMetric(SpecCatalog config, MetricSpec metric, List<Diagnostic> d)
    {
        if (!Namespaces.IsValidId(metric.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{metric.Id}' is not a valid metric ID.", metric.Id));
        }

        if (string.IsNullOrWhiteSpace(metric.Label) || metric.Label.Length > 60)
        {
            d.Add(Diagnostic.Error("config.bad_label", $"Metric '{metric.Id}' needs a label of 1–60 characters.", metric.Id));
        }

        if (config.FindEntity(metric.Entity) is not { } entity)
        {
            d.Add(Diagnostic.Error("config.not_found", $"Metric '{metric.Label}' counts unknown entity '{metric.Entity}'.", metric.Id, Expression.Suggest(metric.Entity, config.Entities.Select(e => e.Id))));
            return;
        }

        if (CheckExpression(entity, metric.Filter, metric.Id, "filter", d) is { } filter && Records.QueryFilterSupport.Unsupported(filter.Root) is { } unsupported)
        {
            d.Add(Diagnostic.Error("config.bad_filter", $"The filter of metric '{metric.Label}' uses {unsupported}, which lists and dashboards cannot query.", metric.Id, "Use record fields, user.id, user.groups or user.roles, comparisons, in, &&, ||, ! and has/size/contains/startsWith/endsWith."));
        }

        FieldSpec? Field(string? key, string what)
        {
            if (key is null)
            {
                return null;
            }

            var field = entity.FindField(key);
            if (field is null)
            {
                d.Add(Diagnostic.Error("config.unknown_field", $"Metric '{metric.Label}' {what} '{key}', which {entity.Label} does not have.", metric.Id, Expression.Suggest(key, entity.Fields.Select(f => f.Key))));
            }

            return field;
        }

        var measure = metric.Measure;
        switch (measure.Kind)
        {
            case MetricMeasures.Count:
                break;
            case MetricMeasures.Sum or MetricMeasures.Avg or MetricMeasures.Min or MetricMeasures.Max:
                if (measure.Field is null)
                {
                    d.Add(Diagnostic.Error("config.bad_measure", $"Metric '{metric.Label}' needs a number field to {measure.Kind}.", metric.Id));
                }
                else if (Field(measure.Field, "measures") is { } number && number.Type is not (FieldTypes.Integer or FieldTypes.Decimal))
                {
                    d.Add(Diagnostic.Error("config.bad_measure", $"Metric '{metric.Label}' takes the {measure.Kind} of '{number.Key}', which is a {number.Type} field.", metric.Id, "Use an integer or decimal field."));
                }

                break;
            case MetricMeasures.Duration:
                foreach (var (key, what) in new[] { (measure.From, "measures from"), (measure.To, "measures to") })
                {
                    if (key is null)
                    {
                        d.Add(Diagnostic.Error("config.bad_measure", $"Metric '{metric.Label}' needs both ends of the duration (from and to).", metric.Id));
                    }
                    else if (Field(key, what) is { } instant && instant.Type != FieldTypes.DateTime)
                    {
                        d.Add(Diagnostic.Error("config.bad_measure", $"Metric '{metric.Label}' measures time with '{instant.Key}', which is a {instant.Type} field.", metric.Id, "Use date and time fields such as created_at."));
                    }
                }

                if (measure.Unit is not ("minutes" or "hours" or "days"))
                {
                    d.Add(Diagnostic.Error("config.bad_measure", $"Metric '{metric.Label}' has duration unit '{measure.Unit}'.", metric.Id, "minutes, hours or days"));
                }

                break;
            default:
                d.Add(Diagnostic.Error("config.bad_measure", $"Metric '{metric.Label}' has measure '{measure.Kind}'.", metric.Id, Expression.Suggest(measure.Kind, MetricMeasures.All) ?? string.Join(", ", MetricMeasures.All)));
                break;
        }

        if (metric.DateRange is { } range)
        {
            if (Field(range.Field, "limits dates by") is { } dateField && !DateTypes.Contains(dateField.Type))
            {
                d.Add(Diagnostic.Error("config.bad_date_range", $"Metric '{metric.Label}' limits dates by '{dateField.Key}', which is a {dateField.Type} field.", metric.Id, "Use a date or date and time field."));
            }

            if (range.Last is < 1 or > 366 || !MetricBuckets.All.Contains(range.Unit))
            {
                d.Add(Diagnostic.Error("config.bad_date_range", $"Metric '{metric.Label}' covers the last {range.Last} {range.Unit}(s); use 1–366 days, weeks or months.", metric.Id));
            }

            if (range.Calendar is { } calendar && config.FindBusinessCalendar(calendar) is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Metric '{metric.Label}' aligns dates to unknown calendar '{calendar}'.", metric.Id, Expression.Suggest(calendar, config.BusinessCalendars.Select(c => c.Id))));
            }

            if (range.TimeZone is { } zone && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            {
                d.Add(Diagnostic.Error("config.bad_time_zone", $"Metric '{metric.Label}' has time zone '{zone}'.", metric.Id, "Use an IANA name such as Europe/London."));
            }
        }

        if (metric.GroupBy is { } group && Field(group.Field, "groups by") is { } groupField)
        {
            if (group.Bucket is null && !IsGroupable(groupField))
            {
                d.Add(Diagnostic.Error("config.bad_group", $"Metric '{metric.Label}' groups by '{groupField.Key}', a {groupField.Type} field.", metric.Id, DateTypes.Contains(groupField.Type) ? "Add a bucket (day, week or month) and a date range." : "Group by a choice, status, priority, user, group or toggle field."));
            }

            if (group.Bucket is { } bucket)
            {
                if (!DateTypes.Contains(groupField.Type) || !MetricBuckets.All.Contains(bucket) || metric.DateRange is null)
                {
                    d.Add(Diagnostic.Error("config.bad_group", $"Metric '{metric.Label}' buckets '{groupField.Key}' by '{bucket}'; buckets need a date field, day/week/month and a date range.", metric.Id));
                }
                else if (Buckets(metric.DateRange, bucket) > 52)
                {
                    d.Add(Diagnostic.Error("config.too_large", $"Metric '{metric.Label}' would draw {Buckets(metric.DateRange, bucket)} {bucket} buckets; at most 52.", metric.Id, "Use a coarser bucket or a shorter range."));
                }
            }

            if (group.Limit is < 1 or > 25)
            {
                d.Add(Diagnostic.Error("config.too_large", $"Metric '{metric.Label}' shows {group.Limit} groups; use 1–25.", metric.Id));
            }
        }

        if (metric.Sla is { } sla)
        {
            if (config.FindSlaPolicy(sla.Policy) is not { } policy)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Metric '{metric.Label}' counts breaches of unknown SLA policy '{sla.Policy}'.", metric.Id, Expression.Suggest(sla.Policy, config.SlaPolicies.Select(p => p.Id))));
            }
            else if (policy.Entity != entity.Id)
            {
                d.Add(Diagnostic.Error("config.bad_sla", $"Metric '{metric.Label}' counts {entity.Label} records, but SLA policy '{policy.Label}' measures {policy.Entity}.", metric.Id));
            }
            else if (sla.Target is { } target && policy.FindTarget(target) is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"SLA policy '{policy.Label}' has no target '{target}'.", metric.Id, Expression.Suggest(target, policy.Targets.Select(t => t.Id))));
            }

            if (sla.State != "breached")
            {
                d.Add(Diagnostic.Error("config.bad_sla", $"Metric '{metric.Label}' counts SLA state '{sla.State}'; only 'breached' is supported.", metric.Id));
            }
        }

        foreach (var role in metric.Roles.Where(r => config.Roles.All(x => x.Id != r)))
        {
            d.Add(Diagnostic.Error("config.not_found", $"Metric '{metric.Label}' is for unknown role '{role}'.", metric.Id, Expression.Suggest(role, config.Roles.Select(r => r.Id))));
        }

        if (metric.Format is not (null or "number" or "duration" or "percent"))
        {
            d.Add(Diagnostic.Error("config.bad_format", $"Metric '{metric.Label}' has format '{metric.Format}'.", metric.Id, "number, duration or percent"));
        }

        // Fields some people may not see make the figure restricted for them (it is never shown with their data).
        var reads = new[] { measure.Field, measure.From, measure.To, metric.GroupBy?.Field, metric.DateRange?.Field }.OfType<string>().ToList();
        if (metric.Filter is not null)
        {
            try
            {
                reads.AddRange(Expression.Compile(metric.Filter).References.Where(r => r.StartsWith("record.", StringComparison.Ordinal)).Select(r => r["record.".Length..]));
            }
            catch (ExpressionException)
            {
                // Reported above.
            }
        }

        var hiddenFor = config.PermissionSets.Where(p => p.Fields.Any(f => f.Entity == entity.Id && f.Access == FieldAccessLevels.Hidden && reads.Contains(f.Field))).SelectMany(p => p.Roles).Distinct().ToList();
        if (hiddenFor.Count > 0)
        {
            d.Add(Diagnostic.Warning("metric.restricted_field", $"Metric '{metric.Label}' reads fields that {string.Join(", ", hiddenFor)} cannot see; they see the figure as restricted.", metric.Id));
        }
    }

    private static int Buckets(MetricDateRangeSpec range, string bucket)
    {
        var days = range.Unit switch { MetricBuckets.Week => range.Last * 7, MetricBuckets.Month => range.Last * 31, _ => range.Last };
        return bucket switch { MetricBuckets.Week => (days + 6) / 7, MetricBuckets.Month => (days + 30) / 31, _ => days };
    }

    private static void CheckCalendar(SpecCatalog config, string? calendar, string where, List<Diagnostic> d)
    {
        if (calendar is not null && calendar != SlaCalendars.AroundTheClock && config.FindBusinessCalendar(calendar) is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"'{where}' measures in unknown business calendar '{calendar}'.", where, Expression.Suggest(calendar, config.BusinessCalendars.Select(c => c.Id)) ?? $"Use {SlaCalendars.AroundTheClock} for elapsed time."));
        }
    }

    /// <summary>The payload type a record field's values have.</summary>
    private static string PayloadType(string fieldType) => fieldType switch
    {
        FieldTypes.Integer or FieldTypes.Decimal => EventFieldTypes.Number,
        FieldTypes.Toggle => EventFieldTypes.Boolean,
        FieldTypes.MultiSelect or FieldTypes.Principals => EventFieldTypes.List,
        _ => EventFieldTypes.String,
    };

    private static readonly HashSet<string> ApproverFieldTypes = [FieldTypes.User, FieldTypes.Group];

    private static void ValidateApprovalPolicy(SpecCatalog config, ApprovalPolicySpec policy, List<Diagnostic> d)
    {
        if (!Namespaces.IsValidId(policy.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{policy.Id}' is not a valid approval policy ID.", policy.Id));
        }

        if (string.IsNullOrWhiteSpace(policy.Label) || policy.Label.Length > 80)
        {
            d.Add(Diagnostic.Error("config.bad_label", $"Approval policy '{policy.Id}' needs a label of 1–80 characters.", policy.Id));
        }

        var entity = config.FindEntity(policy.Entity);
        if (entity?.Request?.Approval is null)
        {
            d.Add(Diagnostic.Error("config.bad_request_entity", $"Approval policy '{policy.Label}' applies to '{policy.Entity}', which has no approval binding.", policy.Id, string.Join(", ", config.Entities.Where(e => e.Request?.Approval is not null).Select(e => e.Id))));
            return;
        }

        if (policy.Stages.Count == 0 || policy.Stages.Count > MaxStages)
        {
            d.Add(Diagnostic.Error("config.bad_stages", $"Approval policy '{policy.Label}' needs 1–{MaxStages} stages.", policy.Id));
        }

        foreach (var dup in policy.Stages.GroupBy(s => s.Id).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_id", $"Approval policy '{policy.Label}' has two stages '{dup.Key}'.", policy.Id));
        }

        // Stage conditions see the request (record) and its answers (variables), checked against each item using the policy.
        var items = config.CatalogItems.Where(i => i.ApprovalPolicy == policy.Id).ToList();
        foreach (var stage in policy.Stages)
        {
            var target = $"{policy.Id}/{stage.Id}";
            if (!Namespaces.IsValidFieldKey(stage.Id))
            {
                d.Add(Diagnostic.Error("config.invalid_key", $"'{stage.Id}' is not a valid stage ID (lowercase letters, digits, underscores).", target));
            }

            if (string.IsNullOrWhiteSpace(stage.Label) || stage.Label.Length > 80)
            {
                d.Add(Diagnostic.Error("config.bad_label", $"Stage '{stage.Id}' needs a label of 1–80 characters.", target));
            }

            if (!ApprovalModes.Values.Contains(stage.Mode))
            {
                d.Add(Diagnostic.Error("config.bad_mode", $"Stage '{stage.Label}' has mode '{stage.Mode}'.", target, "Use all or any."));
            }

            if (stage.Approvers.Count == 0 || stage.Approvers.Count > MaxApprovers)
            {
                d.Add(Diagnostic.Error("config.bad_approvers", $"Stage '{stage.Label}' needs 1–{MaxApprovers} approvers.", target));
            }

            foreach (var approver in stage.Approvers.Concat(stage.EscalateTo))
            {
                ValidateApprover(config, entity, approver, target, d);
            }

            if (stage.ExpiresAfterHours is { } hours && (hours < 1 || hours > 24 * 90))
            {
                d.Add(Diagnostic.Error("config.bad_expiry", $"Stage '{stage.Label}' expires after {hours} hours; use 1 to 2160.", target));
            }

            if (!ApprovalExpiry.Values.Contains(stage.OnExpiry))
            {
                d.Add(Diagnostic.Error("config.bad_expiry", $"Stage '{stage.Label}' has onExpiry '{stage.OnExpiry}'.", target, "Use reject or escalate."));
            }
            else if (stage.OnExpiry == ApprovalExpiry.Escalate && (stage.EscalateTo.Count == 0 || stage.ExpiresAfterHours is null))
            {
                d.Add(Diagnostic.Error("config.bad_expiry", $"Stage '{stage.Label}' escalates but has no escalateTo approvers or no expiresAfterHours.", target));
            }

            if (!string.IsNullOrWhiteSpace(stage.When))
            {
                // Questions differ per item: until an item uses the policy only the syntax is checked; then the
                // condition is checked against every item that uses it.
                IEnumerable<ExpressionSchema?> schemas = items.Count == 0 ? [null] : items.Select(i => (ExpressionSchema?)EntitySchema.ForRequest(entity, i));
                foreach (var schema in schemas)
                {
                    try
                    {
                        Expression.Compile(stage.When, schema);
                    }
                    catch (ExpressionException ex)
                    {
                        d.Add(Diagnostic.Error(ex.Code, $"The condition of stage '{stage.Label}' is invalid: {ex.Message}", target, ex.Suggestion));
                        break;
                    }
                }
            }
        }
    }

    private static void ValidateApprover(SpecCatalog config, EntitySpec entity, ApproverSpec approver, string target, List<Diagnostic> d)
    {
        switch (approver.Kind)
        {
            case ApproverKinds.Group:
                if (string.IsNullOrWhiteSpace(approver.Ref))
                {
                    d.Add(Diagnostic.Error("config.bad_approvers", "A group approver needs a group key.", target));
                }

                break;
            case ApproverKinds.Role:
                if (!config.Roles.Exists(r => r.Id == approver.Ref))
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Approver role '{approver.Ref}' does not exist.", target, Expression.Suggest(approver.Ref, config.Roles.Select(r => r.Id))));
                }

                break;
            case ApproverKinds.Field:
                var parts = approver.Ref.Split('.', 2);
                if (parts is ["record", var key])
                {
                    if (entity.FindField(key) is not { } f || !ApproverFieldTypes.Contains(f.Type))
                    {
                        d.Add(Diagnostic.Error("config.bad_approvers", $"Approver field '{approver.Ref}' must be a user or group field of {entity.Label}.", target, Expression.Suggest(key, entity.Fields.Where(x => ApproverFieldTypes.Contains(x.Type)).Select(x => x.Key))));
                    }
                    else if (!f.ReadOnly && !f.System)
                    {
                        // A field the requester can fill would let them choose their own approver.
                        d.Add(Diagnostic.Error("config.self_chosen_approver", $"Approver field '{approver.Ref}' can be set by the requester; approvers must not be chosen by the person asking.", target, "Use a read-only field set by agents or the platform, a group, or a role."));
                    }
                }
                else if (parts is ["variables", _])
                {
                    d.Add(Diagnostic.Error("config.self_chosen_approver", $"Approver '{approver.Ref}' is an answer the requester gives; approvers must not be chosen by the person asking.", target, "Use a group, a role, or a read-only record field."));
                }
                else
                {
                    d.Add(Diagnostic.Error("config.bad_approvers", $"Approver field '{approver.Ref}' must look like record.<field> or variables.<question>.", target));
                }

                break;
            default:
                d.Add(Diagnostic.Error("config.bad_approvers", $"Approver kind '{approver.Kind}' is unknown.", target, string.Join(", ", ApproverKinds.Configurable)));
                break;
        }
    }

    private const int MaxStages = 10;

    private const int MaxApprovers = 20;

    private static void ValidateCatalogItem(SpecCatalog config, CatalogItemSpec item, List<Diagnostic> d)
    {
        if (!Namespaces.IsValidId(item.Id))
        {
            d.Add(Diagnostic.Error("config.invalid_id", $"'{item.Id}' is not a valid catalog item ID.", item.Id));
        }

        if (string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 80)
        {
            d.Add(Diagnostic.Error("config.bad_label", $"Catalog item '{item.Id}' needs a label of 1–80 characters.", item.Id));
        }

        var requestEntity = config.FindEntity(item.RequestEntity);
        if (requestEntity?.Request is null)
        {
            d.Add(Diagnostic.Error("config.bad_request_entity", $"Catalog item '{item.Label}' targets '{item.RequestEntity}', which does not receive requests.", item.Id, string.Join(", ", config.Entities.Where(e => e.Request is not null).Select(e => e.Id))));
            return;
        }

        if (item.Variables.Count > MaxVariables || item.Tasks.Count > MaxTasks)
        {
            d.Add(Diagnostic.Error("config.too_large", $"Catalog item '{item.Label}' has {item.Variables.Count} questions and {item.Tasks.Count} tasks; the limits are {MaxVariables} and {MaxTasks}.", item.Id, "Split it into several catalog items."));
        }

        foreach (var dup in item.Variables.GroupBy(v => v.Key).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_variable", $"Catalog item '{item.Label}' has two variables '{dup.Key}'.", item.Id));
        }

        foreach (var v in item.Variables)
        {
            var target = $"{item.Id}/{v.Key}";
            if (!Namespaces.IsValidFieldKey(v.Key))
            {
                d.Add(Diagnostic.Error("config.invalid_key", $"'{v.Key}' is not a valid variable key (lowercase letters, digits, underscores).", target));
            }

            if (!VariableTypes.All.Contains(v.Type))
            {
                d.Add(Diagnostic.Error("config.bad_variable_type", $"Variable '{v.Label}' has type '{v.Type}', which catalog items do not support.", target, Expression.Suggest(v.Type, VariableTypes.All)));
            }

            if (FieldTypes.UsesChoiceList(v.Type) && (v.ChoiceList is null || config.FindChoiceList(v.ChoiceList) is null))
            {
                d.Add(Diagnostic.Error("config.missing_choice_list", $"Variable '{v.Label}' needs an existing choice list.", target, v.ChoiceList is null ? "Create one with add_choice_list." : Expression.Suggest(v.ChoiceList, config.ChoiceLists.Select(c => c.Id))));
            }

            if (string.IsNullOrWhiteSpace(v.Label) || v.Label.Length > 120)
            {
                d.Add(Diagnostic.Error("config.bad_label", $"Variable '{v.Key}' needs a label of 1–120 characters.", target));
            }
        }

        foreach (var dup in item.Tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_task", $"Catalog item '{item.Label}' has two tasks '{dup.Key}'.", item.Id));
        }

        var taskIds = item.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var schema = EntitySchema.ForRequest(requestEntity, item);
        foreach (var t in item.Tasks)
        {
            var target = $"{item.Id}/{t.Id}";
            if (!Namespaces.IsValidFieldKey(t.Id))
            {
                d.Add(Diagnostic.Error("config.invalid_key", $"'{t.Id}' is not a valid task ID (lowercase letters, digits, underscores).", target));
            }

            if (string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > 200)
            {
                d.Add(Diagnostic.Error("config.bad_label", $"Task '{t.Id}' needs a title of 1–200 characters.", target));
            }

            foreach (var dep in t.DependsOn.Where(x => !taskIds.Contains(x) || x == t.Id))
            {
                d.Add(Diagnostic.Error("config.bad_dependency", $"Task '{t.Id}' depends on '{dep}', which is not another task of '{item.Label}'.", target, Expression.Suggest(dep, taskIds)));
            }

            if (!string.IsNullOrWhiteSpace(t.When))
            {
                try
                {
                    Expression.Compile(t.When, schema);
                }
                catch (ExpressionException ex)
                {
                    d.Add(Diagnostic.Error(ex.Code, $"The condition of task '{t.Id}' is invalid: {ex.Message}", target, ex.Suggestion));
                }
            }
        }

        if (FindTaskCycle(item) is { } cycle)
        {
            d.Add(Diagnostic.Error("config.task_cycle", $"Tasks of '{item.Label}' depend on each other in a cycle: {cycle}.", item.Id, "Remove one dependency so the tasks can start."));
        }

        if (item.ApprovalPolicy is { } policyId)
        {
            if (config.FindApprovalPolicy(policyId) is not { } policy)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Catalog item '{item.Label}' uses unknown approval policy '{policyId}'.", item.Id, Expression.Suggest(policyId, config.ApprovalPolicies.Select(p => p.Id))));
            }
            else if (policy.Entity != item.RequestEntity)
            {
                d.Add(Diagnostic.Error("config.bad_approval_policy", $"Approval policy '{policy.Label}' applies to {policy.Entity}, not to {item.RequestEntity}.", item.Id));
            }
        }

        if (item.Tasks.Count == 0)
        {
            d.Add(Diagnostic.Warning("config.no_tasks", $"Catalog item '{item.Label}' has no fulfillment tasks; its requests complete as soon as fulfillment starts.", item.Id));
        }
    }

    private static string? FindTaskCycle(CatalogItemSpec item)
    {
        var deps = item.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().DependsOn, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        bool Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 1)
            {
                path.Add(id);
                return true;
            }

            if (state.GetValueOrDefault(id) == 2 || !deps.TryGetValue(id, out var next))
            {
                return false;
            }

            state[id] = 1;
            path.Add(id);
            if (next.Any(Visit))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            return false;
        }

        return deps.Keys.Any(Visit) ? string.Join(" → ", path) : null;
    }

    private void ValidateView(SpecCatalog config, ViewSpec view, List<Diagnostic> d)
    {
        var entity = config.FindEntity(view.Entity);
        if (entity is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"View '{view.Id}' references unknown entity '{view.Entity}'.", view.Id));
            return;
        }

        if (view.Layout.Type != "layout.page")
        {
            d.Add(Diagnostic.Error("layout.bad_root", $"View '{view.Id}' must start with a layout.page node.", view.Id));
        }

        foreach (var dup in view.Layout.Descendants().GroupBy(n => n.Id).Where(g => g.Count() > 1))
        {
            d.Add(Diagnostic.Error("config.duplicate_id", $"Two nodes in {view.Label} share the ID '{dup.Key}'.", dup.Key, Namespaces.NewTenantNodeId()));
        }

        ValidateNode(config, view, entity, view.Layout, parent: null, d);
    }

    private void ValidateNode(SpecCatalog config, ViewSpec view, EntitySpec entity, NodeSpec node, NodeSpec? parent, List<Diagnostic> d)
    {
        var element = registry.Find(node.Type);
        if (element is null || !element.Placeable)
        {
            var placeable = registry.All.Where(e => e.Placeable).Select(e => e.Id).ToList();
            var suggestion = Expression.Suggest(node.Type, placeable);
            d.Add(Diagnostic.Error("layout.unknown_element", $"'{node.Type}' is not an element that can be placed on a page." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), node.Id, suggestion));
            return;
        }

        if (parent is not null)
        {
            var parentElement = registry.Find(parent.Type);
            if (parentElement is not null && (!parentElement.AllowsChild(node.Type) || !element.AllowsParent(parent.Type)))
            {
                var allowed = string.Join(", ", element.AllowedParents);
                d.Add(Diagnostic.Error("layout.bad_parent", $"{element.Label} ({node.Id}) cannot be placed inside {parentElement.Label} ({parent.Id}).", node.Id, $"Place it inside: {allowed}."));
            }
        }

        if (element.MaxChildren is { } max && node.Children.Count > max)
        {
            d.Add(Diagnostic.Error("layout.too_many_children", $"{element.Label} '{node.Id}' has {node.Children.Count} children; the limit is {max}.", node.Id, "Split it into several containers."));
        }

        foreach (var error in propsValidator.Validate(element, node.Props))
        {
            d.Add(Diagnostic.Error("layout.bad_props", $"{element.Label} '{node.Id}': {error}", node.Id));
        }

        var nodeEntity = node.StringProp("entity") is { } entityId ? config.FindEntity(entityId) : entity;
        if (node.StringProp("entity") is { } eid && nodeEntity is null)
        {
            d.Add(Diagnostic.Error("config.not_found", $"'{node.Id}' references unknown entity '{eid}'.", node.Id));
        }

        var fieldEntity = node.Type == "collection.column" && parent?.StringProp("entity") is { } tableEntity ? config.FindEntity(tableEntity) ?? entity : nodeEntity ?? entity;
        if (node.StringProp("field") is { } fieldKey && fieldEntity.FindField(fieldKey) is null)
        {
            d.Add(Diagnostic.Error("layout.unknown_field", $"'{node.Id}' shows field '{fieldKey}', which {fieldEntity.Label} does not have.", node.Id, Expression.Suggest(fieldKey, fieldEntity.Fields.Select(f => f.Key))));
        }

        if (node.Props["fields"] is JsonArray fields)
        {
            foreach (var key in fields.OfType<JsonValue>().Select(v => v.ToString()).Where(k => fieldEntity.FindField(k) is null))
            {
                d.Add(Diagnostic.Error("layout.unknown_field", $"'{node.Id}' lists field '{key}', which {fieldEntity.Label} does not have.", node.Id, Expression.Suggest(key, fieldEntity.Fields.Select(f => f.Key))));
            }
        }

        if (node.StringProp("relationship") is { } relId)
        {
            var rel = config.Relationships.Find(r => r.Id == relId);
            if (rel is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"'{node.Id}' uses unknown relationship '{relId}'.", node.Id, Expression.Suggest(relId, config.Relationships.Select(r => r.Id))));
            }
            else if (node.Props["columns"] is JsonArray cols && config.FindEntity(rel.Entity) is { } child)
            {
                foreach (var c in cols.OfType<JsonValue>().Select(v => v.ToString()).Where(c => child.FindField(c) is null))
                {
                    d.Add(Diagnostic.Error("layout.unknown_field", $"'{node.Id}' lists column '{c}', which {child.Label} does not have.", node.Id));
                }
            }
        }

        if (view.Kind == ViewKinds.Dashboard && !node.Type.StartsWith("layout.", StringComparison.Ordinal) && !node.Type.StartsWith("display.", StringComparison.Ordinal))
        {
            d.Add(Diagnostic.Error("layout.bad_dashboard", $"Dashboards show figures, not records: {element.Label} ({node.Id}) cannot be on dashboard '{view.Label}'.", node.Id, "Use layout and display elements such as display.kpi_tile."));
        }

        if (node.StringProp("metric") is { } metricId)
        {
            if (config.FindMetric(metricId) is not { } metric)
            {
                d.Add(Diagnostic.Error("config.not_found", $"'{node.Id}' shows unknown metric '{metricId}'.", node.Id, Expression.Suggest(metricId, config.Metrics.Select(m => m.Id))));
            }
            else if (node.Type is "display.bar_chart" or "display.metric_table" && metric.GroupBy is null)
            {
                d.Add(Diagnostic.Error("layout.bad_metric", $"{element.Label} '{node.Id}' needs a grouped metric; '{metric.Label}' has no groups.", node.Id, "Use display.kpi_tile, or add groupBy to the metric."));
            }
            else if (node.Type == "display.kpi_tile" && metric.GroupBy is not null)
            {
                d.Add(Diagnostic.Error("layout.bad_metric", $"A KPI tile shows one figure; metric '{metric.Label}' is grouped.", node.Id, "Use display.bar_chart or display.metric_table."));
            }
        }

        if (node.Type == "collection.data_table")
        {
            var listEntity = nodeEntity ?? entity;
            var groups = new List<string>();
            if (node.StringProp("groupBy") is { } groupBy)
            {
                groups.Add(groupBy);
            }

            if (node.Props["groupOptions"] is JsonArray options)
            {
                groups.AddRange(options.OfType<JsonValue>().Select(v => v.ToString()));
            }

            foreach (var key in groups.Distinct(StringComparer.Ordinal))
            {
                if (listEntity.FindField(key) is not { } field)
                {
                    d.Add(Diagnostic.Error("layout.unknown_field", $"'{node.Id}' groups by '{key}', which {listEntity.Label} does not have.", node.Id, Expression.Suggest(key, listEntity.Fields.Select(f => f.Key))));
                }
                else if (!IsGroupable(field))
                {
                    d.Add(Diagnostic.Error("layout.bad_group", $"'{node.Id}' groups by '{key}', a {field.Type} field.", node.Id, "Group by a choice, status, priority, user, group or toggle field."));
                }
            }
        }

        if (node.Type == "collection.saved_views" && node.Props["views"] is JsonArray savedViews)
        {
            foreach (var savedMetric in savedViews.Select(v => v?["metric"]).OfType<JsonValue>().Select(v => v.ToString()))
            {
                if (config.FindMetric(savedMetric) is not { } metric)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"A saved view in '{node.Id}' uses unknown metric '{savedMetric}'.", node.Id, Expression.Suggest(savedMetric, config.Metrics.Select(m => m.Id))));
                }
                else if (metric.Entity != entity.Id || metric.GroupBy is not null || metric.DateRange is not null)
                {
                    d.Add(Diagnostic.Error("layout.bad_metric", $"A saved view in '{node.Id}' uses metric '{metric.Label}', which must count {entity.Label} records without groups or date ranges.", node.Id));
                }
            }
        }

        CheckExpression(nodeEntity ?? entity, node.StringProp("visibleWhen"), node.Id, "visibility condition", d);
        CheckExpression(nodeEntity ?? entity, node.StringProp("filter"), node.Id, "filter", d);

        if (node.Type == "layout.row")
        {
            var spans = node.Children.Where(c => !c.Hidden).Select(c => c.Props["span"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : 0).ToList();
            if (spans.Count > 0 && spans.Sum() != 12)
            {
                d.Add(Diagnostic.Error("layout.row_spans", $"Columns in row '{node.Id}' span {spans.Sum()} of 12 grid units ({string.Join("/", spans)}).", node.Id, "Adjust spans to total 12, e.g. 8/4, 6/6, or 4/4/4."));
            }
        }

        foreach (var child in node.Children)
        {
            ValidateNode(config, view, entity, child, node, d);
        }
    }

    private static void ValidatePermissions(SpecCatalog config, List<Diagnostic> d)
    {
        var roles = config.Roles.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var ps in config.PermissionSets)
        {
            foreach (var role in ps.Roles.Where(r => !roles.Contains(r)))
            {
                d.Add(Diagnostic.Error("config.not_found", $"Permission set '{ps.Id}' references unknown role '{role}'.", ps.Id, Expression.Suggest(role, roles)));
            }

            foreach (var e in ps.Entities)
            {
                if (e.Entity == "*")
                {
                    continue;
                }

                var entity = config.FindEntity(e.Entity);
                if (entity is null)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Permission set '{ps.Id}' references unknown entity '{e.Entity}'.", ps.Id));
                    continue;
                }

                foreach (var scope in new[] { e.Read, e.Update }.Where(s => s is not (RecordScopes.None or RecordScopes.Related or RecordScopes.All)))
                {
                    d.Add(Diagnostic.Error("config.bad_scope", $"Permission set '{ps.Id}' uses scope '{scope}'; use none, related, or all.", ps.Id));
                }

                CheckExpression(entity, e.UpdateCondition, ps.Id, "update condition", d);
            }

            foreach (var f in ps.Fields)
            {
                var entity = config.FindEntity(f.Entity);
                if (entity is null || (f.Field != "*" && entity.FindField(f.Field) is null))
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Permission set '{ps.Id}' references unknown field '{f.Entity}/{f.Field}'.", ps.Id));
                }

                if (f.Access is not (FieldAccessLevels.Hidden or FieldAccessLevels.Read or FieldAccessLevels.Write))
                {
                    d.Add(Diagnostic.Error("config.bad_access", $"Field access must be hidden, read, or write (got '{f.Access}').", ps.Id));
                }
            }

            foreach (var a in ps.Actions.Where(a => !PlatformActions.All.Contains(a)))
            {
                d.Add(Diagnostic.Error("config.not_found", $"Permission set '{ps.Id}' grants unknown action '{a}'.", ps.Id, Expression.Suggest(a, PlatformActions.All)));
            }
        }
    }

    /// <summary>Notification templates, subscriptions, profiles and routes (ADR 0021).</summary>
    private static void ValidateNotifications(SpecCatalog config, List<Diagnostic> d)
    {
        const int MaxBody = 4000;
        const int MaxRecipients = 50;
        ValidateNotificationOwnership(config, d);
        foreach (var nt in config.NotificationTemplates)
        {
            var entity = nt.Entity is null ? null : config.FindEntity(nt.Entity);
            if (nt.Entity is not null && entity is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Notification template '{nt.Id}' references unknown entity '{nt.Entity}'.", nt.Id, Expression.Suggest(nt.Entity, config.Entities.Select(e => e.Id))));
                continue;
            }

            if (nt.Event is not null)
            {
                if (entity is null)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Notification template '{nt.Id}' has a trigger but no entity.", nt.Id, "Set entity, or use a subscription and route instead of a trigger."));
                    continue;
                }

                foreach (var r in nt.Recipients.Where(r => r != "watchers"))
                {
                    if (entity.FindField(r) is not { Type: FieldTypes.User or FieldTypes.Email })
                    {
                        d.Add(Diagnostic.Error("config.bad_recipient", $"Notification '{nt.Id}' recipient '{r}' must be a user or email field.", nt.Id));
                    }
                }
            }

            var texts = new List<(string Where, string Text)> { ("subject", nt.Subject), ("body", nt.Body) };
            foreach (var (channel, variant) in nt.Variants)
            {
                if (!NotificationChannels.All.Contains(channel))
                {
                    d.Add(Diagnostic.Error("config.bad_channel", $"Template '{nt.Id}' has a variant for unknown channel '{channel}'.", nt.Id, $"Use one of: {string.Join(", ", NotificationChannels.All)}."));
                }

                texts.Add(($"{channel} body", variant.Body));
                if (variant.Subject is { } subject)
                {
                    texts.Add(($"{channel} subject", subject));
                }
            }

            foreach (var (where, text) in texts)
            {
                if (text.Length > MaxBody)
                {
                    d.Add(Diagnostic.Error("config.too_large", $"The {where} of template '{nt.Id}' is longer than {MaxBody} characters.", nt.Id, "Shorten it; link to the record for details."));
                }

                if (RawHtml().IsMatch(text))
                {
                    d.Add(Diagnostic.Error("config.raw_html", $"The {where} of template '{nt.Id}' contains HTML.", nt.Id, "Templates are plain text; channels format them."));
                }

                foreach (Match m in AnyPlaceholder().Matches(text))
                {
                    var name = m.Groups[1].Value;
                    var ok = name is "recipient.name" or "link" || name.StartsWith("event.", StringComparison.Ordinal)
                        || (name.StartsWith("record.", StringComparison.Ordinal) && entity?.FindField(name["record.".Length..]) is not null)
                        || name is "record.number" or "record.id";
                    if (!ok)
                    {
                        d.Add(Diagnostic.Error("config.bad_placeholder", $"Template '{nt.Id}' uses unknown placeholder '{{{{{name}}}}}' in its {where}.", nt.Id,
                            entity is null ? "Use {{event.<field>}}, {{recipient.name}} or {{link}}." : $"Use {{{{record.<field>}}}} with a field of {entity.Label}, {{{{event.<field>}}}}, {{{{recipient.name}}}} or {{{{link}}}}."));
                    }
                }
            }

            foreach (var field in nt.ExternalFields.Where(f => entity?.FindField(f) is null))
            {
                d.Add(Diagnostic.Error("config.not_found", $"Template '{nt.Id}' lets external recipients see unknown field '{field}'.", nt.Id));
            }
        }

        foreach (var sub in config.Subscriptions)
        {
            var type = config.FindEventType(sub.Event);
            if (type is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Subscription '{sub.Label}' listens to unknown event type '{sub.Event}'.", sub.Id, Expression.Suggest(sub.Event, config.EventTypes.Select(e => e.Id))));
                continue;
            }

            if (!SubscriptionTargets.All.Contains(sub.Target))
            {
                d.Add(Diagnostic.Error("config.bad_target", $"Subscription '{sub.Label}' targets '{sub.Target}'.", sub.Id, $"Use one of: {string.Join(", ", SubscriptionTargets.All)}."));
            }

            var entityId = sub.Entity ?? type.SubjectEntity;
            var entity = entityId is null ? null : config.FindEntity(entityId);
            if (entityId is not null && entity is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Subscription '{sub.Label}' references unknown entity '{entityId}'.", sub.Id));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sub.When))
            {
                try
                {
                    var references = Expression.Compile(sub.When, EntitySchema.ForSubscription(entity, type)).References;

                    // Whether someone is told reveals what the condition read: no hidden fields, and no values of
                    // restricted events (their payloads may carry hidden fields).
                    var hidden = entity is null ? [] : HiddenFields(config, entity);
                    if (references.Where(r => r.StartsWith("record.", StringComparison.Ordinal)).Select(r => r["record.".Length..]).FirstOrDefault(hidden.Contains) is { } secret)
                    {
                        d.Add(Diagnostic.Error("config.notification_hidden_field", $"The condition of subscription '{sub.Label}' reads '{secret}', which some people may not see.", sub.Id, "Use a field everyone who reads the record can see."));
                    }

                    if (type.Sensitivity == EventSensitivities.Restricted && references.Any(r => r.StartsWith("event.", StringComparison.Ordinal)))
                    {
                        d.Add(Diagnostic.Error("config.restricted_event_text", $"The condition of subscription '{sub.Label}' reads values of restricted event '{type.Id}'.", sub.Id, "Use record fields everyone who reads the record can see."));
                    }
                }
                catch (ExpressionException ex)
                {
                    d.Add(Diagnostic.Error(ex.Code, $"The condition of subscription '{sub.Label}' is invalid: {ex.Message}", sub.Id, ex.Suggestion));
                }
            }
        }

        foreach (var profile in config.NotificationProfiles)
        {
            if (profile.Channels.Count == 0 || profile.Channels.Any(c => !NotificationChannels.All.Contains(c)) || profile.Channels.Distinct().Count() != profile.Channels.Count)
            {
                d.Add(Diagnostic.Error("config.bad_channel", $"Profile '{profile.Label}' needs one or more different channels.", profile.Id, $"Use: {string.Join(", ", NotificationChannels.All)}."));
            }

            if (!NotificationDelivery.Kinds.Contains(profile.Delivery))
            {
                d.Add(Diagnostic.Error("config.bad_delivery", $"Profile '{profile.Label}' delivery '{profile.Delivery}' is unknown.", profile.Id, "Use all or fallback."));
            }

            if (profile.MinSeverity is { } min && !EventSeverities.All.Contains(min))
            {
                d.Add(Diagnostic.Error("config.bad_severity", $"Profile '{profile.Label}' minimum severity '{min}' is unknown.", profile.Id, $"Use one of: {string.Join(", ", EventSeverities.All)}."));
            }
        }

        foreach (var route in config.NotificationRoutes)
        {
            var sub = config.FindSubscription(route.Subscription);
            var profile = config.FindNotificationProfile(route.Profile);
            var template = config.FindNotificationTemplate(route.Template);
            if (sub is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Route '{route.Label}' uses unknown subscription '{route.Subscription}'.", route.Id, Expression.Suggest(route.Subscription, config.Subscriptions.Select(s => s.Id))));
            }
            else if (sub.Target != SubscriptionTargets.Notify)
            {
                d.Add(Diagnostic.Error("config.bad_target", $"Route '{route.Label}' uses subscription '{sub.Label}', which targets {sub.Target}.", route.Id, "Use a subscription with target notify."));
            }

            if (profile is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Route '{route.Label}' uses unknown profile '{route.Profile}'.", route.Id, Expression.Suggest(route.Profile, config.NotificationProfiles.Select(p => p.Id))));
            }

            if (template is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Route '{route.Label}' uses unknown template '{route.Template}'.", route.Id, Expression.Suggest(route.Template, config.NotificationTemplates.Select(t => t.Id))));
            }
            else if (template.Event is not null)
            {
                d.Add(Diagnostic.Error("config.bad_template", $"Route '{route.Label}' uses template '{template.Id}', which has its own trigger.", route.Id, "Use a template without an event trigger."));
            }

            // A mandatory route must keep reaching people: nothing it relies on may be switched off, and the bell is
            // always one of its channels (REQ-NOT-004).
            if (route.Mandatory && route.Enabled && (sub?.Enabled == false || template?.Enabled == false || profile?.Enabled == false || profile?.Channels.Contains(NotificationChannels.InApp) == false))
            {
                d.Add(Diagnostic.Error("config.mandatory_unreachable", $"Mandatory route '{route.Label}' would stop reaching people: its subscription, template and profile must stay enabled and the profile must include in_app.", route.Id,
                    "Keep them enabled and in_app in the profile, or ask an administrator to make the route optional first."));
            }

            if (route.Recipients.Count is 0 or > MaxRecipients)
            {
                d.Add(Diagnostic.Error(route.Recipients.Count == 0 ? "config.bad_recipient" : "config.too_large", $"Route '{route.Label}' needs 1 to {MaxRecipients} recipient rules.", route.Id));
            }

            var type = sub is null ? null : config.FindEventType(sub.Event);
            var entityId = sub?.Entity ?? type?.SubjectEntity;
            var entity = entityId is null ? null : config.FindEntity(entityId);
            if (template?.Entity is { } templateEntity && entityId is not null && templateEntity != entityId)
            {
                d.Add(Diagnostic.Error("config.bad_template", $"Route '{route.Label}' renders a template for {templateEntity} on {entityId} events.", route.Id, "Use a template for the subscription's entity."));
            }

            foreach (var r in route.Recipients)
            {
                var problem = r.Kind switch
                {
                    RecipientKinds.Field => entity?.FindField(r.Ref) is { Type: FieldTypes.User or FieldTypes.Group or FieldTypes.Email or FieldTypes.Principals } ? null : $"'{r.Ref}' is not a user, group or email field of {entity?.Label ?? "the event's record"}",
                    RecipientKinds.Role => config.Roles.Any(x => x.Id == r.Ref) || r.Ref == PlatformModule.TenantAdminRole ? null : $"role '{r.Ref}' does not exist",
                    RecipientKinds.Group => GroupKey().IsMatch(r.Ref) ? null : $"'{r.Ref}' is not a group key",
                    RecipientKinds.User => Guid.TryParse(r.Ref, out _) ? null : $"'{r.Ref}' is not a person's ID",
                    RecipientKinds.Approvers => type?.Fields.ContainsKey("approval") == true || sub?.Event.StartsWith("platform.approval.", StringComparison.Ordinal) == true ? null : "approvers only exist for approval events",
                    RecipientKinds.AlertOwner => sub?.Event.StartsWith("platform.alert.", StringComparison.Ordinal) == true ? null : "alert_owner only exists for alert events",
                    RecipientKinds.Address => EmailAddress().IsMatch(r.Ref) || PhoneNumber().IsMatch(r.Ref) ? null : $"'{r.Ref}' is not an email address or phone number (+ and digits)",
                    _ => $"kind '{r.Kind}' is unknown",
                };
                if (problem is not null)
                {
                    d.Add(Diagnostic.Error("config.bad_recipient", $"Route '{route.Label}': {problem}.", route.Id, $"Recipient kinds: {string.Join(", ", RecipientKinds.All)}."));
                }
            }

            // External recipients (addresses and email fields) only get public events, and only the template's external fields.
            if (route.Recipients.Any(r => IsExternalRecipient(entity, r)) && type is not null && type.Sensitivity != EventSensitivities.Public)
            {
                d.Add(Diagnostic.Error("config.external_redaction", $"Route '{route.Label}' sends {type.Sensitivity} event '{type.Id}' to external addresses.", route.Id, "Only public events go to external recipients; notify people in the workspace instead."));
            }

            // Restricted payloads may carry hidden fields: their values never become message text.
            if (type?.Sensitivity == EventSensitivities.Restricted && template is not null && TemplateTexts(template).Any(t => AnyPlaceholder().Matches(t).Any(m => m.Groups[1].Value.StartsWith("event.", StringComparison.Ordinal))))
            {
                d.Add(Diagnostic.Error("config.restricted_event_text", $"Route '{route.Label}' renders values of restricted event '{type.Id}' with template '{template.Id}'.", route.Id, "Use {{record.<field>}} placeholders, which render per recipient, instead of {{event.<field>}}."));
            }
        }
    }

    /// <summary>Recipients outside the workspace: addresses, and email fields of the record (ADR 0021).</summary>
    internal static bool IsExternalRecipient(EntitySpec? entity, RecipientSpec r) =>
        RecipientKinds.IsExternal(r) || (r.Kind == RecipientKinds.Field && entity?.FindField(r.Ref)?.Type == FieldTypes.Email);

    private static IEnumerable<string> TemplateTexts(NotificationTemplateSpec t) =>
        [t.Subject, t.Body, .. t.Variants.Values.SelectMany(v => new[] { v.Subject ?? string.Empty, v.Body })];

    private static HashSet<string> HiddenFields(SpecCatalog config, EntitySpec entity) =>
        config.PermissionSets.SelectMany(p => p.Fields).Where(f => f.Entity == entity.Id && f.Access == FieldAccessLevels.Hidden).Select(f => f.Field).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\{\s*([a-z][a-z0-9_.]*)\s*\}\}")]
    private static partial Regex AnyPlaceholder();

    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z][^>]*>")]
    private static partial Regex RawHtml();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,40}$")]
    private static partial Regex GroupKey();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"^\+[0-9]{7,15}$")]
    private static partial Regex PhoneNumber();

    private static CompiledExpression? CheckExpression(EntitySpec entity, string? expression, string target, string what, List<Diagnostic> d)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        try
        {
            return Expression.Compile(expression, EntitySchema.For(entity));
        }
        catch (ExpressionException ex)
        {
            d.Add(Diagnostic.Error(ex.Code, $"The {what} of '{target}' is invalid: {ex.Message}", target, ex.Suggestion));
            return null;
        }
    }
}

/// <summary>Validates node props against an element's JSON Schema (port; implemented with JsonSchema.Net).</summary>
public interface IPropsValidator
{
    IEnumerable<string> Validate(ElementDefinition element, JsonObject props);
}
