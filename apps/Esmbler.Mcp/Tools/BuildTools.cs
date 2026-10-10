using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [Description("require, show, hide, read_only, set_value, restrict_options, or message")]
    public required string Type { get; set; }

    [Description("Field key the action applies to (not needed for message).")]
    public string? Field { get; set; }

    [Description("set_value: expression for the value, e.g. 'high' (quoted text) or record.priority.")]
    public string? Value { get; set; }

    [Description("restrict_options: allowed option IDs.")]
    public List<string>? Options { get; set; }

    [Description("message: text to show.")]
    public string? Message { get; set; }
}

/// <summary>
/// Build tools (REQ-AI-003). Every change is a Command appended to the open draft and appears on the live page
/// within a second. Pass the draft version you last saw as expected_version; a newer draft is reported as a conflict.
/// Tools never publish.
/// </summary>
[McpServerToolType]
public sealed class BuildTools(ToolRunner runner, DraftService drafts)
{
    [McpServerTool(Name = "open_draft"), Description("Open (or join) the draft for this workspace. Returns its version. Everyone else keeps seeing the published pages until a person publishes.")]
    public Task<CallToolResult> OpenDraft() =>
        runner.RunAsync("open_draft", AgentScopes.ConfigEdit, null, async ctx => await drafts.OpenAsync(ctx, default));

    [McpServerTool(Name = "add_field"), Description("Add a field to an entity and optionally place it on a page card. For single_select/multi_select give choices (labels) and a choice list is created. Returns the new draft version and the step numbers.")]
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
        [Description("Optional: place after this sibling node ID.")] string? after = null) =>
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

            return await ApplyAllAsync(ctx, ops, expected_version);
        });

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

    [McpServerTool(Name = "add_rule"), Description("Add a rule. Conditional: when <expression> then actions (require, show, hide, read_only, set_value, restrict_options, message). Validation: assert <expression> with a message. Expressions use record.<field_key>, e.g. record.category == 'access'. Data-scope rules apply on every form and the API.")]
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
                Actions = (actions ?? []).Select(a => new RuleActionSpec { Type = a.Type, Field = a.Field, Value = a.Value, Options = a.Options, Message = a.Message }).ToList(),
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

    private async Task<object> ApplyAllAsync(RequestContext ctx, IReadOnlyList<OverlayOp> ops, int expectedVersion)
    {
        var version = expectedVersion;
        var steps = new List<object>();
        foreach (var op in ops)
        {
            var result = await drafts.ApplyAsync(ctx, op, version, "mcp", default);
            version = result.Version;
            steps.Add(new { step = result.Seq, result.Summary, result.Targets, warnings = result.Warnings.Count == 0 ? null : result.Warnings });
        }

        return new { version, steps };
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
