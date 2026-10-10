using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Esmbler.Mcp.Tools;

public sealed class RuleActionInput
{
    [Description("require, show, hide, read_only, set_value, restrict_options, message, or emit_event")]
    public required string Type { get; set; }

    [Description("Field key the action applies to (not needed for message).")]
    public string? Field { get; set; }

    [Description("set_value: expression for the value, e.g. 'high' (quoted text) or record.priority.")]
    public string? Value { get; set; }

    [Description("restrict_options: allowed option IDs.")]
    public List<string>? Options { get; set; }

    [Description("message: text to show.")]
    public string? Message { get; set; }

    [Description("emit_event: event type ID to publish when the condition becomes true (see list_event_types).")]
    public string? Event { get; set; }

    [Description("emit_event: payload field key to expression, e.g. {\"priority\": \"record.priority\"}.")]
    public Dictionary<string, string>? Payload { get; set; }
}

public sealed class EventEmitInput
{
    [Description("Event type ID (see list_event_types).")]
    public required string Event { get; set; }

    [Description("Payload field key to expression over record and prior, e.g. {\"priority\": \"record.priority\", \"from\": \"prior.status\"}.")]
    public Dictionary<string, string> Payload { get; set; } = [];

    [Description("Optional severity expression, e.g. record.priority == 'p1' ? 'critical' : 'high'. Defaults to the event type's severity.")]
    public string? Severity { get; set; }
}

public sealed class CatalogVariableInput
{
    [Description("Question people see, e.g. \"Licence plate\".")]
    public required string Label { get; set; }

    [Description("short_text, long_text, integer, decimal, date, date_time, single_select, multi_select, toggle, email, user, or group.")]
    public required string Type { get; set; }

    [Description("Optional snake_case key; derived from the label when omitted.")]
    public string? Key { get; set; }

    public bool Required { get; set; }

    public string? Help { get; set; }

    [Description("single_select/multi_select: option labels (a choice list is created).")]
    public List<string>? Choices { get; set; }

    [Description("single_select/multi_select: an existing choice list ID instead of choices.")]
    public string? ChoiceList { get; set; }
}

/// <summary>One approval stage for add_approval_policy (ADR 0016).</summary>
public sealed class ApprovalStageInput
{
    [Description("Stage name people see, e.g. \"Manager approval\".")]
    public required string Label { get; set; }

    [Description("all (every approver must approve) or any (the first approval decides). Default all.")]
    public string? Mode { get; set; }

    [Description("Role IDs whose holders may approve, e.g. core.tickets.approver.")]
    public List<string>? Roles { get; set; }

    [Description("Group keys whose members may approve, e.g. service_desk.")]
    public List<string>? Groups { get; set; }

    [Description("Read-only user or group fields naming the approver: record.<field>. Requesters never choose their own approvers.")]
    public List<string>? Fields { get; set; }

    [Description("Optional condition over record and variables; the stage is skipped when false, e.g. variables.access_level == 'admin'.")]
    public string? When { get; set; }

    [Description("Hours until the stage expires; omit to wait indefinitely.")]
    public int? ExpiresAfterHours { get; set; }

    [Description("Group keys that receive the request when the stage expires (escalation). Without them the request is declined on expiry.")]
    public List<string>? EscalateToGroups { get; set; }
}

public sealed class SlaGoalInput
{
    [Description("Goal in minutes (business minutes when the target uses a calendar), e.g. 480 for one 8-hour business day.")]
    public required int Minutes { get; set; }

    [Description("Optional condition selecting the records this goal applies to, e.g. record.priority == 'p1'. Put the goal without a condition last.")]
    public string? When { get; set; }

    [Description("Optional calendar ID for this goal, or 24x7 to measure it around the clock.")]
    public string? Calendar { get; set; }
}

public sealed class SlaTargetInput
{
    [Description("Target name, e.g. \"Resolution\".")]
    public required string Label { get; set; }

    [Description("When the clock stops (target met if within the goal), e.g. record.status in ['resolved', 'closed'].")]
    public required string Stop { get; set; }

    [Description("Goals in order; the first whose condition holds when the clock starts applies.")]
    public required List<SlaGoalInput> Goals { get; set; }

    [Description("When the clock starts; omit to start when the record is created.")]
    public string? Start { get; set; }

    [Description("While the clock pauses, e.g. record.status == 'on_hold'.")]
    public string? Pause { get; set; }

    [Description("Business calendar ID; omit for elapsed time around the clock.")]
    public string? Calendar { get; set; }

    [Description("resume (default), restart or none: what happens when a stopped record is reopened.")]
    public string? OnReopen { get; set; }

    [Description("Warn when this share of the goal has elapsed (1-99). Default 75.")]
    public int? WarnAtPercent { get; set; }
}

public sealed class WorkingHoursInput
{
    [Description("mon, tue, wed, thu, fri, sat or sun.")]
    public required string Day { get; set; }

    [Description("Start, HH:mm local time.")]
    public required string Start { get; set; }

    [Description("End, HH:mm local time (24:00 for midnight).")]
    public required string End { get; set; }
}

/// <summary>What a metric computes, for add_metric and update_metric (ADR 0019).</summary>
public sealed class MetricInput
{
    [Description("count (default), sum, avg, min, max, or duration (average time between two date-time fields).")]
    public string? Measure { get; set; }

    [Description("sum/avg/min/max: a number field key.")]
    public string? Field { get; set; }

    [Description("duration: start field (a date_time field or created_at).")]
    public string? From { get; set; }

    [Description("duration: end field (a date_time field or updated_at).")]
    public string? To { get; set; }

    [Description("duration: minutes, hours (default) or days.")]
    public string? Unit { get; set; }

    [Description("Group by this field (choice, status, priority, user, group or toggle; see describe_metric_fields), or by a date field with group_bucket.")]
    public string? GroupBy { get; set; }

    [Description("day, week or month: group a date field into buckets (needs date_field and last).")]
    public string? GroupBucket { get; set; }

    [Description("At most this many groups, largest first (1-25, default 12).")]
    public int? GroupLimit { get; set; }

    [Description("Date field limiting the records to a rolling window, e.g. created_at.")]
    public string? DateField { get; set; }

    [Description("Window length: the last N days, weeks or months (including the current one).")]
    public int? Last { get; set; }

    [Description("day (default), week or month.")]
    public string? LastUnit { get; set; }

    [Description("Count only records with a recorded breach of this SLA policy (see list_sla_policies).")]
    public string? SlaPolicy { get; set; }

    [Description("Limit the SLA condition to one target, e.g. resolution.")]
    public string? SlaTarget { get; set; }
}

public sealed class CatalogTaskInput
{
    [Description("What the fulfiller does, e.g. \"Issue the permit\".")]
    public required string Title { get; set; }

    [Description("Optional snake_case ID; derived from the title when omitted. dependsOn refers to these IDs.")]
    public string? Id { get; set; }

    public string? Description { get; set; }

    [Description("Group key that receives the task, e.g. service_desk.")]
    public string? AssignmentGroup { get; set; }

    [Description("IDs of tasks of this item that must finish first.")]
    public List<string>? DependsOn { get; set; }

    [Description("Only create the task when this holds, e.g. variables.needs_laptop == true.")]
    public string? When { get; set; }
}

/// <summary>
/// Build tools (REQ-AI-003). Every change is a Command appended to the open draft and appears on the live page
/// within a second. Pass the draft version you last saw as expected_version; a newer draft is reported as a conflict.
/// Tools never publish.
/// </summary>
[McpServerToolType]
public sealed class BuildTools(ToolRunner runner, DraftService drafts, PreviewService preview)
{
    [McpServerTool(Name = "open_draft"), Description("Open (or join) the draft for this workspace. Returns its version. Everyone else keeps seeing the published pages until a person publishes.")]
    public Task<CallToolResult> OpenDraft() =>
        runner.RunAsync("open_draft", AgentScopes.ConfigEdit, null, async ctx => await drafts.OpenAsync(ctx, default));

    [McpServerTool(Name = "add_field"), Description("Add a field to an entity and, in the same change, optionally place it on a page card, make it required under a condition, and add it to the list as a column, filter or grouping. For single_select/multi_select give choices (labels) and a choice list is created. All parts apply together or not at all, and undo together. New fields become available to metrics (describe_metric_fields) by type. Returns the new draft version and the step numbers.")]
    public Task<CallToolResult> AddField(
        [Description("Entity ID, e.g. core.tickets.ticket.")] string entity,
        [Description("Label people see, e.g. \"Vendor\".")] string label,
        [Description("Field type: short_text, long_text, rich_text, integer, decimal, date, date_time, single_select, multi_select, toggle, email, user, group, lookup.")] string type,
        [Description("Draft version you last saw.")] int expected_version,
        [Description("Optional key (must start with tenant__). Derived from the label when omitted.")] string? key = null,
        [Description("Whether the field is always required.")] bool required = false,
        [Description("Maximum length for text fields.")] int? max_length = null,
        [Description("Choice labels for select fields.")] string[]? choices = null,
        [Description("Help text shown under the field.")] string? help = null,
        [Description("Lookup target entity ID (lookup fields).")] string? target = null,
        [Description("Optional: a layout.card (or column/tab) node ID to place the field in, from get_view.")] string? place_in_view_node = null,
        [Description("Optional: view ID that contains place_in_view_node, e.g. core.tickets.record.")] string? view = null,
        [Description("Optional: place after this sibling node ID.")] string? after = null,
        [Description("Optional condition that makes the field required, e.g. record.category == 'hardware'. Added as a rule in the same change.")] string? required_when = null,
        [Description("Also show the field as a column in the entity's list.")] bool list_column = false,
        [Description("Also offer it as a list filter (choice, user and group fields).")] bool list_filter = false,
        [Description("Also offer \"group by\" on it in the list (choice, user, group and toggle fields).")] bool list_group = false) =>
        runner.RunAsync("add_field", AgentScopes.ConfigEdit, entity, async ctx =>
        {
            var fieldKey = key ?? Namespaces.TenantKeyFromLabel(label);
            var ops = new List<OverlayOp>();
            string? listId = null;
            if (type is FieldTypes.SingleSelect or FieldTypes.MultiSelect)
            {
                if (choices is not { Length: > 0 })
                {
                    throw new KernelException("missing_choices", $"A {type.Replace('_', ' ')} field needs choices.", 400, fieldKey, "Pass choices, e.g. [\"Low\", \"High\"].");
                }

                listId = $"{fieldKey}_options";
                ops.Add(new AddChoiceList { ChoiceList = new ChoiceListSpec { Id = listId, Label = label, Options = choices.Select(c => new ChoiceOption { Id = Slug(c), Label = c }).ToList() } });
            }

            ops.Add(new AddField { Entity = entity, Field = new FieldSpec { Key = fieldKey, Type = type, Label = label, Required = required, MaxLength = max_length, Help = help, ChoiceList = listId, Target = target } });
            if (place_in_view_node is not null)
            {
                if (view is null)
                {
                    throw new KernelException("missing_view", "Say which view the node is in.", 400, place_in_view_node, "Pass view, e.g. core.tickets.record.");
                }

                ops.Add(new InsertNode { View = view, Parent = place_in_view_node, Anchor = after is null ? null : new Anchor { After = after }, Node = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "field", Props = new JsonObject { ["field"] = fieldKey } } });
            }

            if (required_when is not null)
            {
                var ruleSlug = fieldKey.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal) ? fieldKey[Namespaces.TenantPrefix.Length..] : fieldKey;
                ops.Add(new AddRule
                {
                    Rule = new RuleSpec
                    {
                        Id = $"{Namespaces.TenantPrefix}require_{ruleSlug}",
                        Kind = RuleKinds.Conditional,
                        Entity = entity,
                        Description = $"{label} is required when {required_when}",
                        When = required_when,
                        Actions = [new RuleActionSpec { Type = RuleActionTypes.Require, Field = fieldKey }],
                    },
                });
            }

            if (list_column || list_filter || list_group)
            {
                ops.AddRange(await ListOpsAsync(ctx, entity, fieldKey, type, list_column, list_filter, list_group));
            }

            return await ApplyAllAsync(ctx, ops, expected_version);
        });

    /// <summary>Operations that put a new field into the entity's list: a column, a filter and a grouping option.</summary>
    private async Task<IEnumerable<OverlayOp>> ListOpsAsync(RequestContext ctx, string entity, string fieldKey, string type, bool column, bool filter, bool group)
    {
        var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
        var list = config.Views.FirstOrDefault(v => v.Entity == entity && v.Kind == ViewKinds.List)
            ?? throw new KernelException("no_list", $"'{entity}' has no list view to add the field to.", 400, entity, "Leave list_column, list_filter and list_group off.");
        var nodes = list.Layout.Descendants().ToList();
        var table = nodes.FirstOrDefault(n => n.Type == "collection.data_table") ?? throw new KernelException("no_list", $"List view '{list.Id}' has no table.", 400, list.Id);
        var ops = new List<OverlayOp>();
        if (column)
        {
            ops.Add(new InsertNode { View = list.Id, Parent = table.Id, Node = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "collection.column", Props = new JsonObject { ["field"] = fieldKey } } });
        }

        if (filter)
        {
            if (type is not (FieldTypes.SingleSelect or FieldTypes.Status or FieldTypes.Priority or FieldTypes.User or FieldTypes.Group))
            {
                throw new KernelException("not_filterable", $"A {type.Replace('_', ' ')} field cannot be a list filter.", 400, fieldKey, "List filters work on single select, status, priority, user and group fields; use search for text.");
            }

            var bar = nodes.FirstOrDefault(n => n.Type == "collection.filter_bar") ?? throw new KernelException("no_filter_bar", $"List view '{list.Id}' has no filter bar.", 400, list.Id);
            var fields = new JsonArray([.. (bar.Props["fields"] as JsonArray ?? []).Select(f => f?.DeepClone()), JsonValue.Create(fieldKey)]);
            ops.Add(new UpdateNode { View = list.Id, Node = bar.Id, Props = new JsonObject { ["fields"] = fields } });
        }

        if (group)
        {
            if (type is not (FieldTypes.SingleSelect or FieldTypes.Status or FieldTypes.Priority or FieldTypes.User or FieldTypes.Group or FieldTypes.Toggle))
            {
                throw new KernelException("not_groupable", $"A list cannot be grouped by a {type.Replace('_', ' ')} field.", 400, fieldKey, "Group by single select, status, priority, user, group or toggle fields.");
            }

            var options = new JsonArray([.. (table.Props["groupOptions"] as JsonArray ?? []).Select(f => f?.DeepClone()), JsonValue.Create(fieldKey)]);
            ops.Add(new UpdateNode { View = list.Id, Node = table.Id, Props = new JsonObject { ["groupOptions"] = options } });
        }

        return ops;
    }

    [McpServerTool(Name = "update_field"), Description("Change a field: relabel, make required (tenants can tighten base fields but not loosen them), help text, limits, pattern, default.")]
    public Task<CallToolResult> UpdateField(
        string entity,
        [Description("Field key.")] string field,
        int expected_version,
        string? label = null,
        bool? required = null,
        string? help = null,
        int? max_length = null,
        decimal? min = null,
        decimal? max = null,
        [Description("Regular expression the value must match, e.g. ^[0-9]+$ for numbers only.")] string? pattern = null,
        [Description("Message shown when the pattern does not match.")] string? pattern_message = null) =>
        runner.RunAsync("update_field", AgentScopes.ConfigEdit, $"{entity}/{field}", async ctx =>
        {
            var patch = new FieldPatch { Label = label, Required = required, Help = help, MaxLength = max_length, Min = min, Max = max, Pattern = pattern, PatternMessage = pattern_message };
            return await ApplyAllAsync(ctx, [new UpdateField { Entity = entity, Field = field, Set = patch }], expected_version);
        });

    [McpServerTool(Name = "reset_to_default"), Description("Reset one customized element (from get_customizations) to the default module's definition. Elements this workspace added are removed; validate_draft then shows anything that still depends on them.")]
    public Task<CallToolResult> ResetToDefault([Description("Element key from get_customizations, e.g. core.tickets.ticket/category.")] string element, int expected_version) =>
        runner.RunAsync("reset_to_default", AgentScopes.ConfigEdit, element, async ctx =>
            await ApplyAllAsync(ctx, [new ResetElement { Target = element }], expected_version));

    [McpServerTool(Name = "hide_field"), Description("Hide (or show again) a field everywhere. Data is kept. The title field and workflow state field cannot be hidden.")]
    public Task<CallToolResult> HideField(string entity, string field, int expected_version, bool hidden = true) =>
        runner.RunAsync("hide_field", AgentScopes.ConfigEdit, $"{entity}/{field}", async ctx =>
            await ApplyAllAsync(ctx, [new HideField { Entity = entity, Field = field, Hidden = hidden }], expected_version));

    [McpServerTool(Name = "add_choice_option"), Description("Add an option to a choice list (option IDs added to base lists get the tenant__ prefix automatically).")]
    public Task<CallToolResult> AddChoiceOption(string choice_list, string label, int expected_version, string? after_option = null) =>
        runner.RunAsync("add_choice_option", AgentScopes.ConfigEdit, choice_list, async ctx =>
        {
            var id = choice_list.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal) ? Slug(label) : Namespaces.TenantPrefix + Slug(label);
            return await ApplyAllAsync(ctx, [new AddChoiceOption { ChoiceList = choice_list, Option = new ChoiceOption { Id = id, Label = label }, Anchor = after_option is null ? null : new Anchor { After = after_option } }], expected_version);
        });

    [McpServerTool(Name = "add_card"), Description("Add a titled card with fields to a page column or tab, next to an existing node. The simplest way to add a page section.")]
    public Task<CallToolResult> AddCard(
        [Description("View ID, e.g. core.tickets.record.")] string view,
        [Description("Parent column or tab node ID from get_view.")] string parent,
        [Description("Card title, e.g. \"Vendor\".")] string title,
        [Description("Field keys to show in the card, in order.")] string[] fields,
        int expected_version,
        [Description("Place after this sibling node ID (recommended).")] string? after = null,
        [Description("1 or 2 field columns.")] int columns = 1) =>
        runner.RunAsync("add_card", AgentScopes.ConfigEdit, parent, async ctx =>
        {
            var props = new JsonObject { ["title"] = title };
            if (columns == 2)
            {
                props["columns"] = 2;
            }

            var node = new NodeSpec
            {
                Id = Namespaces.NewTenantNodeId(),
                Type = "layout.card",
                Props = props,
                Children = fields.Select(f => new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "field", Props = new JsonObject { ["field"] = f } }).ToList(),
            };
            return await ApplyAllAsync(ctx, [new InsertNode { View = view, Parent = parent, Node = node, Anchor = after is null ? null : new Anchor { After = after } }], expected_version);
        });

    [McpServerTool(Name = "insert_node"), Description("Insert any placeable element (see list_elements) into a parent node. props must match the element's props schema. New node IDs are generated.")]
    public Task<CallToolResult> InsertNode(string view, string parent, [Description("Element type, e.g. display.callout.")] string type, [Description("Props object as JSON.")] JsonElement props, int expected_version, string? after = null, string? before = null) =>
        runner.RunAsync("insert_node", AgentScopes.ConfigEdit, parent, async ctx =>
        {
            var node = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = type, Props = JsonNode.Parse(props.GetRawText()) as JsonObject ?? throw new KernelException("invalid_props", "props must be a JSON object.", 400) };
            return await ApplyAllAsync(ctx, [new InsertNode { View = view, Parent = parent, Node = node, Anchor = after is null && before is null ? null : new Anchor { After = after, Before = before } }], expected_version);
        });

    [McpServerTool(Name = "update_node"), Description("Change props of a node (e.g. a card title). A null value removes the prop.")]
    public Task<CallToolResult> UpdateNode(string view, string node, JsonElement props, int expected_version) =>
        runner.RunAsync("update_node", AgentScopes.ConfigEdit, node, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateNode { View = view, Node = node, Props = JsonNode.Parse(props.GetRawText()) as JsonObject ?? throw new KernelException("invalid_props", "props must be a JSON object.", 400) }], expected_version));

    [McpServerTool(Name = "move_node"), Description("Move a node to another parent or position.")]
    public Task<CallToolResult> MoveNode(string view, string node, string parent, int expected_version, string? after = null, string? before = null) =>
        runner.RunAsync("move_node", AgentScopes.ConfigEdit, node, async ctx =>
            await ApplyAllAsync(ctx, [new MoveNode { View = view, Node = node, Parent = parent, Anchor = after is null && before is null ? null : new Anchor { After = after, Before = before } }], expected_version));

    [McpServerTool(Name = "remove_node"), Description("Remove a node. Nodes from the default module are hidden instead, so upgrades keep working.")]
    public Task<CallToolResult> RemoveNode(string view, string node, int expected_version) =>
        runner.RunAsync("remove_node", AgentScopes.ConfigEdit, node, async ctx =>
            await ApplyAllAsync(ctx, [new RemoveNode { View = view, Node = node }], expected_version));

    [McpServerTool(Name = "add_rule"), Description("Add a rule. Conditional: when <expression> then actions (require, show, hide, read_only, set_value, restrict_options, message, emit_event; emit_event publishes once each time the condition becomes true). Validation: assert <expression> with a message. Expressions use record.<field_key>, e.g. record.category == 'access'. Data-scope rules apply on every form and the API.")]
    public Task<CallToolResult> AddRule(
        string entity,
        [Description("Plain-English summary, e.g. \"Access requests need a justification\".")] string description,
        int expected_version,
        [Description("Condition, e.g. record.category == 'access'. Omit for always.")] string? when = null,
        [Description("Actions for conditional rules.")] RuleActionInput[]? actions = null,
        [Description("conditional (default) or validation.")] string kind = RuleKinds.Conditional,
        [Description("Validation rules: expression that must be true, e.g. record.title.size() >= 10.")] string? assert = null,
        [Description("Validation rules: message telling people how to fix the value.")] string? message = null,
        [Description("Validation rules: field the message attaches to.")] string? field = null) =>
        runner.RunAsync("add_rule", AgentScopes.ConfigEdit, entity, async ctx =>
        {
            var rule = new RuleSpec
            {
                Id = Namespaces.TenantPrefix + "r_" + Slug(description)[..Math.Min(40, Slug(description).Length)] + "_" + Guid.NewGuid().ToString("N")[..4],
                Kind = kind,
                Entity = entity,
                Description = description,
                When = when,
                Assert = assert,
                Message = message,
                Field = field,
                Actions = (actions ?? []).Select(a => new RuleActionSpec
                {
                    Type = a.Type,
                    Field = a.Field,
                    Value = a.Value,
                    Options = a.Options,
                    Message = a.Message,
                    Emit = a.Event is null ? null : new EventEmitSpec { Type = a.Event, Payload = a.Payload ?? [] },
                }).ToList(),
            };
            return await ApplyAllAsync(ctx, [new AddRule { Rule = rule }], expected_version);
        });

    [McpServerTool(Name = "set_rule_enabled"), Description("Enable or disable a rule. Default-module rules can only be disabled, not edited.")]
    public Task<CallToolResult> SetRuleEnabled(string rule, bool enabled, int expected_version) =>
        runner.RunAsync("set_rule_enabled", AgentScopes.ConfigEdit, rule, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateRule { Rule = rule, Enabled = enabled }], expected_version));

    [McpServerTool(Name = "remove_rule"), Description("Remove a rule this workspace added.")]
    public Task<CallToolResult> RemoveRule(string rule, int expected_version) =>
        runner.RunAsync("remove_rule", AgentScopes.ConfigEdit, rule, async ctx =>
            await ApplyAllAsync(ctx, [new RemoveRule { Rule = rule }], expected_version));

    [McpServerTool(Name = "undo_step"), Description("Undo one step of the draft by its number. Refused if later steps depend on it.")]
    public Task<CallToolResult> UndoStep(int step, int expected_version) =>
        runner.RunAsync("undo_step", AgentScopes.ConfigEdit, step.ToString(System.Globalization.CultureInfo.InvariantCulture), async ctx =>
        {
            var state = await drafts.SetUndoneAsync(ctx, step, true, expected_version, default);
            return new { state.Version, undone = step };
        });

    [McpServerTool(Name = "add_catalog_item"), Description("Add a service catalog item: the questions (variables) requesters answer and the fulfillment tasks that run, with dependencies (dependsOn) and optional conditions (when, over variables.<key>). Tasks start when an agent starts fulfillment; dependents wait for their prerequisites.")]
    public Task<CallToolResult> AddCatalogItem(
        [Description("Name in the catalog, e.g. \"Parking permit\".")] string label,
        [Description("Questions requesters answer, in order.")] CatalogVariableInput[] variables,
        [Description("Fulfillment tasks.")] CatalogTaskInput[] tasks,
        int expected_version,
        [Description("One sentence shown on the catalog card.")] string? description = null,
        [Description("Catalog section, e.g. Hardware, Access, Facilities.")] string? category = null,
        [Description("Entity that receives the requests; defaults to the workspace's request entity when there is exactly one.")] string? request_entity = null) =>
        runner.RunAsync("add_catalog_item", AgentScopes.ConfigEdit, label, async ctx =>
        {
            var ops = new List<OverlayOp>();
            var slug = Slug(label);
            if (request_entity is null)
            {
                var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
                var candidates = config.Entities.Where(e => e.Request is not null).Select(e => e.Id).ToList();
                request_entity = candidates.Count == 1
                    ? candidates[0]
                    : throw new KernelException("request_entity_required", candidates.Count == 0 ? "No entity in this workspace receives requests." : "More than one entity receives requests.", 400, "request_entity", candidates.Count == 0 ? "Install a module release with service requests first." : $"Pass request_entity, one of: {string.Join(", ", candidates)}.");
            }

            var item = new CatalogItemSpec
            {
                Id = Namespaces.TenantPrefix + slug[..Math.Min(40, slug.Length)],
                Label = label,
                Description = description,
                Category = category,
                RequestEntity = request_entity,
            };
            foreach (var v in variables)
            {
                var key = v.Key ?? Slug(v.Label);
                string? listId = v.ChoiceList;
                if (v.Choices is { Count: > 0 } choices)
                {
                    listId = $"{Namespaces.TenantPrefix}cl_{slug[..Math.Min(20, slug.Length)]}_{key}";
                    ops.Add(new AddChoiceList { ChoiceList = new ChoiceListSpec { Id = listId, Label = v.Label, Options = choices.Select(c => new ChoiceOption { Id = Slug(c), Label = c }).ToList() } });
                }

                item.Variables.Add(new VariableSpec { Key = key, Label = v.Label, Type = v.Type, Required = v.Required, Help = v.Help, ChoiceList = listId });
            }

            foreach (var t in tasks)
            {
                item.Tasks.Add(new FulfillmentTaskSpec { Id = t.Id ?? Slug(t.Title)[..Math.Min(40, Slug(t.Title).Length)], Title = t.Title, Description = t.Description, AssignmentGroup = t.AssignmentGroup, DependsOn = t.DependsOn ?? [], When = t.When });
            }

            ops.Add(new AddCatalogItem { Item = item });
            return await ApplyAllAsync(ctx, ops, expected_version);
        });

    [McpServerTool(Name = "update_catalog_item"), Description("Change a catalog item's name, description, section, or whether it can be requested (active). Default-module items cannot have their questions or tasks replaced; deactivate them and add your own.")]
    public Task<CallToolResult> UpdateCatalogItem(
        [Description("Catalog item ID from list_catalog_items.")] string item,
        int expected_version,
        string? label = null,
        string? description = null,
        string? category = null,
        bool? active = null,
        [Description("Approval policy ID from list_approval_policies. Changing which policy approves an item is a permission change that only people with permission administration make; AI agents are refused.")] string? approval_policy = null) =>
        runner.RunAsync("update_catalog_item", AgentScopes.ConfigEdit, item, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateCatalogItem { Item = item, Set = new CatalogItemPatch { Label = label, Description = description, Category = category, Active = active, ApprovalPolicy = approval_policy } }], expected_version));

    [McpServerTool(Name = "add_event_type"), Description("Declare an event the workspace publishes, e.g. \"Vendor contract expiring\". Publish it from a rule (add_rule with an emit_event action) or a transition (update_transition with publish_events). Subscribers react to it by ID.")]
    public Task<CallToolResult> AddEventType(
        [Description("Event name in the past tense, e.g. \"Hardware ticket escalated\".")] string label,
        [Description("Payload field keys and types (string, number, boolean, list), e.g. {\"priority\": \"string\"}.")] Dictionary<string, string> fields,
        int expected_version,
        [Description("Entity the event is about, e.g. core.tickets.ticket.")] string? subject_entity = null,
        [Description("info, low, medium, high or critical.")] string severity = "info",
        string? description = null) =>
        runner.RunAsync("add_event_type", AgentScopes.ConfigEdit, label, async ctx =>
        {
            var slug = Slug(label);
            var type = new EventTypeSpec
            {
                Id = Namespaces.TenantPrefix + slug[..Math.Min(60, slug.Length)],
                Label = label,
                Description = description,
                SubjectEntity = subject_entity,
                Severity = severity,
                Fields = fields.ToDictionary(f => f.Key, f => new EventFieldSpec { Type = f.Value }),
            };
            return await ApplyAllAsync(ctx, [new AddEventType { EventType = type }], expected_version);
        });

    [McpServerTool(Name = "add_business_calendar"), Description("Add business hours in a time zone, minus holidays, for measuring SLA goals in business time, e.g. \"London office hours\".")]
    public Task<CallToolResult> AddBusinessCalendar(
        string label,
        [Description("IANA time zone, e.g. Europe/London or America/New_York.")] string time_zone,
        [Description("Working intervals per weekday.")] WorkingHoursInput[] week,
        int expected_version,
        [Description("Holidays as YYYY-MM-DD dates.")] string[]? holidays = null) =>
        runner.RunAsync("add_business_calendar", AgentScopes.ConfigEdit, label, async ctx =>
        {
            var slug = Slug(label);
            var calendar = new BusinessCalendarSpec
            {
                Id = Namespaces.TenantPrefix + slug[..Math.Min(60, slug.Length)],
                Label = label,
                TimeZone = time_zone,
                Week = [.. week.Select(w => new WorkingHoursSpec { Day = w.Day, Start = w.Start, End = w.End })],
                Holidays = [.. (holidays ?? []).Select(h => new HolidaySpec { Date = h })],
            };
            return await ApplyAllAsync(ctx, [new AddBusinessCalendar { Calendar = calendar }], expected_version);
        });

    [McpServerTool(Name = "update_business_calendar"), Description("Change a business calendar's time zone, hours or holidays (default or your own). Running SLA clocks keep the calendar they started with; new clocks use the change.")]
    public Task<CallToolResult> UpdateBusinessCalendar(
        string calendar,
        int expected_version,
        string? label = null,
        [Description("IANA time zone.")] string? time_zone = null,
        [Description("Replaces all working intervals.")] WorkingHoursInput[]? week = null,
        [Description("Replaces all holidays (YYYY-MM-DD).")] string[]? holidays = null) =>
        runner.RunAsync("update_business_calendar", AgentScopes.ConfigEdit, calendar, async ctx =>
            await ApplyAllAsync(ctx, [new UpdateBusinessCalendar
            {
                Calendar = calendar,
                Set = new BusinessCalendarPatch
                {
                    Label = label,
                    TimeZone = time_zone,
                    Week = week?.Select(w => new WorkingHoursSpec { Day = w.Day, Start = w.Start, End = w.End }).ToList(),
                    Holidays = holidays?.Select(h => new HolidaySpec { Date = h }).ToList(),
                },
            }], expected_version));

    [McpServerTool(Name = "add_sla_policy"), Description("Add service-level targets on an entity's records, e.g. \"VIP resolution within 4 business hours\". Each target is a clock with start, pause and stop conditions and goals by condition (usually priority).")]
    public Task<CallToolResult> AddSlaPolicy(
        string entity,
        string label,
        SlaTargetInput[] targets,
        int expected_version,
        [Description("Optional condition limiting which records the policy applies to, e.g. record.priority in ['p1', 'p2'].")] string? when = null,
        string? description = null) =>
        runner.RunAsync("add_sla_policy", AgentScopes.ConfigEdit, entity, async ctx =>
        {
            var slug = Slug(label);
            var policy = new SlaPolicySpec
            {
                Id = Namespaces.TenantPrefix + slug[..Math.Min(60, slug.Length)],
                Label = label,
                Description = description,
                Entity = entity,
                When = when,
                Targets = [.. targets.Select(ToTarget)],
            };
            return await ApplyAllAsync(ctx, [new AddSlaPolicy { Policy = policy }], expected_version);
        });

    [McpServerTool(Name = "set_sla_goals"), Description("Change one SLA target's goals (and optionally its calendar or warning threshold), on a default or your own policy, e.g. P2 resolution in 8 business hours. Running clocks keep their goal; new clocks use the change.")]
    public Task<CallToolResult> SetSlaGoals(
        string policy,
        [Description("Target ID, e.g. response or resolution (see list_sla_policies).")] string target,
        SlaGoalInput[] goals,
        int expected_version,
        [Description("Business calendar ID, or 24x7.")] string? calendar = null,
        int? warn_at_percent = null) =>
        runner.RunAsync("set_sla_goals", AgentScopes.ConfigEdit, $"{policy}/{target}", async ctx =>
        {
            var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
            var current = config.FindSlaPolicy(policy) ?? throw KernelException.NotFound($"SLA policy '{policy}'");
            if (current.FindTarget(target) is null)
            {
                throw new KernelException("not_found", $"SLA policy '{current.Label}' has no target '{target}'.", 404, target, Expression.Suggest(target, current.Targets.Select(t => t.Id)));
            }

            var targets = current.Targets.Select(SpecJson.Clone).ToList();
            var changed = targets.Single(t => t.Id == target);
            changed.Goals = [.. goals.Select(g => new SlaGoalSpec { Minutes = g.Minutes, When = g.When, Calendar = g.Calendar })];
            changed.Calendar = calendar == SlaCalendars.AroundTheClock ? null : calendar ?? changed.Calendar;
            changed.WarnAtPercent = warn_at_percent ?? changed.WarnAtPercent;
            return await ApplyAllAsync(ctx, [new UpdateSlaPolicy { Policy = policy, Set = new SlaPolicyPatch { Targets = targets } }], expected_version);
        });

    private static SlaTargetSpec ToTarget(SlaTargetInput t)
    {
        var slug = Slug(t.Label);
        return new SlaTargetSpec
        {
            Id = slug[..Math.Min(40, slug.Length)],
            Label = t.Label,
            Start = t.Start,
            Stop = t.Stop,
            Pause = t.Pause,
            Calendar = t.Calendar is null or SlaCalendars.AroundTheClock ? null : t.Calendar,
            OnReopen = t.OnReopen ?? SlaReopen.Resume,
            WarnAtPercent = t.WarnAtPercent ?? 75,
            Goals = [.. t.Goals.Select(g => new SlaGoalSpec { Minutes = g.Minutes, When = g.When, Calendar = g.Calendar })],
        };
    }

    [McpServerTool(Name = "add_approval_policy"), Description("Add an approval policy: stages that run in order, each with approvers (roles, groups, or fields naming a person) who decide in parallel. Point a catalog item at it with update_catalog_item (approval_policy). The requester never approves their own request, and AI agents never approve.")]
    public Task<CallToolResult> AddApprovalPolicy(
        [Description("Policy name, e.g. \"Finance approval\".")] string label,
        [Description("Stages in order.")] ApprovalStageInput[] stages,
        int expected_version,
        string? description = null,
        [Description("Request entity; defaults to the workspace's request entity with approvals when there is exactly one.")] string? request_entity = null) =>
        runner.RunAsync("add_approval_policy", AgentScopes.ConfigEdit, label, async ctx =>
        {
            if (request_entity is null)
            {
                var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
                var candidates = config.Entities.Where(e => e.Request?.Approval is not null).Select(e => e.Id).ToList();
                request_entity = candidates.Count == 1
                    ? candidates[0]
                    : throw new KernelException("request_entity_required", candidates.Count == 0 ? "No entity in this workspace takes approvals." : "More than one entity takes approvals.", 400, "request_entity", candidates.Count == 0 ? "Install a module release with approvals first." : $"Pass request_entity, one of: {string.Join(", ", candidates)}.");
            }

            var slug = Slug(label);
            var policy = new ApprovalPolicySpec { Id = Namespaces.TenantPrefix + slug[..Math.Min(40, slug.Length)], Label = label, Description = description, Entity = request_entity };
            foreach (var s in stages)
            {
                var id = Slug(s.Label);
                policy.Stages.Add(new ApprovalStageSpec
                {
                    Id = id[..Math.Min(40, id.Length)],
                    Label = s.Label,
                    Mode = s.Mode ?? ApprovalModes.All,
                    Approvers =
                    [
                        .. (s.Roles ?? []).Select(r => new ApproverSpec { Kind = ApproverKinds.Role, Ref = r }),
                        .. (s.Groups ?? []).Select(g => new ApproverSpec { Kind = ApproverKinds.Group, Ref = g }),
                        .. (s.Fields ?? []).Select(x => new ApproverSpec { Kind = ApproverKinds.Field, Ref = x }),
                    ],
                    When = s.When,
                    ExpiresAfterHours = s.ExpiresAfterHours,
                    OnExpiry = s.EscalateToGroups is { Count: > 0 } ? ApprovalExpiry.Escalate : ApprovalExpiry.Reject,
                    EscalateTo = (s.EscalateToGroups ?? []).Select(g => new ApproverSpec { Kind = ApproverKinds.Group, Ref = g }).ToList(),
                });
            }

            return await ApplyAllAsync(ctx, [new AddApprovalPolicy { Policy = policy }], expected_version);
        });

    [McpServerTool(Name = "add_metric"), Description("Define a reusable metric, e.g. \"Unassigned P1 tickets\" or \"Open tickets by team\". Dashboards, tiles and saved views use it by ID; every figure is computed for the person looking at it and opens the list of exactly those records. Use describe_metric_fields for what can be grouped, dated or summed.")]
    public Task<CallToolResult> AddMetric(
        string entity,
        string label,
        int expected_version,
        [Description("Which records count, in the list filter language over record and user, e.g. !(record.status in ['resolved', 'closed', 'cancelled']) && !has(record.assignee). Omit to count every record people can see.")] string? filter = null,
        [Description("What to compute and how to group; omit for a plain count.")] MetricInput? options = null,
        [Description("Role IDs that see the figure; omit for everyone who can read the records. Never widens what people can read.")] string[]? roles = null,
        string? description = null) =>
        runner.RunAsync("add_metric", AgentScopes.ConfigEdit, entity, async ctx =>
        {
            var slug = Slug(label);
            var metric = new MetricSpec
            {
                Id = Namespaces.TenantPrefix + "metric_" + slug[..Math.Min(52, slug.Length)],
                Label = label,
                Description = description,
                Entity = entity,
                Filter = filter,
                Roles = [.. roles ?? []],
            };
            Apply(metric, options);
            return await ApplyAllAsync(ctx, [new AddMetric { Metric = metric }], expected_version);
        });

    [McpServerTool(Name = "update_metric"), Description("Change a metric (default or your own): label, filter, what it computes, grouping, date range, SLA condition or roles. Every tile and saved view using it follows the change.")]
    public Task<CallToolResult> UpdateMetric(
        string metric,
        int expected_version,
        string? label = null,
        [Description("Replaces the filter (list filter language).")] string? filter = null,
        [Description("Replaces what it computes and how it groups; omitted parts keep their current value.")] MetricInput? options = null,
        [Description("Replaces the roles that see the figure.")] string[]? roles = null,
        string? description = null) =>
        runner.RunAsync("update_metric", AgentScopes.ConfigEdit, metric, async ctx =>
        {
            var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
            var current = SpecJson.Clone(config.FindMetric(metric) ?? throw KernelException.NotFound($"Metric '{metric}'"));
            Apply(current, options);
            var set = new MetricPatch
            {
                Label = label,
                Description = description,
                Filter = filter,
                Roles = roles?.ToList(),
                Measure = options is null ? null : current.Measure,
                GroupBy = options?.GroupBy is null && options?.GroupBucket is null && options?.GroupLimit is null ? null : current.GroupBy,
                DateRange = options?.DateField is null && options?.Last is null && options?.LastUnit is null ? null : current.DateRange,
                Sla = options?.SlaPolicy is null && options?.SlaTarget is null ? null : current.Sla,
            };
            return await ApplyAllAsync(ctx, [new UpdateMetric { Metric = metric, Set = set }], expected_version);
        });

    [McpServerTool(Name = "remove_metric"), Description("Remove a metric this workspace added. Refused while a tile or saved view uses it (list_metrics shows usedBy); remove those first.")]
    public Task<CallToolResult> RemoveMetric(string metric, int expected_version) =>
        runner.RunAsync("remove_metric", AgentScopes.ConfigEdit, metric, async ctx =>
            await ApplyAllAsync(ctx, [new RemoveMetric { Metric = metric }], expected_version));

    [McpServerTool(Name = "add_dashboard_tile"), Description("Show a metric on a dashboard: kpi (one number; ungrouped metrics), bar (a bar per group; grouped metrics) or table (grouped metrics). Put it in a column or card of the dashboard (list_metrics lists dashboards and their containers).")]
    public Task<CallToolResult> AddDashboardTile(
        string view,
        string parent,
        string metric,
        [Description("kpi, bar or table.")] string kind,
        int expected_version,
        [Description("Tile label or chart title; defaults to the metric's label.")] string? title = null,
        [Description("kpi: neutral, info, success, warning or critical.")] string? tone = null,
        [Description("bar: horizontal (categories, default) or vertical (time).")] string? orientation = null,
        string? after = null) =>
        runner.RunAsync("add_dashboard_tile", AgentScopes.ConfigEdit, view, async ctx =>
        {
            var props = new JsonObject { ["metric"] = metric };
            var type = kind switch
            {
                "kpi" => "display.kpi_tile",
                "bar" => "display.bar_chart",
                "table" => "display.metric_table",
                _ => throw new KernelException("invalid_kind", $"'{kind}' is not a tile kind.", 400, "kind", "Use kpi, bar or table."),
            };
            if (title is not null)
            {
                props[kind == "kpi" ? "label" : "title"] = title;
            }

            if (kind == "kpi" && tone is not null)
            {
                props["tone"] = tone;
            }

            if (kind == "bar" && orientation is not null)
            {
                props["orientation"] = orientation;
            }

            var node = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = type, Props = props };
            return await ApplyAllAsync(ctx, [new InsertNode { View = view, Parent = parent, Node = node, Anchor = after is null ? null : new Anchor { After = after } }], expected_version);
        });

    private static void Apply(MetricSpec metric, MetricInput? o)
    {
        if (o is null)
        {
            return;
        }

        if (o.Measure is not null || o.Field is not null || o.From is not null || o.To is not null || o.Unit is not null)
        {
            metric.Measure = new MetricMeasureSpec
            {
                Kind = o.Measure ?? metric.Measure.Kind,
                Field = o.Field ?? metric.Measure.Field,
                From = o.From ?? metric.Measure.From,
                To = o.To ?? metric.Measure.To,
                Unit = o.Unit ?? metric.Measure.Unit,
            };
        }

        if (o.GroupBy is not null || o.GroupBucket is not null || o.GroupLimit is not null)
        {
            metric.GroupBy = new MetricGroupSpec
            {
                Field = o.GroupBy ?? metric.GroupBy?.Field ?? throw new KernelException("invalid_group", "group_bucket and group_limit need group_by.", 400, "group_by", "Name the field to group by."),
                Bucket = o.GroupBucket ?? metric.GroupBy?.Bucket,
                Limit = o.GroupLimit ?? metric.GroupBy?.Limit ?? 12,
            };
        }

        if (o.DateField is not null || o.Last is not null || o.LastUnit is not null)
        {
            metric.DateRange = new MetricDateRangeSpec
            {
                Field = o.DateField ?? metric.DateRange?.Field ?? "created_at",
                Last = o.Last ?? metric.DateRange?.Last ?? throw new KernelException("invalid_date_range", "A date range needs last (how many days, weeks or months).", 400, "last", "Set last, e.g. 30 with last_unit day."),
                Unit = o.LastUnit ?? metric.DateRange?.Unit ?? MetricBuckets.Day,
            };
        }

        if (o.SlaPolicy is not null || o.SlaTarget is not null)
        {
            metric.Sla = new MetricSlaSpec
            {
                Policy = o.SlaPolicy ?? metric.Sla?.Policy ?? throw new KernelException("invalid_sla", "sla_target needs sla_policy.", 400, "sla_policy", "Name the SLA policy (list_sla_policies)."),
                Target = o.SlaTarget ?? metric.Sla?.Target,
            };
        }
    }

    [McpServerTool(Name = "add_workflow_state"), Description("Add a state to a workflow (it also becomes a status option). Category is one of new, active, pending, resolved, closed, cancelled. Add transitions into and out of it with add_transition.")]
    public Task<CallToolResult> AddWorkflowState(
        [Description("Workflow ID from describe_entity, e.g. core.tickets.request_lifecycle.")] string workflow,
        [Description("State name people see, e.g. \"Awaiting information\".")] string label,
        [Description("new, active, pending, resolved, closed, or cancelled.")] string category,
        int expected_version) =>
        runner.RunAsync("add_workflow_state", AgentScopes.ConfigEdit, workflow, async ctx =>
            await ApplyAllAsync(ctx, [new AddWorkflowState { Workflow = workflow, State = new WorkflowState { Id = Namespaces.TenantPrefix + Slug(label), Category = category }, Label = label, Tone = category switch { "pending" => "accent.amber", "resolved" => "accent.green", "cancelled" => "accent.gray", "active" => "accent.purple", _ => "accent.blue" } }], expected_version));

    [McpServerTool(Name = "add_transition"), Description("Add a workflow transition (a button) from one or more states to another. Optionally limit it to roles, require fields, or guard it with an expression.")]
    public Task<CallToolResult> AddTransition(
        string workflow,
        [Description("Button label, a verb, e.g. \"Ask for information\".")] string label,
        [Description("State IDs it starts from.")] string[] from,
        [Description("State ID it goes to.")] string to,
        int expected_version,
        [Description("Role IDs allowed to fire it, e.g. the module's agent role. Required for AI agents: a transition anyone may fire is a permission change only an administrator can make.")] string[]? roles = null,
        [Description("Field keys that must be filled when it fires.")] string[]? required_fields = null,
        [Description("Condition that must hold, e.g. record.assignee != null.")] string? guard = null,
        string? guard_message = null,
        [Description("primary, neutral, or danger.")] string? tone = null) =>
        runner.RunAsync("add_transition", AgentScopes.ConfigEdit, workflow, async ctx =>
            await ApplyAllAsync(ctx, [new AddTransition { Workflow = workflow, Transition = new WorkflowTransition { Id = Namespaces.TenantPrefix + Slug(label), Label = label, From = [.. from], To = to, Roles = [.. roles ?? []], RequiredFields = [.. required_fields ?? []], Guard = guard, GuardMessage = guard_message, Tone = tone } }], expected_version));

    [McpServerTool(Name = "update_transition"), Description("Change a transition. Default-module transitions accept only label, guard_message, tone, enabled (disable instead of delete) and extra events to publish; your own transitions accept everything.")]
    public Task<CallToolResult> UpdateTransition(
        string workflow,
        string transition,
        int expected_version,
        string? label = null,
        bool? enabled = null,
        string? guard_message = null,
        string? tone = null,
        string[]? from = null,
        string? to = null,
        string? guard = null,
        [Description("Events to publish when the transition fires, added to the ones it already publishes.")] EventEmitInput[]? publish_events = null) =>
        runner.RunAsync("update_transition", AgentScopes.ConfigEdit, $"{workflow}/{transition}", async ctx =>
        {
            List<EventEmitSpec>? emits = null;
            if (publish_events is { Length: > 0 })
            {
                var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, default);
                var current = config.Workflows.Find(w => w.Id == workflow)?.FindTransition(transition)?.Emits ?? [];
                emits = [.. current, .. publish_events.Select(e => new EventEmitSpec { Type = e.Event, Payload = e.Payload, Severity = e.Severity })];
            }

            return await ApplyAllAsync(ctx, [new UpdateTransition { Workflow = workflow, Transition = transition, Set = new TransitionPatch { Label = label, Enabled = enabled, GuardMessage = guard_message, Tone = tone, From = from?.ToList(), To = to, Guard = guard, Emits = emits } }], expected_version);
        });

    [McpServerTool(Name = "set_workflow_policy"), Description("Decide what happens to records already in a workflow when this draft changes it: pin (default; they finish on the version they started with), migrate (move them through state_map, old state → new state), or cancel (move active ones to cancel_state). The publish review shows how many records each choice affects.")]
    public Task<CallToolResult> SetWorkflowPolicy(
        string workflow,
        [Description("pin, migrate, or cancel.")] string policy,
        int expected_version,
        [Description("migrate: old state ID → new state ID.")] Dictionary<string, string>? state_map = null,
        [Description("cancel: a cancelled-category state ID.")] string? cancel_state = null) =>
        runner.RunAsync("set_workflow_policy", AgentScopes.ConfigEdit, workflow, async ctx =>
            await ApplyAllAsync(ctx, [new SetActiveInstancePolicy { Workflow = workflow, Policy = new ActiveInstancePolicy { Policy = policy, StateMap = state_map ?? [], CancelState = cancel_state } }], expected_version));

    /// <summary>Applies a tool's operations as one change (REQ-WS-005): all or none, one version, undone together.</summary>
    private async Task<object> ApplyAllAsync(RequestContext ctx, IReadOnlyList<OverlayOp> ops, int expectedVersion)
    {
        var result = await drafts.ApplyBatchAsync(ctx, ops, expectedVersion, "mcp", default);
        return new
        {
            version = result.Version,
            steps = result.Steps.Select(s => new { step = s.Seq, s.Summary, s.Targets, warnings = s.Warnings.Count == 0 ? null : s.Warnings }),
            note = result.Steps.Count > 1 ? "These steps are one change: undoing any of them undoes all of them." : null,
        };
    }

    internal static string Slug(string label)
    {
        var s = new string(label.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (s.Contains("__", StringComparison.Ordinal))
        {
            s = s.Replace("__", "_", StringComparison.Ordinal);
        }

        return s.Length == 0 || !char.IsLetter(s[0]) ? "o_" + s : s;
    }
}
