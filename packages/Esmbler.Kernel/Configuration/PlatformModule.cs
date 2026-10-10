using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>
/// The always-installed platform layer: the tenant administrator role and its permission set. The event types the
/// platform itself publishes (record lifecycle, configuration, approvals; ADR 0017) are built in (<see cref="EventTypes"/>, ASM-051).
/// </summary>
public sealed class PlatformModule : IModuleDefinition
{
    public const string ModuleId = "core.platform";
    public const string TenantAdminRole = "core.platform.tenant_admin";

    private static readonly ModuleSpec Release = new()
    {
        Id = ModuleId,
        Version = "1.0.0",
        Name = "Platform",
        Description = "Kernel-provided roles and permissions present in every tenant.",
        Roles = [new RoleSpec { Id = TenantAdminRole, Label = "Tenant administrator", Description = "Full access within the tenant, including permission administration." }],
        PermissionSets =
        [
            new PermissionSetSpec
            {
                Id = "core.platform.ps_tenant_admin",
                Label = "Tenant administrators",
                Roles = [TenantAdminRole],
                Entities = [new EntityPermissionSpec { Entity = "*", Create = true, Read = RecordScopes.All, Update = RecordScopes.All, Delete = true }],
                Actions = [.. PlatformActions.All],
            },
        ],
    };

    private static Dictionary<string, EventFieldSpec> SlaFields() => new()
    {
        ["policy"] = Text("SLA policy ID.", required: true),
        ["target"] = Text("Target ID, e.g. response or resolution.", required: true),
        ["goal_minutes"] = new() { Type = EventFieldTypes.Number, Required = true, Description = "Goal in business or elapsed minutes." },
        ["due_at"] = Text("When the goal is (or was) due, UTC."),
        ["timer"] = Text("SLA timer ID.", required: true),
    };

    private static EventFieldSpec Text(string description, bool required = false) => new() { Type = EventFieldTypes.String, Required = required, Description = description };

    /// <summary>
    /// The event types the platform publishes (ADR 0017). They are built in rather than part of a platform release:
    /// <see cref="Composer.ComposeBase"/> adds the current list to every configuration that includes the platform, so
    /// every tenant's registry matches what the running platform publishes, whatever platform version it installed.
    /// </summary>
    public static IReadOnlyList<EventTypeSpec> EventTypes { get; } =
        [
            new EventTypeSpec { Id = Sdk.Events.EventTypes.RecordCreated, Label = "Record created", Description = "A record of any entity was created.", Fields = new() { ["changed"] = new() { Type = EventFieldTypes.List, Description = "Field keys set on create." } } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.RecordUpdated, Label = "Record updated", Description = "Fields of a record changed.", Fields = new() { ["changed"] = new() { Type = EventFieldTypes.List, Required = true, Description = "Field keys that changed." } } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.RecordTransitioned, Label = "Record changed state", Description = "A workflow transition moved a record to another state.", Fields = new() { ["from"] = Text("State before."), ["to"] = Text("State after.", required: true), ["transition"] = Text("Transition ID."), ["changed"] = new() { Type = EventFieldTypes.List } } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.RecordDeleted, Label = "Record deleted", Description = "A record was deleted (restorable for 30 days)." },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.RecordRestored, Label = "Record restored", Description = "A deleted record was restored." },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.ActivityAdded, Label = "Activity added", Description = "A reply or work note was added to a record.", Fields = new() { ["kind"] = Text("public_reply or work_note.", required: true) } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.ConfigPublished, Label = "Configuration published", Description = "A configuration revision was activated (publish, rollback, upgrade, install).", Category = "audit", Fields = new() { ["revision"] = new() { Type = EventFieldTypes.Number, Required = true }, ["source"] = Text("publish, rollback, upgrade or install."), ["content_hash"] = Text("Artifact content hash.") } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.ApprovalRequested, Label = "Approval requested", Description = "A request entered an approval stage; its approvers were asked to decide.", Severity = EventSeverities.Low, Fields = new() { ["policy"] = Text("Approval policy ID.", required: true), ["stage"] = Text("Stage ID.", required: true) } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.ApprovalApproved, Label = "Approval granted", Description = "Every stage of a request's approval was approved.", Fields = new() { ["policy"] = Text("Approval policy ID.", required: true) } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.ApprovalRejected, Label = "Approval rejected", Description = "A request's approval was rejected or expired.", Severity = EventSeverities.Medium, Fields = new() { ["policy"] = Text("Approval policy ID.", required: true), ["reason"] = Text("Why it ended.") } },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.SlaWarning, Label = "SLA at risk", Description = "An SLA target reached its warning threshold while running (ADR 0018).", Severity = EventSeverities.Medium, Fields = SlaFields() },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.SlaBreached, Label = "SLA breached", Description = "An SLA target passed its goal; the breach is recorded and stays recorded (ADR 0018).", Severity = EventSeverities.High, Fields = SlaFields() },
            new EventTypeSpec { Id = Sdk.Events.EventTypes.ApprovalCancelled, Label = "Approval cancelled", Description = "A pending approval ended because its request was cancelled.", Fields = new() { ["policy"] = Text("Approval policy ID.", required: true) } },
        ];

    public string Id => ModuleId;

    public string Name => "Platform";

    public IReadOnlyList<ModuleSpec> Releases { get; } = [Release];
}
