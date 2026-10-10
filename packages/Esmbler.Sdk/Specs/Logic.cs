using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Specs;

public static class RuleKinds
{
    public const string Validation = "validation";
    public const string Conditional = "conditional";
}

public static class RuleScopes
{
    /// <summary>Enforced on every write path: UI, API, imports, automations, AI.</summary>
    public const string Data = "data";

    /// <summary>Applies to one view only (presentation, never security).</summary>
    public const string Form = "form";
}

public static class RuleActionTypes
{
    public const string Require = "require";
    public const string Show = "show";
    public const string Hide = "hide";
    public const string ReadOnly = "read_only";
    public const string SetValue = "set_value";

    /// <summary>Publishes a domain event when the rule's condition becomes true (ADR 0017).</summary>
    public const string EmitEvent = "emit_event";
    public const string RestrictOptions = "restrict_options";
    public const string Message = "message";

    public static IReadOnlyList<string> All { get; } = [Require, Show, Hide, ReadOnly, SetValue, RestrictOptions, Message, EmitEvent];
}

public sealed class RuleSpec
{
    public required string Id { get; set; }

    public required string Kind { get; set; }

    public string Scope { get; set; } = RuleScopes.Data;

    public required string Entity { get; set; }

    /// <summary>For form-scoped rules, the view they apply to.</summary>
    public string? View { get; set; }

    public string? Description { get; set; }

    /// <summary>Condition (CEL subset). Empty means "always".</summary>
    public string? When { get; set; }

    /// <summary>Validation rules: must evaluate to true for the write to succeed.</summary>
    public string? Assert { get; set; }

    public string? Message { get; set; }

    /// <summary>Field the validation message attaches to.</summary>
    public string? Field { get; set; }

    public List<RuleActionSpec> Actions { get; set; } = [];

    /// <summary>Lower runs first. Ties break by ID.</summary>
    public int Order { get; set; } = 100;

    public bool Enabled { get; set; } = true;
}

public sealed class RuleActionSpec
{
    public required string Type { get; set; }

    public string? Field { get; set; }

    /// <summary>set_value: an expression producing the value.</summary>
    public string? Value { get; set; }

    /// <summary>restrict_options: allowed choice IDs.</summary>
    public List<string>? Options { get; set; }

    public string? Message { get; set; }

    /// <summary>message: info, warning, or critical.</summary>
    public string? Tone { get; set; }

    /// <summary>emit_event: the event to publish when the condition turns true (false before the change, true after).</summary>
    public EventEmitSpec? Emit { get; set; }
}

public static class StateCategories
{
    public const string New = "new";
    public const string Active = "active";
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";
}

public sealed class WorkflowSpec
{
    public required string Id { get; set; }

    public required string Entity { get; set; }

    public required string StateField { get; set; }

    public required string InitialState { get; set; }

    /// <summary>Monotonic definition version; active instances pin it.</summary>
    public int DefinitionVersion { get; set; } = 1;

    public List<WorkflowState> States { get; set; } = [];

    public List<WorkflowTransition> Transitions { get; set; } = [];

    /// <summary>What happens to running instances when a published change creates a new definition version (REQ-MIG-003).</summary>
    public ActiveInstancePolicy ActiveInstances { get; set; } = new();

    public WorkflowState? FindState(string id) => States.Find(s => s.Id == id);

    public WorkflowTransition? FindTransition(string id) => Transitions.Find(t => t.Id == id);
}

public static class ActiveInstancePolicies
{
    /// <summary>Instances finish on the definition version they started with.</summary>
    public const string Pin = "pin";

    /// <summary>Instances move to the new version through <see cref="ActiveInstancePolicy.StateMap"/>.</summary>
    public const string Migrate = "migrate";

    /// <summary>Active instances move to <see cref="ActiveInstancePolicy.CancelState"/>; publishing approves it.</summary>
    public const string Cancel = "cancel";

    public static IReadOnlyList<string> All { get; } = [Pin, Migrate, Cancel];
}

public sealed class ActiveInstancePolicy
{
    public string Policy { get; set; } = ActiveInstancePolicies.Pin;

    /// <summary>migrate: old state ID → new state ID. States missing here keep their ID if it still exists.</summary>
    public Dictionary<string, string> StateMap { get; set; } = [];

    /// <summary>cancel: a state in the cancelled category.</summary>
    public string? CancelState { get; set; }
}

public sealed class WorkflowState
{
    public required string Id { get; set; }

    public required string Category { get; set; }
}

public sealed class WorkflowTransition
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public List<string> From { get; set; } = [];

    public required string To { get; set; }

    public string? Guard { get; set; }

    public string? GuardMessage { get; set; }

    public List<string> RequiredFields { get; set; } = [];

    /// <summary>Roles allowed to fire it in addition to entity update permission. Empty = any editor.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Field values set when the transition fires (expressions).</summary>
    public Dictionary<string, string> Set { get; set; } = [];

    /// <summary>button tone token: primary, neutral, danger.</summary>
    public string? Tone { get; set; }

    /// <summary>Disabled transitions are not offered or accepted (tenants disable base transitions instead of removing them).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fired only by the platform (fulfillment, migrations); never offered as a button or accepted from people.</summary>
    public bool System { get; set; }

    /// <summary>Domain events published when the transition fires (ADR 0017), e.g. core.tickets.ticket.escalated.</summary>
    public List<EventEmitSpec> Emits { get; set; } = [];
}

public sealed class AssignmentRuleSpec
{
    public required string Id { get; set; }

    public required string Entity { get; set; }

    public string? Description { get; set; }

    public required string When { get; set; }

    /// <summary>Group key (resolved per tenant) to assign.</summary>
    public string? Group { get; set; }

    public int Order { get; set; } = 100;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A notification message. Legacy templates (Gate 1) carry their own <see cref="Event"/> trigger and recipients; templates
/// used by notification routes (ADR 0021) leave them empty and may add per-channel <see cref="Variants"/>.
/// </summary>
public sealed class NotificationTemplateSpec
{
    public required string Id { get; set; }

    public string? Label { get; set; }

    /// <summary>Legacy trigger, "record.transitioned:&lt;state&gt;" or "record.updated:&lt;field&gt;". Null for route templates.</summary>
    public string? Event { get; set; }

    /// <summary>Entity whose fields the placeholders read; null for templates over event fields only.</summary>
    public string? Entity { get; set; }

    /// <summary>Legacy recipients: field keys (user fields) or the literal "watchers".</summary>
    public List<string> Recipients { get; set; } = [];

    public required string Subject { get; set; }

    /// <summary>
    /// Plain text with placeholders: {{record.field}}, {{event.field}}, {{recipient.name}} and {{link}}. Never raw HTML.
    /// Each recipient sees only fields they may read.
    /// </summary>
    public required string Body { get; set; }

    /// <summary>Per-channel text (in_app, email, sms); channels without a variant use Subject and Body.</summary>
    public Dictionary<string, TemplateVariantSpec> Variants { get; set; } = [];

    /// <summary>Record fields external recipients may see; everything else renders empty for them.</summary>
    public List<string> ExternalFields { get; set; } = [];

    public bool Enabled { get; set; } = true;
}

public sealed class ExtensionPointSpec
{
    public required string Id { get; set; }

    /// <summary>lifecycle_hook, strategy, schema_extension, ui_slot, workflow_step.</summary>
    public required string Kind { get; set; }

    public required string Version { get; set; }

    /// <summary>multiple, ordered_pipeline, single_provider.</summary>
    public required string Cardinality { get; set; }

    public string? Description { get; set; }

    public JsonObject? Contract { get; set; }
}

public sealed class MigrationSpec
{
    public required string Id { get; set; }

    /// <summary>backfill (set empty field from expression) or convert (type conversion source → target).</summary>
    public required string Kind { get; set; }

    public required string Entity { get; set; }

    public required string Field { get; set; }

    /// <summary>backfill: expression producing the value.</summary>
    public string? Value { get; set; }

    /// <summary>convert: source field whose values are converted into <see cref="Field"/>; source values are preserved.</summary>
    public string? Source { get; set; }

    public string? Description { get; set; }

    public int BatchSize { get; set; } = 500;
}
