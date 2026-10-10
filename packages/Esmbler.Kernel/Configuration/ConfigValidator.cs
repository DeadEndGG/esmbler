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
public sealed class ConfigValidator(IElementRegistry registry, IPropsValidator propsValidator)
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

        foreach (var nt in config.NotificationTemplates)
        {
            var entity = config.FindEntity(nt.Entity);
            if (entity is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Notification template '{nt.Id}' references unknown entity '{nt.Entity}'.", nt.Id));
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
        Check(config.ChoiceLists.Select(c => c.Id), "choice list");
        Check(config.Rules.Select(r => r.Id), "rule");
        Check(config.Views.Select(v => v.Id), "view");
        Check(config.Workflows.Select(w => w.Id), "workflow");
        Check(config.Roles.Select(r => r.Id), "role");
        Check(config.PermissionSets.Select(p => p.Id), "permission set");
        Check(config.Relationships.Select(r => r.Id), "relationship");
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
            if (entity.FindField(access.Field) is not { Type: FieldTypes.User or FieldTypes.Group })
            {
                d.Add(Diagnostic.Error("config.bad_access_field", $"Access field '{access.Field}' on {entity.Label} must be a user or group field.", entity.Id));
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
