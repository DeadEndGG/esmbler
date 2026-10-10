using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-CFG-001..006: typed overlay operations, namespacing, conflicts, validation.</summary>
public class OverlayTests
{
    private const string Ticket = "core.tickets.ticket";
    private const string Record = "core.tickets.record";

    public static OverlayOp[] CanonicalCustomization() =>
    [
        new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__vendor", Type = "short_text", Label = "Vendor", MaxLength = 80 } },
        new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__justification", Type = "long_text", Label = "Justification" } },
        new AddRule
        {
            Rule = new RuleSpec
            {
                Id = "tenant__access_needs_justification",
                Kind = RuleKinds.Conditional,
                Entity = Ticket,
                When = "record.category == 'access'",
                Actions = [new RuleActionSpec { Type = RuleActionTypes.Require, Field = "tenant__justification" }],
            },
        },
        new InsertNode
        {
            View = Record,
            Parent = "core.tickets.record.c_side",
            Anchor = new Anchor { After = "core.tickets.record.card_properties" },
            Node = new NodeSpec
            {
                Id = "tenant__n_vendor_card",
                Type = "layout.card",
                Props = new JsonObject { ["title"] = "Vendor" },
                Children = [new NodeSpec { Id = "tenant__n_vendor_field", Type = "field", Props = new JsonObject { ["field"] = "tenant__vendor" } }],
            },
        },
    ];

    [Fact]
    public void Canonical_customization_compiles_and_records_provenance()
    {
        var result = Compile("1.0.0", CanonicalCustomization());
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.NotNull(cfg.FindEntity(Ticket)!.FindField("tenant__vendor"));
        var side = cfg.FindView(Record)!.Layout.Find("core.tickets.record.c_side")!;
        Assert.Equal(["core.tickets.record.card_properties", "tenant__n_vendor_card", "core.tickets.record.card_resolution"], side.Children.Select(c => c.Id));
        Assert.Equal("tenant", cfg.Provenance[$"{Ticket}/tenant__vendor"]);
        Assert.Equal("core.tickets@1.0.0", cfg.Provenance[$"{Ticket}/title"]);
        Assert.Contains("tenant__n_vendor_card", cfg.Customized);
    }

    [Fact]
    public void Tenant_field_without_namespace_is_rejected_with_suggestion()
    {
        var result = Compile("1.0.0", new AddField { Entity = Ticket, Field = new FieldSpec { Key = "vendor", Type = "short_text", Label = "Vendor name" } });
        var error = Assert.Single(result.Errors);
        Assert.Equal("overlay.namespace", error.Code);
        Assert.Equal("tenant__vendor_name", error.Suggestion);
    }

    [Fact]
    public void Unknown_field_in_rule_is_reported_with_suggestion()
    {
        var result = Compile("1.0.0", new AddRule
        {
            Rule = new RuleSpec { Id = "tenant__r", Kind = RuleKinds.Conditional, Entity = Ticket, When = "record.categry == 'access'", Actions = [new RuleActionSpec { Type = "require", Field = "title" }] },
        });
        var error = Assert.Single(result.Errors);
        Assert.Equal("expr.unknown_field", error.Code);
        Assert.Equal("record.category", error.Suggestion);
    }

    [Fact]
    public void Card_inside_a_row_is_rejected_by_containment_rules()
    {
        var result = Compile("1.0.0", new InsertNode
        {
            View = Record,
            Parent = "core.tickets.record.r_main",
            Node = new NodeSpec { Id = "tenant__n_bad", Type = "layout.card", Props = new JsonObject { ["title"] = "Bad" } },
        });
        Assert.Contains(result.Errors, e => e.Code == "layout.bad_parent");
        Assert.Contains(result.Errors, e => e.Code == "layout.row_spans" || e.Code == "layout.bad_parent");
    }

    [Fact]
    public void Invalid_props_are_rejected()
    {
        var result = Compile("1.0.0", new InsertNode
        {
            View = Record,
            Parent = "core.tickets.record.c_side",
            Node = new NodeSpec { Id = "tenant__n_card", Type = "layout.card", Props = new JsonObject { ["title"] = "X", ["color"] = "#ff0000" } },
        });
        var error = Assert.Single(result.Errors);
        Assert.Equal("layout.bad_props", error.Code);
        Assert.Contains("'color' is not a supported property", error.Message);
    }

    [Fact]
    public void Base_field_cannot_be_removed_or_loosened()
    {
        var remove = Compile("1.0.0", new RemoveField { Entity = Ticket, Field = "category" });
        Assert.Equal("overlay.base_immutable", Assert.Single(remove.Errors).Code);
        var loosen = Compile("1.0.0", new UpdateField { Entity = Ticket, Field = "title", Set = new FieldPatch { Required = false } });
        Assert.Equal("overlay.loosen_base", Assert.Single(loosen.Errors).Code);
    }

    [Fact]
    public void Removing_a_used_tenant_field_lists_dependents()
    {
        var ops = CanonicalCustomization().Append(new RemoveField { Entity = Ticket, Field = "tenant__vendor" }).ToArray();
        var result = Compile("1.0.0", ops);
        var error = Assert.Single(result.Errors);
        Assert.Equal("overlay.has_dependents", error.Code);
        Assert.Contains("tenant__n_vendor_field", error.Message);
    }

    [Fact]
    public void Removing_a_base_node_hides_it()
    {
        var result = Compile("1.0.0", new RemoveNode { View = Record, Node = "core.tickets.record.card_resolution" });
        Assert.True(result.Succeeded);
        Assert.True(result.Config.FindView(Record)!.Layout.Find("core.tickets.record.card_resolution")!.Hidden);
    }

    [Fact]
    public void Required_and_hidden_without_default_is_rejected()
    {
        var result = Compile(
            "1.0.0",
            new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__x", Type = "short_text", Label = "X" } },
            new UpdateField { Entity = Ticket, Field = "tenant__x", Set = new FieldPatch { Required = true } },
            new HideField { Entity = Ticket, Field = "tenant__x" });
        Assert.Contains(result.Errors, e => e.Code == "config.required_hidden");
    }

    [Fact]
    public void Set_value_cycles_are_detected()
    {
        RuleSpec Setter(string id, string field, string from) => new()
        {
            Id = id,
            Kind = RuleKinds.Conditional,
            Entity = Ticket,
            Actions = [new RuleActionSpec { Type = "set_value", Field = field, Value = $"record.{from}" }],
        };
        var result = Compile(
            "1.0.0",
            new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__a", Type = "short_text", Label = "A" } },
            new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__b", Type = "short_text", Label = "B" } },
            new AddRule { Rule = Setter("tenant__r1", "tenant__a", "tenant__b") },
            new AddRule { Rule = Setter("tenant__r2", "tenant__b", "tenant__a") });
        Assert.Contains(result.Errors, e => e.Code == "config.rule_cycle");
    }

    [Fact]
    public void Overlay_operations_round_trip_through_json()
    {
        var json = SpecJson.Serialize<OverlayOp[]>(CanonicalCustomization());
        Assert.Contains("\"op\":\"field.add\"", json);
        var back = SpecJson.Deserialize<OverlayOp[]>(json);
        Assert.Equal(4, back.Length);
        Assert.IsType<InsertNode>(back[3]);
    }
}
