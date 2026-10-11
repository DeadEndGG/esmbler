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
[JsonDerivedType(typeof(AddView), "view.add")]
[JsonDerivedType(typeof(RemoveView), "view.remove")]
[JsonDerivedType(typeof(SetFieldAccess), "permission.field_access")]
[JsonDerivedType(typeof(ResetElement), "element.reset")]
[JsonDerivedType(typeof(AddCatalogItem), "catalog_item.add")]
[JsonDerivedType(typeof(UpdateCatalogItem), "catalog_item.update")]
[JsonDerivedType(typeof(RemoveCatalogItem), "catalog_item.remove")]
[JsonDerivedType(typeof(AddWorkflowState), "workflow.add_state")]
[JsonDerivedType(typeof(AddTransition), "workflow.add_transition")]
[JsonDerivedType(typeof(UpdateTransition), "workflow.update_transition")]
[JsonDerivedType(typeof(SetActiveInstancePolicy), "workflow.set_active_instances")]
[JsonDerivedType(typeof(AddApprovalPolicy), "approval_policy.add")]
[JsonDerivedType(typeof(UpdateApprovalPolicy), "approval_policy.update")]
[JsonDerivedType(typeof(RemoveApprovalPolicy), "approval_policy.remove")]
[JsonDerivedType(typeof(AddEventType), "event_type.add")]
[JsonDerivedType(typeof(RemoveEventType), "event_type.remove")]
[JsonDerivedType(typeof(AddBusinessCalendar), "business_calendar.add")]
[JsonDerivedType(typeof(UpdateBusinessCalendar), "business_calendar.update")]
[JsonDerivedType(typeof(RemoveBusinessCalendar), "business_calendar.remove")]
[JsonDerivedType(typeof(AddSlaPolicy), "sla_policy.add")]
[JsonDerivedType(typeof(UpdateSlaPolicy), "sla_policy.update")]
[JsonDerivedType(typeof(RemoveSlaPolicy), "sla_policy.remove")]
[JsonDerivedType(typeof(AddMetric), "metric.add")]
[JsonDerivedType(typeof(UpdateMetric), "metric.update")]
[JsonDerivedType(typeof(RemoveMetric), "metric.remove")]
[JsonDerivedType(typeof(AddSubscription), "subscription.add")]
[JsonDerivedType(typeof(UpdateSubscription), "subscription.update")]
[JsonDerivedType(typeof(RemoveSubscription), "subscription.remove")]
[JsonDerivedType(typeof(AddNotificationTemplate), "notification_template.add")]
[JsonDerivedType(typeof(UpdateNotificationTemplate), "notification_template.update")]
[JsonDerivedType(typeof(RemoveNotificationTemplate), "notification_template.remove")]
[JsonDerivedType(typeof(AddNotificationProfile), "notification_profile.add")]
[JsonDerivedType(typeof(UpdateNotificationProfile), "notification_profile.update")]
[JsonDerivedType(typeof(RemoveNotificationProfile), "notification_profile.remove")]
[JsonDerivedType(typeof(AddNotificationRoute), "notification_route.add")]
[JsonDerivedType(typeof(UpdateNotificationRoute), "notification_route.update")]
[JsonDerivedType(typeof(RemoveNotificationRoute), "notification_route.remove")]
[JsonDerivedType(typeof(AddAlertRule), "alert_rule.add")]
[JsonDerivedType(typeof(UpdateAlertRule), "alert_rule.update")]
[JsonDerivedType(typeof(RemoveAlertRule), "alert_rule.remove")]
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

    /// <summary>Approval policy ID; an empty string removes the item's approval (ADR 0016).</summary>
    public string? ApprovalPolicy { get; set; }
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

    /// <summary>
    /// Choosing which policy approves an item's requests decides who must say yes (a weaker policy is as good as none),
    /// so any change of an item's approval policy is a permission change (ADR 0016).
    /// </summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Set.ApprovalPolicy is not null;
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

    /// <summary>Domain events the transition publishes (replaces the list); allowed on base transitions too (ADR 0017).</summary>
    public List<EventEmitSpec>? Emits { get; set; }
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

/// <summary>Adds a tenant approval policy (ADR 0016). It takes effect once a catalog item names it.</summary>
public sealed class AddApprovalPolicy : OverlayOp
{
    public required ApprovalPolicySpec Policy { get; set; }

    public override string Describe() =>
        $"Add approval policy \"{Policy.Label}\" with {Policy.Stages.Count} stage(s): {string.Join(" → ", Policy.Stages.Select(s => s.Label))}";

    public override IEnumerable<string> Targets() => [Policy.Id];
}

/// <summary>Patch of an approval policy. Null members are left unchanged; stages replace the whole list.</summary>
public sealed class ApprovalPolicyPatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public List<ApprovalStageSpec>? Stages { get; set; }
}

/// <summary>Changes an approval policy. Base policies accept only label and description. Approvals in flight keep their snapshot.</summary>
public sealed class UpdateApprovalPolicy : OverlayOp
{
    public required string Policy { get; set; }

    public required ApprovalPolicyPatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() =>
        Set.Stages is { } stages
            ? $"Change approval policy {Policy}: stages {string.Join(" → ", stages.Select(s => s.Label))}"
            : $"Change approval policy {Policy}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => $"{p.Key} = {p.Value?.ToJsonString()}"))}";

    public override IEnumerable<string> Targets() => [Policy];

    /// <summary>Changing stages changes who approves: a permission change.</summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Set.Stages is not null;
}

/// <summary>Removes a tenant approval policy that no catalog item uses.</summary>
public sealed class RemoveApprovalPolicy : OverlayOp
{
    public required string Policy { get; set; }

    public override string Describe() => $"Remove approval policy {Policy}";

    public override IEnumerable<string> Targets() => [Policy];

    [JsonIgnore]
    public override bool AffectsPermissions => true;
}

/// <summary>Adds a tenant event type (ADR 0017) that tenant rules and transitions can publish.</summary>
public sealed class AddEventType : OverlayOp
{
    public required EventTypeSpec EventType { get; set; }

    public override string Describe() => $"Add event type \"{EventType.Label}\" ({EventType.Id}) with {EventType.Fields.Count} payload field(s)";

    public override IEnumerable<string> Targets() => [EventType.Id];
}

/// <summary>Removes a tenant event type that nothing publishes or subscribes to.</summary>
public sealed class RemoveEventType : OverlayOp
{
    public required string EventType { get; set; }

    public override string Describe() => $"Remove event type {EventType}";

    public override IEnumerable<string> Targets() => [EventType];
}

/// <summary>Adds a tenant business calendar (ADR 0018).</summary>
public sealed class AddBusinessCalendar : OverlayOp
{
    public required BusinessCalendarSpec Calendar { get; set; }

    public override string Describe() => $"Add business calendar \"{Calendar.Label}\" ({Calendar.TimeZone}, {Calendar.Week.Count} working interval(s), {Calendar.Holidays.Count} holiday(s))";

    public override IEnumerable<string> Targets() => [Calendar.Id];
}

/// <summary>Changes a business calendar, the module's or the tenant's. Running SLA timers keep the calendar they started with.</summary>
public sealed class UpdateBusinessCalendar : OverlayOp
{
    public required string Calendar { get; set; }

    public required BusinessCalendarPatch Set { get; set; }

    /// <summary>Values the tenant saw when making the change; an upgrade that changed them reports a conflict.</summary>
    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change business calendar {Calendar}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Calendar];
}

public sealed class BusinessCalendarPatch
{
    public string? Label { get; set; }

    public string? TimeZone { get; set; }

    public List<WorkingHoursSpec>? Week { get; set; }

    public List<HolidaySpec>? Holidays { get; set; }
}

/// <summary>Removes a tenant business calendar that no SLA policy uses.</summary>
public sealed class RemoveBusinessCalendar : OverlayOp
{
    public required string Calendar { get; set; }

    public override string Describe() => $"Remove business calendar {Calendar}";

    public override IEnumerable<string> Targets() => [Calendar];
}

/// <summary>Adds a tenant SLA policy (ADR 0018).</summary>
public sealed class AddSlaPolicy : OverlayOp
{
    public required SlaPolicySpec Policy { get; set; }

    public override string Describe() => $"Add SLA policy \"{Policy.Label}\" on {Policy.Entity}: {string.Join(", ", Policy.Targets.Select(t => t.Label))}";

    public override IEnumerable<string> Targets() => [Policy.Id];
}

/// <summary>Changes an SLA policy, the module's or the tenant's: label, condition, enabled, or targets (goals, calendars, conditions). Running timers keep the snapshot they started with.</summary>
public sealed class UpdateSlaPolicy : OverlayOp
{
    public required string Policy { get; set; }

    public required SlaPolicyPatch Set { get; set; }

    /// <summary>Values the tenant saw when making the change; an upgrade that changed them reports a conflict.</summary>
    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change SLA policy {Policy}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Policy];
}

public sealed class SlaPolicyPatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? When { get; set; }

    public bool? Enabled { get; set; }

    public List<SlaTargetSpec>? Targets { get; set; }
}

/// <summary>Removes a tenant SLA policy; running timers of it finish on their snapshot.</summary>
public sealed class RemoveSlaPolicy : OverlayOp
{
    public required string Policy { get; set; }

    public override string Describe() => $"Remove SLA policy {Policy}";

    public override IEnumerable<string> Targets() => [Policy];
}

/// <summary>
/// Adds a tenant dashboard (ADR 0019, REQ-WS-005): a view of kind <c>dashboard</c> whose layout holds layout and display
/// nodes only. Its ID and every node ID start with <c>tenant__</c>. Other view kinds come from modules.
/// </summary>
public sealed class AddView : OverlayOp
{
    public required ViewSpec View { get; set; }

    public override string Describe() => $"Add {View.Kind} \"{View.Label}\"";

    public override IEnumerable<string> Targets() => [View.Id, .. View.Layout.Descendants().Select(n => n.Id)];
}

/// <summary>Removes a dashboard this workspace added.</summary>
public sealed class RemoveView : OverlayOp
{
    public required string View { get; set; }

    public override string Describe() => $"Remove view {View}";

    public override IEnumerable<string> Targets() => [View];
}

/// <summary>Adds a tenant metric (ADR 0019). Metrics only count what each viewer can already read.</summary>
public sealed class AddMetric : OverlayOp
{
    public required MetricSpec Metric { get; set; }

    public override string Describe() => $"Add metric \"{Metric.Label}\" ({Metric.Measure.Kind} of {Metric.Entity}{(Metric.GroupBy is null ? string.Empty : $" by {Metric.GroupBy.Field}")})";

    public override IEnumerable<string> Targets() => [Metric.Id];
}

/// <summary>Changes a metric, the module's or the tenant's.</summary>
public sealed class UpdateMetric : OverlayOp
{
    public required string Metric { get; set; }

    public required MetricPatch Set { get; set; }

    /// <summary>Values the tenant saw when making the change; an upgrade that changed them reports a conflict.</summary>
    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change metric {Metric}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    /// <summary>Who sees a figure is an access decision (roles never widen record access, but they gate the figure).</summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Set.Roles is not null;

    public override IEnumerable<string> Targets() => [Metric];
}

public sealed class MetricPatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? Filter { get; set; }

    public MetricMeasureSpec? Measure { get; set; }

    public MetricGroupSpec? GroupBy { get; set; }

    public MetricDateRangeSpec? DateRange { get; set; }

    public MetricSlaSpec? Sla { get; set; }

    /// <summary>Makes it an alert metric, or changes which alerts it counts.</summary>
    public MetricAlertSpec? Alerts { get; set; }

    public List<string>? Roles { get; set; }

    public string? Format { get; set; }
}

/// <summary>Removes a tenant metric that no dashboard or saved view uses.</summary>
public sealed class RemoveMetric : OverlayOp
{
    public required string Metric { get; set; }

    public override string Describe() => $"Remove metric {Metric}";

    public override IEnumerable<string> Targets() => [Metric];
}

/// <summary>Adds a tenant subscription: which events the notification service hears about (ADR 0021).</summary>
public sealed class AddSubscription : OverlayOp
{
    public required SubscriptionSpec Subscription { get; set; }

    public override string Describe() => $"Add subscription \"{Subscription.Label}\" to {Subscription.Event}";

    public override IEnumerable<string> Targets() => [Subscription.Id];
}

/// <summary>Changes a subscription's label, condition or enabled state, the module's or the tenant's.</summary>
public sealed class UpdateSubscription : OverlayOp
{
    public required string Subscription { get; set; }

    public required SubscriptionPatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change subscription {Subscription}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Subscription];
}

public sealed class SubscriptionPatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? When { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Removes a tenant subscription that no route uses.</summary>
public sealed class RemoveSubscription : OverlayOp
{
    public required string Subscription { get; set; }

    public override string Describe() => $"Remove subscription {Subscription}";

    public override IEnumerable<string> Targets() => [Subscription];
}

/// <summary>Adds a tenant notification template.</summary>
public sealed class AddNotificationTemplate : OverlayOp
{
    public required NotificationTemplateSpec Template { get; set; }

    public override string Describe() => $"Add notification template \"{Template.Label ?? Template.Subject}\"";

    public override IEnumerable<string> Targets() => [Template.Id];

    /// <summary>What external recipients may see is an access decision.</summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Template.ExternalFields.Count > 0;
}

/// <summary>Changes a template's text, per-channel variants, external fields or enabled state.</summary>
public sealed class UpdateNotificationTemplate : OverlayOp
{
    public required string Template { get; set; }

    public required NotificationTemplatePatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change notification template {Template}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Template];

    [JsonIgnore]
    public override bool AffectsPermissions => Set.ExternalFields is not null;
}

public sealed class NotificationTemplatePatch
{
    public string? Label { get; set; }

    public string? Subject { get; set; }

    public string? Body { get; set; }

    public Dictionary<string, TemplateVariantSpec>? Variants { get; set; }

    public List<string>? ExternalFields { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Removes a tenant template that no route uses.</summary>
public sealed class RemoveNotificationTemplate : OverlayOp
{
    public required string Template { get; set; }

    public override string Describe() => $"Remove notification template {Template}";

    public override IEnumerable<string> Targets() => [Template];
}

/// <summary>Adds a tenant notification profile (channels and severity filter).</summary>
public sealed class AddNotificationProfile : OverlayOp
{
    public required NotificationProfileSpec Profile { get; set; }

    public override string Describe() => $"Add notification profile \"{Profile.Label}\" ({string.Join(", ", Profile.Channels)})";

    public override IEnumerable<string> Targets() => [Profile.Id];
}

public sealed class UpdateNotificationProfile : OverlayOp
{
    public required string Profile { get; set; }

    public required NotificationProfilePatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change notification profile {Profile}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Profile];
}

public sealed class NotificationProfilePatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public List<string>? Channels { get; set; }

    public string? Delivery { get; set; }

    public string? MinSeverity { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Removes a tenant profile that no route uses.</summary>
public sealed class RemoveNotificationProfile : OverlayOp
{
    public required string Profile { get; set; }

    public override string Describe() => $"Remove notification profile {Profile}";

    public override IEnumerable<string> Targets() => [Profile];
}

/// <summary>Adds a tenant route: who hears about a subscription's events, how, and with which template.</summary>
public sealed class AddNotificationRoute : OverlayOp
{
    public required NotificationRouteSpec Route { get; set; }

    public override string Describe() => $"Add notification route \"{Route.Label}\" for {Route.Subscription}";

    public override IEnumerable<string> Targets() => [Route.Id];

    /// <summary>Mandatory routes and external recipients are access decisions (REQ-AUTHZ-008).</summary>
    [JsonIgnore]
    public override bool AffectsPermissions => Route.Mandatory || Route.Recipients.Any(RecipientKinds.IsExternal);
}

/// <summary>
/// Changes a route. Module routes accept enabled (not for mandatory ones), profile and template; tenant routes accept
/// everything. Making a route mandatory or not, or adding external recipients, is a permission change.
/// </summary>
public sealed class UpdateNotificationRoute : OverlayOp
{
    public required string Route { get; set; }

    public required NotificationRoutePatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change notification route {Route}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Route];

    [JsonIgnore]
    public override bool AffectsPermissions => Set.Mandatory is not null || Set.Recipients?.Any(RecipientKinds.IsExternal) == true;
}

public sealed class NotificationRoutePatch
{
    public string? Label { get; set; }

    public string? Profile { get; set; }

    public string? Template { get; set; }

    public List<RecipientSpec>? Recipients { get; set; }

    public bool? Mandatory { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Removes a tenant route. A mandatory route is made optional first (a permission change).</summary>
public sealed class RemoveNotificationRoute : OverlayOp
{
    public required string Route { get; set; }

    public override string Describe() => $"Remove notification route {Route}";

    public override IEnumerable<string> Targets() => [Route];
}

/// <summary>Adds a tenant alert rule (ADR 0022).</summary>
public sealed class AddAlertRule : OverlayOp
{
    public required AlertRuleSpec Rule { get; set; }

    public override string Describe() => $"Add alert rule \"{Rule.Label}\" on {Rule.Trigger}";

    public override IEnumerable<string> Targets() => [Rule.Id];
}

/// <summary>
/// Changes an alert rule. Module rules keep their trigger and deduplication key; everything else may change. Silencing a
/// rule behind a mandatory alert route is caught as a permission change at publish (<c>PermissionDiff</c>).
/// </summary>
public sealed class UpdateAlertRule : OverlayOp
{
    public required string Rule { get; set; }

    public required AlertRulePatch Set { get; set; }

    public JsonObject? Expected { get; set; }

    public override string Describe() => $"Change alert rule {Rule}: {string.Join(", ", SpecJson.ToNode(Set)!.AsObject().Select(p => p.Key))}";

    public override IEnumerable<string> Targets() => [Rule];
}

public sealed class AlertRulePatch
{
    public string? Label { get; set; }

    public string? Description { get; set; }

    public string? Trigger { get; set; }

    public string? MinSeverity { get; set; }

    public string? Severity { get; set; }

    public string? DedupKey { get; set; }

    public string? Title { get; set; }

    public AlertOwnerSpec? Owner { get; set; }

    public List<AlertEscalationStepSpec>? Escalation { get; set; }

    public List<string>? ResolveOn { get; set; }

    public int? ReopenWithinMinutes { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Removes a tenant alert rule. Its open alerts stay and are resolved by people.</summary>
public sealed class RemoveAlertRule : OverlayOp
{
    public required string Rule { get; set; }

    public override string Describe() => $"Remove alert rule {Rule}";

    public override IEnumerable<string> Targets() => [Rule];
}
