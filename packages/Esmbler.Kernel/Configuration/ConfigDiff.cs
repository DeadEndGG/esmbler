using System.Text.Json.Nodes;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

public sealed record ConfigChange(string Change, string Kind, string Id, string Label, IReadOnlyList<string> Properties);

/// <summary>Element-level diff between two configurations, keyed by stable IDs (never by array position).</summary>
public static class ConfigDiff
{
    public static IReadOnlyList<ConfigChange> Compare(SpecCatalog before, SpecCatalog after)
    {
        var changes = new List<ConfigChange>();
        Diff(changes, "entity", before.Entities, after.Entities, e => e.Id, e => e.Label, e => Without(e, "fields"));
        foreach (var entity in after.Entities)
        {
            var old = before.FindEntity(entity.Id);
            Diff(changes, "field", old?.Fields ?? [], entity.Fields, f => $"{entity.Id}/{f.Key}", f => f.Label, f => SpecJson.ToNode(f)!.AsObject());
        }

        Diff(changes, "choice_list", before.ChoiceLists, after.ChoiceLists, c => c.Id, c => c.Label, c => SpecJson.ToNode(c)!.AsObject());
        Diff(changes, "relationship", before.Relationships, after.Relationships, r => r.Id, r => r.Label, r => SpecJson.ToNode(r)!.AsObject());
        Diff(changes, "rule", before.Rules, after.Rules, r => r.Id, r => r.Description ?? r.Id, r => SpecJson.ToNode(r)!.AsObject());
        Diff(changes, "workflow", before.Workflows, after.Workflows, w => w.Id, w => w.Id, w => SpecJson.ToNode(w)!.AsObject());
        Diff(changes, "role", before.Roles, after.Roles, r => r.Id, r => r.Label, r => SpecJson.ToNode(r)!.AsObject());
        Diff(changes, "permission_set", before.PermissionSets, after.PermissionSets, p => p.Id, p => p.Label, p => SpecJson.ToNode(p)!.AsObject());
        Diff(changes, "assignment_rule", before.AssignmentRules, after.AssignmentRules, a => a.Id, a => a.Description ?? a.Id, a => SpecJson.ToNode(a)!.AsObject());
        Diff(changes, "notification_template", before.NotificationTemplates, after.NotificationTemplates, n => n.Id, n => n.Subject, n => SpecJson.ToNode(n)!.AsObject());
        foreach (var view in after.Views)
        {
            var oldView = before.FindView(view.Id);
            var oldNodes = oldView?.Layout.Descendants().ToList() ?? [];
            var oldParents = oldView is null ? [] : ParentMap(oldView.Layout);
            var newParents = ParentMap(view.Layout);
            Diff(changes, "node", oldNodes, view.Layout.Descendants().ToList(), n => n.Id, Label, n =>
            {
                var o = Without(n, "children");
                o["parent"] = newParents.GetValueOrDefault(n.Id) ?? oldParents.GetValueOrDefault(n.Id);
                return o;
            }, n => new JsonObject { ["parent"] = oldParents.GetValueOrDefault(n.Id) });
        }

        foreach (var removedView in before.Views.Where(v => after.FindView(v.Id) is null))
        {
            changes.Add(new ConfigChange("removed", "view", removedView.Id, removedView.Label, []));
        }

        return changes;
    }

    private static string Label(NodeSpec n) => n.StringProp("title") ?? n.StringProp("field") ?? n.StringProp("text") ?? n.Type;

    private static Dictionary<string, string> ParentMap(NodeSpec root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in root.Descendants())
        {
            foreach (var c in node.Children)
            {
                map[c.Id] = node.Id;
            }
        }

        return map;
    }

    private static JsonObject Without<T>(T value, string property)
    {
        var o = SpecJson.ToNode(value)!.AsObject();
        o.Remove(property);
        return o;
    }

    private static void Diff<T>(List<ConfigChange> changes, string kind, IEnumerable<T> before, IEnumerable<T> after, Func<T, string> id, Func<T, string> label, Func<T, JsonObject> shape, Func<T, JsonObject>? oldExtra = null)
    {
        var old = before.GroupBy(id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in after)
        {
            var key = id(item);
            seen.Add(key);
            if (!old.TryGetValue(key, out var prev))
            {
                changes.Add(new ConfigChange("added", kind, key, label(item), []));
                continue;
            }

            var a = shape(prev);
            if (oldExtra is not null)
            {
                foreach (var (k, v) in oldExtra(prev))
                {
                    a[k] = v?.DeepClone();
                }
            }

            var b = shape(item);
            var props = a.Select(p => p.Key).Union(b.Select(p => p.Key)).Where(k => !JsonNode.DeepEquals(a[k], b[k])).Order(StringComparer.Ordinal).ToList();
            if (props.Count > 0)
            {
                changes.Add(new ConfigChange("changed", kind, key, label(item), props));
            }
        }

        foreach (var (key, prev) in old.Where(o => !seen.Contains(o.Key)))
        {
            changes.Add(new ConfigChange("removed", kind, key, label(prev), []));
        }
    }
}
