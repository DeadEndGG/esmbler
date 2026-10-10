using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

public sealed class CompositionResult
{
    public required EffectiveConfig Config { get; init; }

    public required IReadOnlyList<OpOutcome> Outcomes { get; init; }

    public IEnumerable<Diagnostic> Diagnostics => Outcomes.SelectMany(o => o.Diagnostics);

    public bool HasConflicts => Outcomes.Any(o => o.Status is OpStatus.Conflict or OpStatus.Failed);
}

/// <summary>
/// Layered configuration (overlay pattern): applies typed overlay operations, in order, on top of the
/// composed base modules. Never blind-merges JSON; every operation targets stable identifiers and
/// declares the prior values it expects so base changes are detected as conflicts.
/// </summary>
public static class Composer
{
    public const string TenantLayer = "tenant";

    public static CompositionResult Compose(IReadOnlyList<ModuleSpec> modules, IReadOnlyList<OverlayOp> ops)
    {
        var config = ComposeBase(modules);
        var outcomes = new List<OpOutcome>(ops.Count);
        var resetBy = ResetIndexes(ops);
        for (var i = 0; i < ops.Count; i++)
        {
            if (resetBy[i] is not null)
            {
                outcomes.Add(new OpOutcome(i, OpName(ops[i]), ops[i].Describe(), OpStatus.Reset, []));
                continue;
            }

            var ctx = new OpContext(config, i) { ResetSomething = Array.IndexOf(resetBy, i) >= 0 };
            try
            {
                Apply(ctx, ops[i]);
            }
            catch (OverlayException ex)
            {
                ctx.Fail(ex.Code, ex.Message, ex.Target, ex.Suggestion);
            }

            outcomes.Add(new OpOutcome(i, OpName(ops[i]), ops[i].Describe(), ctx.Status, ctx.Diagnostics));
        }

        config.Customized.Sort(StringComparer.Ordinal);
        return new CompositionResult { Config = config, Outcomes = outcomes };
    }

    /// <summary>For each operation, the index of a later element.reset that undoes it (null when it applies).</summary>
    private static int?[] ResetIndexes(IReadOnlyList<OverlayOp> ops)
    {
        var result = new int?[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is not ResetElement reset)
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (result[j] is null && reset.Covers(ops[j]))
                {
                    result[j] = i;
                }
            }
        }

        return result;
    }

    public static EffectiveConfig ComposeBase(IReadOnlyList<ModuleSpec> modules)
    {
        var config = new EffectiveConfig();
        foreach (var module in modules.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var layer = $"{module.Id}@{module.Version}";
            var copy = SpecJson.Clone(module);
            config.Modules.Add(new LockedModule(module.Id, module.Version, Hash(SpecJson.Canonical(SpecJson.ToNode(module)))));
            config.Entities.AddRange(copy.Entities);
            config.ChoiceLists.AddRange(copy.ChoiceLists);
            config.Relationships.AddRange(copy.Relationships);
            config.Rules.AddRange(copy.Rules);
            config.Workflows.AddRange(copy.Workflows);
            config.Views.AddRange(copy.Views);
            config.Roles.AddRange(copy.Roles);
            config.PermissionSets.AddRange(copy.PermissionSets);
            config.AssignmentRules.AddRange(copy.AssignmentRules);
            config.NotificationTemplates.AddRange(copy.NotificationTemplates);
            config.ExtensionPoints.AddRange(copy.ExtensionPoints);
            config.CatalogItems.AddRange(copy.CatalogItems);
            foreach (var key in ElementKeys(copy))
            {
                config.Provenance[key] = layer;
            }
        }

        return config;
    }

    internal static IEnumerable<string> ElementKeys(SpecCatalog catalog)
    {
        foreach (var e in catalog.Entities)
        {
            yield return e.Id;
            foreach (var f in e.Fields)
            {
                yield return $"{e.Id}/{f.Key}";
            }
        }

        foreach (var c in catalog.ChoiceLists)
        {
            yield return c.Id;
            foreach (var o in c.Options)
            {
                yield return $"{c.Id}/{o.Id}";
            }
        }

        foreach (var r in catalog.Rules)
        {
            yield return r.Id;
        }

        foreach (var v in catalog.Views)
        {
            yield return v.Id;
            foreach (var n in v.Layout.Descendants())
            {
                yield return n.Id;
            }
        }

        foreach (var w in catalog.Workflows)
        {
            yield return w.Id;
            foreach (var st in w.States)
            {
                yield return $"{w.Id}/state.{st.Id}";
            }

            foreach (var t in w.Transitions)
            {
                yield return $"{w.Id}/{t.Id}";
            }
        }

        foreach (var c in catalog.CatalogItems)
        {
            yield return c.Id;
        }

        foreach (var p in catalog.PermissionSets)
        {
            yield return p.Id;
        }

        foreach (var r in catalog.Roles)
        {
            yield return r.Id;
        }
    }

    public static string Hash(string canonicalJson) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    public static string OpName(OverlayOp op) => SpecJson.ToNode<OverlayOp>(op)?["op"]?.GetValue<string>() ?? op.GetType().Name;

    private static void Apply(OpContext ctx, OverlayOp op)
    {
        switch (op)
        {
            case AddField a:
                ApplyAddField(ctx, a);
                break;
            case UpdateField u:
                ApplyUpdateField(ctx, u);
                break;
            case HideField h:
                ApplyHideField(ctx, h);
                break;
            case RemoveField r:
                ApplyRemoveField(ctx, r);
                break;
            case AddChoiceList acl:
                RequireTenantId(acl.ChoiceList.Id, "choice list");
                if (ctx.Config.FindChoiceList(acl.ChoiceList.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Choice list '{acl.ChoiceList.Id}' already exists.", acl.ChoiceList.Id);
                }

                ctx.Config.ChoiceLists.Add(SpecJson.Clone(acl.ChoiceList));
                ctx.Mark(acl.ChoiceList.Id);
                foreach (var o in acl.ChoiceList.Options)
                {
                    ctx.Mark($"{acl.ChoiceList.Id}/{o.Id}");
                }

                break;
            case AddChoiceOption aco:
                ApplyAddChoiceOption(ctx, aco);
                break;
            case UpdateChoiceOption uco:
                ApplyUpdateChoiceOption(ctx, uco);
                break;
            case AddRule ar:
                RequireTenantId(ar.Rule.Id, "rule");
                if (ctx.Config.FindRule(ar.Rule.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"A rule with ID '{ar.Rule.Id}' already exists.", ar.Rule.Id, "Use update_rule to change it, or choose a new ID.");
                }

                RequireEntity(ctx.Config, ar.Rule.Entity);
                ctx.Config.Rules.Add(SpecJson.Clone(ar.Rule));
                ctx.Mark(ar.Rule.Id);
                break;
            case UpdateRule ur:
                ApplyUpdateRule(ctx, ur);
                break;
            case RemoveRule rr:
                var rule = ctx.Config.FindRule(rr.Rule) ?? throw Missing("rule", rr.Rule);
                if (!IsTenant(ctx, rule.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Rule '{rr.Rule}' comes from {ctx.Layer(rule.Id)} and cannot be removed.", rr.Rule, "Disable it with update_rule (enabled: false) instead.");
                }

                ctx.Config.Rules.Remove(rule);
                ctx.Config.Provenance.Remove(rule.Id);
                break;
            case InsertNode ins:
                ApplyInsertNode(ctx, ins);
                break;
            case UpdateNode un:
                ApplyUpdateNode(ctx, un);
                break;
            case MoveNode mv:
                ApplyMoveNode(ctx, mv);
                break;
            case RemoveNode rn:
                ApplyRemoveNode(ctx, rn);
                break;
            case ResetElement re:
                if (!ctx.ResetSomething)
                {
                    ctx.Redundant($"'{re.Target}' has no customizations to reset.", re.Target);
                }

                break;
            case SetFieldAccess sfa:
                ApplySetFieldAccess(ctx, sfa);
                break;
            case AddCatalogItem aci:
                RequireTenantId(aci.Item.Id, "catalog item");
                if (ctx.Config.FindCatalogItem(aci.Item.Id) is not null)
                {
                    throw new OverlayException("overlay.duplicate", $"Catalog item '{aci.Item.Id}' already exists.", aci.Item.Id, "Use update_catalog_item to change it, or choose a new ID.");
                }

                RequireEntity(ctx.Config, aci.Item.RequestEntity);
                ctx.Config.CatalogItems.Add(SpecJson.Clone(aci.Item));
                ctx.Mark(aci.Item.Id);
                break;
            case UpdateCatalogItem uci:
                ApplyUpdateCatalogItem(ctx, uci);
                break;
            case RemoveCatalogItem rci:
                var item = ctx.Config.FindCatalogItem(rci.Item) ?? throw Missing("catalog item", rci.Item, Expression.Suggest(rci.Item, ctx.Config.CatalogItems.Select(c => c.Id)));
                if (!IsTenant(ctx, item.Id))
                {
                    throw new OverlayException("overlay.base_immutable", $"Catalog item '{rci.Item}' comes from {ctx.Layer(item.Id)} and cannot be removed.", rci.Item, "Deactivate it with update_catalog_item (active: false) instead.");
                }

                ctx.Config.CatalogItems.Remove(item);
                ctx.Config.Provenance.Remove(item.Id);
                break;
            case AddWorkflowState aws:
                ApplyAddWorkflowState(ctx, aws);
                break;
            case AddTransition at:
                ApplyAddTransition(ctx, at);
                break;
            case UpdateTransition ut:
                ApplyUpdateTransition(ctx, ut);
                break;
            case SetActiveInstancePolicy sap:
                var flow = RequireWorkflow(ctx.Config, sap.Workflow);
                flow.ActiveInstances = SpecJson.Clone(sap.Policy);
                ctx.Mark($"{flow.Id}/active_instances");
                break;
            default:
                throw new OverlayException("overlay.unsupported", $"Operation {op.GetType().Name} is not supported.");
        }
    }

    private static void ApplyAddField(OpContext ctx, AddField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        if (!op.Field.Key.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
        {
            throw new OverlayException(
                "overlay.namespace",
                $"Tenant fields must use the '{Namespaces.TenantPrefix}' prefix; '{op.Field.Key}' does not.",
                $"{op.Entity}/{op.Field.Key}",
                Namespaces.TenantKeyFromLabel(op.Field.Label));
        }

        if (!Namespaces.IsValidFieldKey(op.Field.Key))
        {
            throw new OverlayException("overlay.invalid_key", $"'{op.Field.Key}' is not a valid field key (lowercase letters, digits, underscores; starts with a letter).", op.Field.Key, Namespaces.TenantKeyFromLabel(op.Field.Label));
        }

        if (entity.FindField(op.Field.Key) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"{entity.Label} already has a field '{op.Field.Key}'.", $"{op.Entity}/{op.Field.Key}", "Choose another key or update the existing field.");
        }

        if (op.Field.System)
        {
            throw new OverlayException("overlay.system_field", "Tenant fields cannot be system fields.", $"{op.Entity}/{op.Field.Key}");
        }

        entity.Fields.Add(SpecJson.Clone(op.Field));
        ctx.Mark($"{op.Entity}/{op.Field.Key}");
    }

    private static void ApplyUpdateField(OpContext ctx, UpdateField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        var field = entity.FindField(op.Field) ?? throw MissingField(entity, op.Field);
        var target = $"{op.Entity}/{op.Field}";
        var isTenant = IsTenant(ctx, target);
        var current = SpecJson.ToNode(field)!.AsObject();
        var patch = SpecJson.ToNode(op.Set)!.AsObject();
        if (patch.Count == 0)
        {
            throw new OverlayException("overlay.empty_patch", "Nothing to change: the update sets no properties.", target);
        }

        CheckExpected(ctx, target, current, op.Expected, patch);

        if (!isTenant)
        {
            if (op.Set.Required == false && field.Required)
            {
                throw new OverlayException("overlay.loosen_base", $"'{field.Label}' is required by {ctx.Layer(target)}; tenants can tighten base rules but not remove them.", target, "Hide it from a specific form with a form-scoped rule, or keep it required.");
            }

            if (op.Set.MaxLength is { } ml && field.MaxLength is { } baseMl && ml > baseMl)
            {
                throw new OverlayException("overlay.loosen_base", $"'{field.Label}' allows at most {baseMl} characters in {ctx.Layer(target)}; an overlay cannot raise the limit.", target, $"Use maxLength ≤ {baseMl}.");
            }

            if ((op.Set.Min is not null && field.Min is { } bmin && op.Set.Min < bmin) || (op.Set.Max is not null && field.Max is { } bmax && op.Set.Max > bmax))
            {
                throw new OverlayException("overlay.loosen_base", $"An overlay can only narrow the range of '{field.Label}'.", target);
            }
        }

        var allSame = true;
        foreach (var (key, value) in patch)
        {
            if (!JsonNode.DeepEquals(current[key], value))
            {
                allSame = false;
            }
        }

        if (allSame)
        {
            ctx.Redundant($"'{field.Label}' already has these values.", target);
            return;
        }

        var merged = current.DeepClone().AsObject();
        foreach (var (key, value) in patch)
        {
            merged[key] = value?.DeepClone();
        }

        var updated = SpecJson.Deserialize<FieldSpec>(merged.ToJsonString());
        entity.Fields[entity.Fields.IndexOf(field)] = updated;
        ctx.Mark(target, keepLayer: !isTenant);
    }

    private static void ApplyHideField(OpContext ctx, HideField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        var field = entity.FindField(op.Field) ?? throw MissingField(entity, op.Field);
        var target = $"{op.Entity}/{op.Field}";
        if (op.Hidden && entity.TitleField == field.Key)
        {
            throw new OverlayException("overlay.contract_field", $"'{field.Label}' names every {entity.Label} and cannot be hidden.", target, "Relabel it instead.");
        }

        if (op.Hidden && ctx.Config.WorkflowFor(entity.Id)?.StateField == field.Key)
        {
            throw new OverlayException("overlay.contract_field", $"'{field.Label}' drives the workflow and cannot be hidden.", target);
        }

        if (field.Hidden == op.Hidden)
        {
            ctx.Redundant($"'{field.Label}' is already {(op.Hidden ? "hidden" : "visible")}.", target);
            return;
        }

        field.Hidden = op.Hidden;
        ctx.Mark(target, keepLayer: !IsTenant(ctx, target));
    }

    private static void ApplyRemoveField(OpContext ctx, RemoveField op)
    {
        var entity = RequireEntity(ctx.Config, op.Entity);
        var field = entity.FindField(op.Field) ?? throw MissingField(entity, op.Field);
        var target = $"{op.Entity}/{op.Field}";
        if (!IsTenant(ctx, target))
        {
            throw new OverlayException("overlay.base_immutable", $"'{field.Label}' comes from {ctx.Layer(target)} and cannot be removed.", target, "Hide it instead (hide_field).");
        }

        var dependents = DependencyGraph.Build(ctx.Config).DependentsOf($"{entity.Id}/{field.Key}").ToList();
        if (dependents.Count > 0)
        {
            throw new OverlayException(
                "overlay.has_dependents",
                $"'{field.Label}' is still used by: {string.Join(", ", dependents)}.",
                target,
                "Remove or change those first, then remove the field. Its stored values are kept until a separate data purge.");
        }

        entity.Fields.Remove(field);
        ctx.Config.Provenance.Remove(target);
    }

    private static void ApplyAddChoiceOption(OpContext ctx, AddChoiceOption op)
    {
        var list = ctx.Config.FindChoiceList(op.ChoiceList) ?? throw Missing("choice list", op.ChoiceList);
        var target = $"{op.ChoiceList}/{op.Option.Id}";
        if (!IsTenant(ctx, list.Id) && !op.Option.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
        {
            throw new OverlayException("overlay.namespace", $"Options added to {list.Label} must use the '{Namespaces.TenantPrefix}' prefix.", target, Namespaces.TenantKeyFromLabel(op.Option.Label));
        }

        if (list.Find(op.Option.Id) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"{list.Label} already has option '{op.Option.Id}'.", target);
        }

        var index = ResolveAnchor(ctx, list.Options.Select(o => o.Id).ToList(), op.Anchor, target);
        list.Options.Insert(index, SpecJson.Clone(op.Option));
        ctx.Mark(target);
    }

    private static void ApplyUpdateChoiceOption(OpContext ctx, UpdateChoiceOption op)
    {
        var list = ctx.Config.FindChoiceList(op.ChoiceList) ?? throw Missing("choice list", op.ChoiceList);
        var option = list.Find(op.Option) ?? throw Missing("option", $"{op.ChoiceList}/{op.Option}", Expression.Suggest(op.Option, list.Options.Select(o => o.Id)));
        var target = $"{op.ChoiceList}/{op.Option}";
        var patch = new JsonObject();
        if (op.Label is not null)
        {
            patch["label"] = op.Label;
        }

        if (op.Hidden is not null)
        {
            patch["hidden"] = op.Hidden;
        }

        CheckExpected(ctx, target, SpecJson.ToNode(option)!.AsObject(), op.Expected, patch);
        option.Label = op.Label ?? option.Label;
        option.Hidden = op.Hidden ?? option.Hidden;
        ctx.Mark(target, keepLayer: !IsTenant(ctx, target));
    }

    private static void ApplyUpdateRule(OpContext ctx, UpdateRule op)
    {
        var rule = ctx.Config.FindRule(op.Rule) ?? throw Missing("rule", op.Rule, Expression.Suggest(op.Rule, ctx.Config.Rules.Select(r => r.Id)));
        if (op.ExpectedHash is not null)
        {
            var currentHash = Hash(SpecJson.Canonical(SpecJson.ToNode(rule)));
            if (currentHash != op.ExpectedHash)
            {
                ctx.Conflict($"Rule '{op.Rule}' changed since this edit was made.", op.Rule, "Review the current rule and re-apply the change.");
            }
        }

        if (op.Replacement is not null)
        {
            if (!IsTenant(ctx, rule.Id))
            {
                throw new OverlayException("overlay.base_immutable", $"Rule '{op.Rule}' comes from {ctx.Layer(rule.Id)}; tenants can only enable or disable it.", op.Rule, "Disable it and add your own rule.");
            }

            if (op.Replacement.Id != rule.Id)
            {
                throw new OverlayException("overlay.id_change", "A rule's ID cannot change.", op.Rule);
            }

            ctx.Config.Rules[ctx.Config.Rules.IndexOf(rule)] = SpecJson.Clone(op.Replacement);
        }

        if (op.Enabled is { } enabled)
        {
            (ctx.Config.FindRule(op.Rule) ?? rule).Enabled = enabled;
        }

        ctx.Mark(op.Rule, keepLayer: !IsTenant(ctx, op.Rule));
    }

    private static void ApplyInsertNode(OpContext ctx, InsertNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var parent = view.Layout.Find(op.Parent) ?? throw MissingNode(view, op.Parent);
        foreach (var node in op.Node.Descendants())
        {
            if (!node.Id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
            {
                throw new OverlayException("overlay.namespace", $"New node IDs must start with '{Namespaces.TenantPrefix}'; '{node.Id}' does not.", node.Id, Namespaces.NewTenantNodeId());
            }

            if (view.Layout.Find(node.Id) is not null)
            {
                throw new OverlayException("overlay.duplicate", $"Node '{node.Id}' already exists in {view.Label}.", node.Id, Namespaces.NewTenantNodeId());
            }
        }

        var index = ResolveAnchor(ctx, parent.Children.Select(c => c.Id).ToList(), op.Anchor, op.Node.Id);
        parent.Children.Insert(index, SpecJson.Clone(op.Node));
        foreach (var node in op.Node.Descendants())
        {
            ctx.Mark(node.Id);
        }
    }

    private static void ApplyUpdateNode(OpContext ctx, UpdateNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var node = view.Layout.Find(op.Node) ?? throw MissingNode(view, op.Node);
        CheckExpected(ctx, op.Node, node.Props, op.Expected, op.Props);
        foreach (var (key, value) in op.Props)
        {
            if (value is null)
            {
                node.Props.Remove(key);
            }
            else
            {
                node.Props[key] = value.DeepClone();
            }
        }

        ctx.Mark(op.Node, keepLayer: !IsTenant(ctx, op.Node));
    }

    private static void ApplyMoveNode(OpContext ctx, MoveNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var node = view.Layout.Find(op.Node) ?? throw MissingNode(view, op.Node);
        var newParent = view.Layout.Find(op.Parent) ?? throw MissingNode(view, op.Parent);
        if (node.Find(op.Parent) is not null)
        {
            throw new OverlayException("overlay.cycle", $"Cannot move '{op.Node}' into itself or one of its children.", op.Node);
        }

        var oldParent = view.Layout.ParentOf(op.Node) ?? throw new OverlayException("overlay.root", "The page root cannot be moved.", op.Node);
        oldParent.Children.Remove(node);
        var index = ResolveAnchor(ctx, newParent.Children.Select(c => c.Id).ToList(), op.Anchor, op.Node);
        newParent.Children.Insert(index, node);
        ctx.Mark(op.Node, keepLayer: !IsTenant(ctx, op.Node));
    }

    private static void ApplyRemoveNode(OpContext ctx, RemoveNode op)
    {
        var view = RequireView(ctx.Config, op.View);
        var node = view.Layout.Find(op.Node) ?? throw MissingNode(view, op.Node);
        var parent = view.Layout.ParentOf(op.Node) ?? throw new OverlayException("overlay.root", "The page root cannot be removed.", op.Node);
        if (IsTenant(ctx, node.Id) && node.Descendants().All(n => IsTenant(ctx, n.Id)))
        {
            parent.Children.Remove(node);
            foreach (var n in node.Descendants())
            {
                ctx.Config.Provenance.Remove(n.Id);
            }
        }
        else
        {
            node.Hidden = true;
            ctx.Mark(op.Node, keepLayer: true);
        }
    }

    private static void ApplySetFieldAccess(OpContext ctx, SetFieldAccess op)
    {
        var set = ctx.Config.PermissionSets.Find(p => p.Id == op.PermissionSet) ?? throw Missing("permission set", op.PermissionSet, Expression.Suggest(op.PermissionSet, ctx.Config.PermissionSets.Select(p => p.Id)));
        var entity = RequireEntity(ctx.Config, op.Entity);
        if (op.Field != "*" && entity.FindField(op.Field) is null)
        {
            throw MissingField(entity, op.Field);
        }

        if (op.Access is not (FieldAccessLevels.Hidden or FieldAccessLevels.Read or FieldAccessLevels.Write))
        {
            throw new OverlayException("overlay.invalid_value", $"Access must be hidden, read, or write (got '{op.Access}').", op.PermissionSet);
        }

        var existing = set.Fields.Find(f => f.Entity == op.Entity && f.Field == op.Field);
        if (existing is not null)
        {
            existing.Access = op.Access;
        }
        else
        {
            set.Fields.Add(new FieldPermissionSpec { Entity = op.Entity, Field = op.Field, Access = op.Access });
        }

        ctx.Mark(op.PermissionSet, keepLayer: !IsTenant(ctx, op.PermissionSet));
    }

    private static void ApplyUpdateCatalogItem(OpContext ctx, UpdateCatalogItem op)
    {
        var item = ctx.Config.FindCatalogItem(op.Item) ?? throw Missing("catalog item", op.Item, Expression.Suggest(op.Item, ctx.Config.CatalogItems.Select(c => c.Id)));
        var tenant = IsTenant(ctx, item.Id);
        if (!tenant && (op.Set.Variables is not null || op.Set.Tasks is not null))
        {
            throw new OverlayException(
                "overlay.base_immutable",
                $"The questions and tasks of '{item.Label}' come from {ctx.Layer(item.Id)} and cannot be replaced.",
                op.Item,
                "Deactivate it and add your own catalog item with add_catalog_item.");
        }

        var patch = SpecJson.ToNode(op.Set)!.AsObject();
        patch.Remove("variables");
        patch.Remove("tasks");
        CheckExpected(ctx, op.Item, SpecJson.ToNode(item)!.AsObject(), op.Expected, patch);
        item.Label = op.Set.Label ?? item.Label;
        item.Description = op.Set.Description ?? item.Description;
        item.Category = op.Set.Category ?? item.Category;
        item.Icon = op.Set.Icon ?? item.Icon;
        item.Active = op.Set.Active ?? item.Active;
        item.Order = op.Set.Order ?? item.Order;
        if (op.Set.Variables is not null)
        {
            item.Variables = op.Set.Variables.Select(SpecJson.Clone).ToList();
        }

        if (op.Set.Tasks is not null)
        {
            item.Tasks = op.Set.Tasks.Select(SpecJson.Clone).ToList();
        }

        ctx.Mark(item.Id, keepLayer: !tenant);
    }

    private static void ApplyAddWorkflowState(OpContext ctx, AddWorkflowState op)
    {
        var wf = RequireWorkflow(ctx.Config, op.Workflow);
        RequireTenantId(op.State.Id, "state");
        if (wf.FindState(op.State.Id) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"Workflow '{wf.Id}' already has a state '{op.State.Id}'.", $"{wf.Id}/state.{op.State.Id}");
        }

        var entity = RequireEntity(ctx.Config, wf.Entity);
        var list = entity.FindField(wf.StateField)?.ChoiceList is { } listId ? ctx.Config.FindChoiceList(listId) : null;
        if (list is null)
        {
            throw new OverlayException("overlay.not_found", $"Workflow '{wf.Id}' has no status choice list to add the state to.", wf.Id);
        }

        wf.States.Add(SpecJson.Clone(op.State));
        if (list.Find(op.State.Id) is null)
        {
            var siblings = list.Options.Select(o => o.Id).ToList();
            list.Options.Insert(ResolveAnchor(ctx, siblings, op.Anchor, op.State.Id), new ChoiceOption { Id = op.State.Id, Label = op.Label, Tone = op.Tone });
            ctx.Mark($"{list.Id}/{op.State.Id}");
        }

        ctx.Mark($"{wf.Id}/state.{op.State.Id}");
    }

    private static void ApplyAddTransition(OpContext ctx, AddTransition op)
    {
        var wf = RequireWorkflow(ctx.Config, op.Workflow);
        RequireTenantId(op.Transition.Id, "transition");
        if (wf.FindTransition(op.Transition.Id) is not null)
        {
            throw new OverlayException("overlay.duplicate", $"Workflow '{wf.Id}' already has a transition '{op.Transition.Id}'.", $"{wf.Id}/{op.Transition.Id}", "Use update_transition to change it.");
        }

        if (op.Transition.System)
        {
            throw new OverlayException("overlay.system_transition", "Only modules define system transitions.", $"{wf.Id}/{op.Transition.Id}");
        }

        wf.Transitions.Add(SpecJson.Clone(op.Transition));
        ctx.Mark($"{wf.Id}/{op.Transition.Id}");
    }

    private static void ApplyUpdateTransition(OpContext ctx, UpdateTransition op)
    {
        var wf = RequireWorkflow(ctx.Config, op.Workflow);
        var target = $"{wf.Id}/{op.Transition}";
        var t = wf.FindTransition(op.Transition) ?? throw Missing("transition", target, Expression.Suggest(op.Transition, wf.Transitions.Select(x => x.Id)));
        var tenant = IsTenant(ctx, target);
        var p = op.Set;
        if (!tenant && (p.From is not null || p.To is not null || p.Guard is not null || p.RequiredFields is not null || p.Roles is not null || p.Set is not null))
        {
            throw new OverlayException(
                "overlay.base_immutable",
                $"Transition '{t.Label}' comes from {ctx.Layer(target)}; tenants can change only its label, guard message, tone and whether it is enabled.",
                target,
                "Disable it and add your own transition with add_transition.");
        }

        if (t.System && p.Enabled == false)
        {
            throw new OverlayException("overlay.system_transition", $"'{t.Label}' is used by the platform and cannot be disabled.", target);
        }

        CheckExpected(ctx, target, SpecJson.ToNode(t)!.AsObject(), op.Expected, SpecJson.ToNode(p)!.AsObject());
        t.Label = p.Label ?? t.Label;
        t.From = p.From ?? t.From;
        t.To = p.To ?? t.To;
        t.Guard = p.Guard ?? t.Guard;
        t.GuardMessage = p.GuardMessage ?? t.GuardMessage;
        t.RequiredFields = p.RequiredFields ?? t.RequiredFields;
        t.Roles = p.Roles ?? t.Roles;
        t.Set = p.Set ?? t.Set;
        t.Tone = p.Tone ?? t.Tone;
        t.Enabled = p.Enabled ?? t.Enabled;
        ctx.Mark(target, keepLayer: !tenant);
    }

    private static WorkflowSpec RequireWorkflow(EffectiveConfig config, string id) =>
        config.Workflows.Find(w => w.Id == id) ?? throw Missing("workflow", id, Expression.Suggest(id, config.Workflows.Select(w => w.Id)));

    private static void CheckExpected(OpContext ctx, string target, JsonObject current, JsonObject? expected, JsonObject patch)
    {
        if (expected is null)
        {
            return;
        }

        foreach (var (key, expectedValue) in expected)
        {
            var actual = current[key];
            if (JsonNode.DeepEquals(actual, expectedValue) || (actual is null && expectedValue is null))
            {
                continue;
            }

            // Base now equals what the overlay wanted: compatible, nothing to do for this property.
            if (patch.TryGetPropertyValue(key, out var wanted) && JsonNode.DeepEquals(actual, wanted))
            {
                continue;
            }

            ctx.Conflict(
                $"{target}: '{key}' was {expectedValue?.ToJsonString() ?? "unset"} when this change was made but is now {actual?.ToJsonString() ?? "unset"} ({ctx.Layer(target)}).",
                target,
                $"Keep your value ({patch[key]?.ToJsonString() ?? "unset"}) or accept the new default ({actual?.ToJsonString() ?? "unset"}).");
        }
    }

    private static int ResolveAnchor(OpContext ctx, List<string> siblings, Anchor? anchor, string target)
    {
        if (anchor is null || (anchor.Before is null && anchor.After is null))
        {
            return anchor?.Position == "start" ? 0 : siblings.Count;
        }

        var reference = anchor.Before ?? anchor.After!;
        var index = siblings.IndexOf(reference);
        if (index < 0)
        {
            ctx.Warn("overlay.anchor_missing", $"'{reference}' is no longer next to where '{target}' was placed; it was placed at the end instead.", target);
            return siblings.Count;
        }

        return anchor.Before is not null ? index : index + 1;
    }

    private static bool IsTenant(OpContext ctx, string key) =>
        !ctx.Config.Provenance.TryGetValue(key, out var layer) || layer == TenantLayer;

    private static void RequireTenantId(string id, string kind)
    {
        if (!id.StartsWith(Namespaces.TenantPrefix, StringComparison.Ordinal))
        {
            throw new OverlayException("overlay.namespace", $"New {kind} IDs must start with '{Namespaces.TenantPrefix}'; '{id}' does not.", id, Namespaces.TenantPrefix + id.Split('.')[^1]);
        }
    }

    private static EntitySpec RequireEntity(EffectiveConfig config, string id) =>
        config.FindEntity(id) ?? throw Missing("entity", id, Expression.Suggest(id, config.Entities.Select(e => e.Id)));

    private static ViewSpec RequireView(EffectiveConfig config, string id) =>
        config.FindView(id) ?? throw Missing("view", id, Expression.Suggest(id, config.Views.Select(v => v.Id)));

    private static OverlayException Missing(string kind, string id, string? suggestion = null) =>
        new("overlay.not_found", $"No {kind} '{id}' exists." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), id, suggestion);

    private static OverlayException MissingField(EntitySpec entity, string key)
    {
        var suggestion = Expression.Suggest(key, entity.Fields.Select(f => f.Key));
        return new OverlayException(
            "overlay.not_found",
            $"{entity.Label} has no field '{key}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"),
            $"{entity.Id}/{key}",
            suggestion);
    }

    private static OverlayException MissingNode(ViewSpec view, string id)
    {
        var suggestion = Expression.Suggest(id, view.Layout.Descendants().Select(n => n.Id));
        return new OverlayException("overlay.not_found", $"{view.Label} has no node '{id}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), id, suggestion);
    }

    private sealed class OpContext(EffectiveConfig config, int index)
    {
        private readonly List<Diagnostic> _diagnostics = [];

        public EffectiveConfig Config { get; } = config;

        public string Status { get; private set; } = OpStatus.Applied;

        /// <summary>For element.reset: whether it undid at least one earlier operation.</summary>
        public bool ResetSomething { get; init; }

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        public string Layer(string key) => Config.Provenance.GetValueOrDefault(key, TenantLayer);

        public void Mark(string key, bool keepLayer = false)
        {
            if (!Config.Customized.Contains(key))
            {
                Config.Customized.Add(key);
            }

            if (!keepLayer)
            {
                Config.Provenance[key] = TenantLayer;
            }
        }

        public void Warn(string code, string message, string? target) =>
            _diagnostics.Add(new Diagnostic(Severity.Warning, code, message, target, null, index));

        public void Redundant(string message, string target)
        {
            Status = OpStatus.Redundant;
            _diagnostics.Add(new Diagnostic(Severity.Warning, "overlay.redundant", message, target, null, index));
        }

        public void Conflict(string message, string target, string? suggestion)
        {
            Status = OpStatus.Conflict;
            _diagnostics.Add(new Diagnostic(Severity.Error, "overlay.conflict", message, target, suggestion, index));
        }

        public void Fail(string code, string message, string? target, string? suggestion)
        {
            Status = OpStatus.Failed;
            _diagnostics.Add(new Diagnostic(Severity.Error, code, message, target, suggestion, index));
        }
    }
}

public sealed class OverlayException : Exception
{
    public OverlayException(string code, string message, string? target = null, string? suggestion = null)
        : base(message)
    {
        Code = code;
        Target = target;
        Suggestion = suggestion;
    }

    public OverlayException()
        : this("overlay.error", "Overlay error")
    {
    }

    public OverlayException(string message)
        : this("overlay.error", message)
    {
    }

    public OverlayException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "overlay.error";
    }

    public string Code { get; }

    public string? Target { get; }

    public string? Suggestion { get; }
}
