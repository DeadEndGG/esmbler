using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Esmbler.Sdk.Specs;

namespace Esmbler.Sdk.Overlay;

/// <summary>
/// Typed overlay operation against stable identifiers (master spec "Configuration composition").
/// Every human or AI edit is one of these (Command pattern). Update operations carry the expected
/// prior values so upgrades can distinguish compatible changes from conflicts (three-way comparison).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(AddField), "field.add")]
[JsonDerivedType(typeof(UpdateField), "field.update")]
[JsonDerivedType(typeof(HideField), "field.hide")]
[JsonDerivedType(typeof(RemoveField), "field.remove")]
[JsonDerivedType(typeof(AddChoiceList), "choice_list.add")]
[JsonDerivedType(typeof(AddChoiceOption), "choice.add")]
[JsonDerivedType(typeof(UpdateChoiceOption), "choice.update")]
[JsonDerivedType(typeof(AddRule), "rule.add")]
[JsonDerivedType(typeof(UpdateRule), "rule.update")]
[JsonDerivedType(typeof(RemoveRule), "rule.remove")]
[JsonDerivedType(typeof(InsertNode), "node.insert")]
[JsonDerivedType(typeof(UpdateNode), "node.update")]
[JsonDerivedType(typeof(MoveNode), "node.move")]
[JsonDerivedType(typeof(RemoveNode), "node.remove")]
[JsonDerivedType(typeof(SetFieldAccess), "permission.field_access")]
[JsonDerivedType(typeof(ResetElement), "element.reset")]
[JsonDerivedType(typeof(AddCatalogItem), "catalog_item.add")]
[JsonDerivedType(typeof(UpdateCatalogItem), "catalog_item.update")]
[JsonDerivedType(typeof(RemoveCatalogItem), "catalog_item.remove")]
[JsonDerivedType(typeof(AddWorkflowState), "workflow.add_state")]
[JsonDerivedType(typeof(AddTransition), "workflow.add_transition")]
[JsonDerivedType(typeof(UpdateTransition), "workflow.update_transition")]
[JsonDerivedType(typeof(SetActiveInstancePolicy), "workflow.set_active_instances")]
public abstract class OverlayOp
{
    /// <summary>Human-readable one-line summary for chat steps, diffs, and audit.</summary>
    public abstract string Describe();

    /// <summary>Stable IDs this operation creates or changes (for highlighting and dependency tracking).</summary>
    public abstract IEnumerable<string> Targets();

    /// <summary>Whether the operation changes authorization; such changes need the permissions.admin action.</summary>
    [JsonIgnore]
    public virtual bool AffectsPermissions => false;
}

/// <summary>Where to place an item among its siblings. Uses sibling IDs, never raw positions, so it survives upgrades.</summary>
public sealed class Anchor
{
    public string? Before { get; set; }

    public string? After { get; set; }

    /// <summary>"start" or "end" (default).</summary>
    public string? Position { get; set; }

    public static Anchor End { get; } = new() { Position = "end" };

    public override string ToString() =>
        Before is not null ? $"before {Before}" : After is not null ? $"after {After}" : Position ?? "end";
}

public sealed class AddField : OverlayOp
{
    public required string Entity { get; set; }

    public required FieldSpec Field { get; set; }

    public override string Describe() => $"Add {Field.Type.Replace('_', ' ')} field \"{Field.Label}\" ({Field.Key}) to {Entity}";

    public override IEnumerable<string> Targets() => [$"{Entity}/{Field.Key}"];
}

/// <summary>Patch of mutable field properties. Null members are left unchanged.</summary>
public sealed class FieldPatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? Help { get; set; }

    public bool? Required { get; set; }

    public bool? ReadOnly { get; set; }

    public int? MaxLength { get; set; }

    public int? MinLength { get; set; }

    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    public string? Pattern { get; set; }

    public string? PatternMessage { get; set; }

    public JsonNode? Default { get; set; }
}

public sealed class UpdateField : OverlayOp
{
    public required string Entity { get; set; }

    public required string Field { get; set; }

    public required FieldPatch Set { get; set; }

    /// <summary>Values of the patched properties when the change was authored.</summary>
    public JsonObject? Expected { get; set; }

    public override string Describe()
    {
        var parts = SpecJson.ToNode(Set)!.AsObject().Select(p => $"{p.Key} = {p.Value?.ToJsonString()}");
        return $"Change {Entity}/{Field}: {string.Join(", ", parts)}";
    }

    public override IEnumerable<string> Targets() => [$"{Entity}/{Field}"];
}

public sealed class HideField : OverlayOp
{
    public required string Entity { get; set; }

    public required string Field { get; set; }

    public bool Hidden { get; set; } = true;

    public override string Describe() => $"{(Hidden ? "Hide" : "Show")} field {Entity}/{Field}";

    public override IEnumerable<string> Targets() => [$"{Entity}/{Field}"];
}

/// <summary>Removes a tenant-owned field definition. Base and package fields can only be hidden.</summary>
public sealed class RemoveField : OverlayOp
{
    public required string Entity { get; set; }

    public required string Field { get; set; }

    public override string Describe() => $"Remove field {Entity}/{Field}";

    public override IEnumerable<string> Targets() => [$"{Entity}/{Field}"];
}

public sealed class AddChoiceList : OverlayOp
{
    public required ChoiceListSpec ChoiceList { get; set; }

    public override string Describe() => $"Add choice list \"{ChoiceList.Label}\" with {ChoiceList.Options.Count} options";

    public override IEnumerable<string> Targets() => [ChoiceList.Id];
}

public sealed class AddChoiceOption : OverlayOp
{
    public required string ChoiceList { get; set; }

    public required ChoiceOption Option { get; set; }

    public Anchor? Anchor { get; set; }

    public override string Describe() => $"Add option \"{Option.Label}\" to {ChoiceList}";

    public override IEnumerable<string> Targets() => [$"{ChoiceList}/{Option.Id}"];
}

public sealed class UpdateChoiceOption : OverlayOp
{
    public required string ChoiceList { get; set; }

    public required string Option { get; set; }

    public string? Label { get; set; }

    public bool? Hidden { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() =>
        Label is not null ? $"Relabel option {ChoiceList}/{Option} to \"{Label}\"" : $"{(Hidden == true ? "Hide" : "Show")} option {ChoiceList}/{Option}";

    public override IEnumerable<string> Targets() => [$"{ChoiceList}/{Option}"];
}

public sealed class AddRule : OverlayOp
{
    public required RuleSpec Rule { get; set; }

    public override string Describe() => $"Add {Rule.Scope} {Rule.Kind} rule {Rule.Id}" + (Rule.Description is null ? string.Empty : $": {Rule.Description}");

    public override IEnumerable<string> Targets() => [Rule.Id];
}

/// <summary>Replaces a rule. For base rules only <c>enabled</c> may change (tenants disable rather than rewrite base logic).</summary>
public sealed class UpdateRule : OverlayOp
{
    public required string Rule { get; set; }

    public RuleSpec? Replacement { get; set; }

    public bool? Enabled { get; set; }

    /// <summary>Content hash of the rule when the change was authored.</summary>
    public string? ExpectedHash { get; set; }

    public override string Describe() =>
        Replacement is not null ? $"Update rule {Rule}" : $"{(Enabled == false ? "Disable" : "Enable")} rule {Rule}";

    public override IEnumerable<string> Targets() => [Rule];
}

public sealed class RemoveRule : OverlayOp
{
    public required string Rule { get; set; }

    public override string Describe() => $"Remove rule {Rule}";

    public override IEnumerable<string> Targets() => [Rule];
}

public sealed class InsertNode : OverlayOp
{
    public required string View { get; set; }

    public required string Parent { get; set; }

    public required NodeSpec Node { get; set; }

    public Anchor? Anchor { get; set; }

    public override string Describe()
    {
        var title = Node.StringProp("title") ?? Node.StringProp("field") ?? Node.StringProp("text");
        return $"Insert {Node.Type}{(title is null ? string.Empty : $" \"{title}\"")} into {Parent} ({Anchor ?? Anchor.End})";
    }

    public override IEnumerable<string> Targets() => Node.Descendants().Select(n => n.Id);
}

public sealed class UpdateNode : OverlayOp
{
    public required string View { get; set; }

    public required string Node { get; set; }

    /// <summary>Props merged into the node's props (null value removes a prop).</summary>
    public required JsonObject Props { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Update {Node}: {string.Join(", ", Props.Select(p => $"{p.Key} = {p.Value?.ToJsonString() ?? "null"}"))}";

    public override IEnumerable<string> Targets() => [Node];
}

public sealed class MoveNode : OverlayOp
{
    public required string View { get; set; }

    public required string Node { get; set; }

    public required string Parent { get; set; }

    public Anchor? Anchor { get; set; }

    public override string Describe() => $"Move {Node} into {Parent} ({Anchor ?? Anchor.End})";

    public override IEnumerable<string> Targets() => [Node];
}

/// <summary>Removes a tenant node; base and package nodes are hidden instead (stays addressable for upgrades).</summary>
public sealed class RemoveNode : OverlayOp
{
    public required string View { get; set; }

    public required string Node { get; set; }

    public override string Describe() => $"Remove {Node} from {View}";

    public override IEnumerable<string> Targets() => [Node];
}

/// <summary>Sets a field's access level in a permission set. Authorization change: needs permissions.admin.</summary>
public sealed class SetFieldAccess : OverlayOp
{
    public required string PermissionSet { get; set; }

    public required string Entity { get; set; }

    public required string Field { get; set; }

    public required string Access { get; set; }

    public override bool AffectsPermissions => true;

    public override string Describe() => $"Set {Entity}/{Field} to {Access} for {PermissionSet}";

    public override IEnumerable<string> Targets() => [PermissionSet, $"{Entity}/{Field}"];
}

/// <summary>
/// Resets one element to the default modules' definition (REQ-CFG-011): every earlier overlay operation that targets
/// it (or its children, such as a choice list's options) stops applying. Elements the tenant added disappear, so
/// rules or page nodes that still use them show up as validation errors in the draft's impact preview.
/// </summary>
public sealed class ResetElement : OverlayOp
{
    /// <summary>Element key as listed in "customized", e.g. core.tickets.ticket/category or a node ID.</summary>
    public required string Target { get; set; }

    public override string Describe() => $"Reset {Target} to default";

    public override IEnumerable<string> Targets() => [Target];

    /// <summary>Whether <paramref name="op"/> is undone by this reset.</summary>
    public bool Covers(OverlayOp op) =>
        op is not ResetElement && op.Targets().Any(t => t == Target || t.StartsWith(Target + "/", StringComparison.Ordinal));
}

/// <summary>Adds a tenant catalog item with its variables and fulfillment tasks (ADR 0015).</summary>
public sealed class AddCatalogItem : OverlayOp
{
    public required CatalogItemSpec Item { get; set; }

    public override string Describe() =>
        $"Add catalog item \"{Item.Label}\" with {Item.Variables.Count} question{(Item.Variables.Count == 1 ? string.Empty : "s")} and {Item.Tasks.Count} task{(Item.Tasks.Count == 1 ? string.Empty : "s")}";

    public override IEnumerable<string> Targets() => [Item.Id];
}

/// <summary>Patch of catalog item properties. Variables and tasks can be replaced on tenant items only.</summary>
public sealed class CatalogItemPatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? Category { get; set; }

    public string? Icon { get; set; }

    public bool? Active { get; set; }

    public int? Order { get; set; }

    public List<VariableSpec>? Variables { get; set; }

    public List<FulfillmentTaskSpec>? Tasks { get; set; }
}

public sealed class UpdateCatalogItem : OverlayOp
{
    public required string Item { get; set; }

    public required CatalogItemPatch Set { get; set; }

    /// <summary>Values of the patched scalar properties when the change was authored.</summary>
    public JsonObject? Expected { get; set; }

    public override string Describe()
    {
        var parts = SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key switch
        {
            "variables" => $"questions ({p.Value!.AsArray().Count})",
            "tasks" => $"tasks ({p.Value!.AsArray().Count})",
            _ => $"{p.Key} = {p.Value?.ToJsonString()}",
        });
        return $"Change catalog item {Item}: {string.Join(", ", parts)}";
    }

    public override IEnumerable<string> Targets() => [Item];
}

/// <summary>Removes a tenant catalog item. Base items are deactivated instead (requests keep their item).</summary>
public sealed class RemoveCatalogItem : OverlayOp
{
    public required string Item { get; set; }

    public override string Describe() => $"Remove catalog item {Item}";

    public override IEnumerable<string> Targets() => [Item];
}

/// <summary>Adds a tenant state to a workflow and the matching option to its status choice list.</summary>
public sealed class AddWorkflowState : OverlayOp
{
    public required string Workflow { get; set; }

    public required WorkflowState State { get; set; }

    public required string Label { get; set; }

    /// <summary>Badge tone token for the status option, e.g. accent.blue.</summary>
    public string? Tone { get; set; }

    public Anchor? Anchor { get; set; }

    public override string Describe() => $"Add state \"{Label}\" ({State.Category}) to workflow {Workflow}";

    public override IEnumerable<string> Targets() => [$"{Workflow}/state.{State.Id}"];
}

public sealed class AddTransition : OverlayOp
{
    public required string Workflow { get; set; }

    public required WorkflowTransition Transition { get; set; }

    public override string Describe() =>
        $"Add transition \"{Transition.Label}\" from {string.Join(", ", Transition.From)} to {Transition.To} in {Workflow}";

    public override IEnumerable<string> Targets() => [$"{Workflow}/{Transition.Id}"];

    /// <summary>
    /// A transition without roles can be fired by anyone who may edit the record (requesters included), and set values
    /// write fields regardless of field permissions, so both are permission changes (permission administration).
    /// </summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Transition.Roles.Count == 0 || Transition.Set.Count > 0;
}

/// <summary>Patch of transition properties. Null members are left unchanged.</summary>
public sealed class TransitionPatch
{
    public string? Label { get; set; }

    public List<string>? From { get; set; }

    public string? To { get; set; }

    public string? Guard { get; set; }

    public string? GuardMessage { get; set; }

    public List<string>? RequiredFields { get; set; }

    public List<string>? Roles { get; set; }

    public Dictionary<string, string>? Set { get; set; }

    public string? Tone { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Changes a transition. Base transitions accept only label, guardMessage, tone and enabled.</summary>
public sealed class UpdateTransition : OverlayOp
{
    public required string Workflow { get; set; }

    public required string Transition { get; set; }

    public required TransitionPatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe()
    {
        var parts = SpecJson.ToNode(Set)!.AsObject().Select(p => $"{p.Key} = {p.Value?.ToJsonString()}");
        return $"Change transition {Workflow}/{Transition}: {string.Join(", ", parts)}";
    }

    public override IEnumerable<string> Targets() => [$"{Workflow}/{Transition}"];

    /// <summary>Changing who may fire a transition, its guard, or the values it sets is a permission change.</summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Set.Roles is not null || Set.Set is not null || Set.Guard is not null;
}

/// <summary>Sets what happens to running instances when this change publishes a new workflow version (REQ-MIG-003).</summary>
public sealed class SetActiveInstancePolicy : OverlayOp
{
    public required string Workflow { get; set; }

    public required ActiveInstancePolicy Policy { get; set; }

    public override string Describe() => Policy.Policy switch
    {
        ActiveInstancePolicies.Migrate => $"Migrate running {Workflow} instances to the new version ({string.Join(", ", Policy.StateMap.Select(m => $"{m.Key} → {m.Value}"))})",
        ActiveInstancePolicies.Cancel => $"Cancel running {Workflow} instances into '{Policy.CancelState}' when the new version publishes",
        _ => $"Keep running {Workflow} instances on the version they started with",
    };

    public override IEnumerable<string> Targets() => [$"{Workflow}/active_instances"];
}
