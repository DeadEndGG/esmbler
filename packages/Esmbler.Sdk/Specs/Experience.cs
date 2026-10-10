using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Specs;

public static class ViewKinds
{
    public const string Record = "record";
    public const string List = "list";
    public const string Form = "form";
}

public sealed class ViewSpec
{
    public required string Id { get; set; }

    public required string Kind { get; set; }

    public required string Entity { get; set; }

    public required string Label { get; set; }

    /// <summary>Layout tree root; always a <c>layout.page</c> node.</summary>
    public required NodeSpec Layout { get; set; }
}

/// <summary>
/// A node in a page layout tree (Composite pattern). <see cref="Type"/> is a registry element ID;
/// <see cref="Props"/> is validated against that element's props schema. IDs are stable across versions.
/// </summary>
public sealed class NodeSpec
{
    public required string Id { get; set; }

    public required string Type { get; set; }

    public JsonObject Props { get; set; } = [];

    public List<NodeSpec> Children { get; set; } = [];

    /// <summary>Hidden by an overlay (base nodes are hidden rather than deleted).</summary>
    public bool Hidden { get; set; }

    public IEnumerable<NodeSpec> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    public NodeSpec? Find(string id) => Descendants().FirstOrDefault(n => n.Id == id);

    public NodeSpec? ParentOf(string id) => Descendants().FirstOrDefault(n => n.Children.Exists(c => c.Id == id));

    public string? StringProp(string name) => Props.TryGetPropertyValue(name, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
}

public sealed class RoleSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }
}

public static class RecordScopes
{
    public const string None = "none";
    public const string Related = "related";
    public const string All = "all";
}

public static class FieldAccessLevels
{
    public const string Hidden = "hidden";
    public const string Read = "read";
    public const string Write = "write";
}

public static class PlatformActions
{
    public const string ConfigEdit = "config.edit";
    public const string ConfigPublish = "config.publish";
    public const string PermissionsAdmin = "permissions.admin";
    public const string AuditRead = "audit.read";
    public const string RecordsExport = "records.export";
    public const string ModulesManage = "modules.manage";

    public static IReadOnlyList<string> All { get; } = [ConfigEdit, ConfigPublish, PermissionsAdmin, AuditRead, RecordsExport, ModulesManage];
}

public sealed class PermissionSetSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public List<string> Roles { get; set; } = [];

    public List<EntityPermissionSpec> Entities { get; set; } = [];

    public List<FieldPermissionSpec> Fields { get; set; } = [];

    public List<string> Actions { get; set; } = [];
}

public sealed class EntityPermissionSpec
{
    public required string Entity { get; set; }

    public bool Create { get; set; }

    public string Read { get; set; } = RecordScopes.None;

    public string Update { get; set; } = RecordScopes.None;

    public bool Delete { get; set; }

    /// <summary>Optional ABAC condition (CEL subset over record/user) further restricting update.</summary>
    public string? UpdateCondition { get; set; }
}

public sealed class FieldPermissionSpec
{
    public required string Entity { get; set; }

    /// <summary>Field key or "*" for all fields of the entity.</summary>
    public required string Field { get; set; }

    public required string Access { get; set; }
}
