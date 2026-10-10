using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Specs;

/// <summary>
/// The set of spec objects a layer contributes. A module release, a package, and the compiled effective
/// configuration all share this shape (layered configuration, master spec "Multi-tenancy").
/// </summary>
public class SpecCatalog
{
    public List<EntitySpec> Entities { get; set; } = [];

    public List<ChoiceListSpec> ChoiceLists { get; set; } = [];

    public List<RelationshipSpec> Relationships { get; set; } = [];

    public List<RuleSpec> Rules { get; set; } = [];

    public List<WorkflowSpec> Workflows { get; set; } = [];

    public List<ViewSpec> Views { get; set; } = [];

    public List<RoleSpec> Roles { get; set; } = [];

    public List<PermissionSetSpec> PermissionSets { get; set; } = [];

    public List<AssignmentRuleSpec> AssignmentRules { get; set; } = [];

    public List<NotificationTemplateSpec> NotificationTemplates { get; set; } = [];

    public List<ExtensionPointSpec> ExtensionPoints { get; set; } = [];

    public List<CatalogItemSpec> CatalogItems { get; set; } = [];

    public List<ApprovalPolicySpec> ApprovalPolicies { get; set; } = [];

    public List<EventTypeSpec> EventTypes { get; set; } = [];

    public EntitySpec? FindEntity(string id) => Entities.Find(e => e.Id == id);

    public ChoiceListSpec? FindChoiceList(string id) => ChoiceLists.Find(c => c.Id == id);

    public ViewSpec? FindView(string id) => Views.Find(v => v.Id == id);

    public RuleSpec? FindRule(string id) => Rules.Find(r => r.Id == id);

    public WorkflowSpec? WorkflowFor(string entityId) => Workflows.Find(w => w.Entity == entityId);

    public CatalogItemSpec? FindCatalogItem(string id) => CatalogItems.Find(c => c.Id == id);

    public ApprovalPolicySpec? FindApprovalPolicy(string id) => ApprovalPolicies.Find(p => p.Id == id);

    public EventTypeSpec? FindEventType(string id) => EventTypes.Find(e => e.Id == id);

    /// <summary>The entity whose request binding names <paramref name="taskEntityId"/> as its task entity.</summary>
    public EntitySpec? RequestEntityForTask(string taskEntityId) => Entities.Find(e => e.Request?.TaskEntity == taskEntityId);
}

/// <summary>A first-party or publisher module release: header plus catalog. Immutable once shipped.</summary>
public sealed class ModuleSpec : SpecCatalog
{
    public required string Id { get; set; }

    public required string Version { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Other modules or shared services this release needs, as semver ranges.</summary>
    public Dictionary<string, string> Dependencies { get; set; } = [];

    /// <summary>Declarative, platform-executed data migrations applied when upgrading to this version.</summary>
    public List<MigrationSpec> Migrations { get; set; } = [];

    /// <summary>Synthetic records seeded into new environments and draft previews.</summary>
    public List<SampleRecordSpec> SampleData { get; set; } = [];
}

public sealed class SampleRecordSpec
{
    public required string Entity { get; set; }

    public required string Key { get; set; }

    public JsonObject Data { get; set; } = [];

    public List<SampleActivitySpec> Activity { get; set; } = [];
}

public sealed class SampleActivitySpec
{
    public required string Kind { get; set; }

    public required string Body { get; set; }

    public string? Author { get; set; }
}
