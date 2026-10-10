using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>Acceptance scenario 3 (unit level): upgrades preserve compatible overlays and block conflicts.</summary>
public class UpgradeTests
{
    private const string Ticket = "core.tickets.ticket";

    [Fact]
    public void Canonical_customization_survives_upgrade_to_1_1_0()
    {
        var result = Compile("1.1.0", OverlayTests.CanonicalCustomization());
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var entity = result.Config.FindEntity(Ticket)!;
        Assert.NotNull(entity.FindField("tenant__vendor"));
        Assert.NotNull(entity.FindField("impact"));
        Assert.Equal("core.tickets@1.1.0", result.Config.Provenance[$"{Ticket}/impact"]);
        Assert.Equal(2, result.Manifest!.Migrations.Count);
    }

    [Fact]
    public void Relabel_conflicts_when_base_changed_the_same_property()
    {
        var relabel = new UpdateField
        {
            Entity = Ticket,
            Field = "category",
            Set = new FieldPatch { Label = "Request type" },
            Expected = new JsonObject { ["label"] = "Category" },
        };
        Assert.True(Compile("1.0.0", relabel).Succeeded);

        var v2 = Tickets("1.1.0");
        v2 = SpecJson.Clone(v2);
        v2.Entities[0].FindField("category")!.Label = "Service category";
        var upgraded = Compiler.Compile([Platform, v2], [relabel]);
        var conflict = Assert.Single(upgraded.Composition.Outcomes, o => o.Status == OpStatus.Conflict);
        Assert.Contains("'label' was \"Category\"", conflict.Diagnostics[0].Message);
        Assert.False(upgraded.Succeeded);
    }

    [Fact]
    public void Base_adopting_the_tenant_value_is_compatible()
    {
        var relabel = new UpdateField { Entity = Ticket, Field = "category", Set = new FieldPatch { Label = "Request type" }, Expected = new JsonObject { ["label"] = "Category" } };
        var v2 = SpecJson.Clone(Tickets("1.1.0"));
        v2.Entities[0].FindField("category")!.Label = "Request type";
        var upgraded = Compiler.Compile([Platform, v2], [relabel]);
        Assert.True(upgraded.Succeeded);
        Assert.Equal(OpStatus.Redundant, upgraded.Composition.Outcomes[0].Status);
    }

    [Fact]
    public void Base_removing_a_field_the_tenant_depends_on_blocks_with_impact()
    {
        var rule = new AddRule
        {
            Rule = new RuleSpec { Id = "tenant__r", Kind = RuleKinds.Conditional, Entity = Ticket, When = "record.estimated_effort == '8'", Actions = [new RuleActionSpec { Type = "message", Message = "Big job" }] },
        };
        var v2 = SpecJson.Clone(Tickets("1.1.0"));
        v2.Entities[0].Fields.RemoveAll(f => f.Key == "estimated_effort");
        foreach (var n in v2.Views.SelectMany(v => v.Layout.Descendants()))
        {
            n.Children.RemoveAll(c => c.StringProp("field") == "estimated_effort");
        }

        v2.PermissionSets.ForEach(p => p.Fields.RemoveAll(f => f.Field == "estimated_effort"));
        v2.Migrations.Clear();
        var upgraded = Compiler.Compile([Platform, v2], [rule]);
        Assert.Contains(upgraded.Errors, e => e.Code == "expr.unknown_field" && e.Target == "tenant__r");
    }

    [Fact]
    public void Anchor_removed_by_base_falls_back_with_warning()
    {
        var insert = new InsertNode
        {
            View = "core.tickets.record",
            Parent = "core.tickets.record.c_side",
            Anchor = new Anchor { After = "core.tickets.record.card_properties" },
            Node = new NodeSpec { Id = "tenant__n_c", Type = "layout.card", Props = new JsonObject { ["title"] = "Extra" } },
        };
        var v2 = SpecJson.Clone(Tickets("1.1.0"));
        var side = v2.Views[0].Layout.Find("core.tickets.record.c_side")!;
        side.Children.RemoveAll(c => c.Id == "core.tickets.record.card_properties");
        var upgraded = Compiler.Compile([Platform, v2], [insert]);
        Assert.True(upgraded.Succeeded);
        Assert.Contains(upgraded.Diagnostics, d => d.Code == "overlay.anchor_missing");
        Assert.Equal("tenant__n_c", upgraded.Config.FindView("core.tickets.record")!.Layout.Find("core.tickets.record.c_side")!.Children[^1].Id);
    }
}
