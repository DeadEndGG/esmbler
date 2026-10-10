using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-BIZ-004, REQ-AUTHZ-011, REQ-MIG-003: approval policies, their validation and overlay operations (ADR 0016).</summary>
public class ApprovalPolicyTests
{
    private const string Request = "core.tickets.request";
    private const string RequestFlow = "core.tickets.request_lifecycle";
    private const string Laptop = "core.tickets.catalog.new_laptop";

    private static ApprovalPolicySpec Policy(string id = "tenant__finance") => new()
    {
        Id = id,
        Label = "Finance approval",
        Entity = Request,
        Stages =
        [
            new ApprovalStageSpec { Id = "manager", Label = "Manager", Mode = ApprovalModes.Any, Approvers = [new ApproverSpec { Kind = ApproverKinds.Role, Ref = "core.tickets.approver" }] },
            new ApprovalStageSpec { Id = "finance", Label = "Finance", Approvers = [new ApproverSpec { Kind = ApproverKinds.Group, Ref = "finance" }], ExpiresAfterHours = 48, OnExpiry = ApprovalExpiry.Reject },
        ],
    };

    [Fact]
    public void Release_1_3_0_puts_laptops_and_software_access_behind_approval()
    {
        var result = Compile("1.3.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.Equal("core.tickets.approval.manager", cfg.FindCatalogItem(Laptop)!.ApprovalPolicy);
        Assert.Equal("awaiting_approval", cfg.FindEntity(Request)!.Request!.Approval!.State);
        Assert.Equal(2, cfg.FindApprovalPolicy("core.tickets.approval.access")!.Stages.Count);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "config.approvers_not_related");
    }

    [Fact]
    public void A_tenant_policy_compiles_and_a_catalog_item_can_use_it()
    {
        var result = Compile("1.3.0", new AddApprovalPolicy { Policy = Policy() }, new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "tenant__finance" } });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Equal("tenant__finance", result.Config.FindCatalogItem(Laptop)!.ApprovalPolicy);
        Assert.Contains("tenant__finance", result.Config.Customized);
    }

    [Fact]
    public void Invalid_stages_and_approvers_are_rejected_with_suggestions()
    {
        var policy = Policy();
        policy.Stages[0].Mode = "majority";
        policy.Stages[0].Approvers.Add(new ApproverSpec { Kind = ApproverKinds.Role, Ref = "core.tickets.aprover" });
        policy.Stages[1].Approvers.Add(new ApproverSpec { Kind = ApproverKinds.Field, Ref = "record.title" });
        policy.Stages[1].OnExpiry = ApprovalExpiry.Escalate;
        policy.Stages[1].When = "variables.amount > 100";
        var result = Compile("1.3.0", new AddApprovalPolicy { Policy = policy }, new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "tenant__finance" } });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_mode");
        Assert.Contains(result.Errors, e => e.Code == "config.not_found" && e.Suggestion == "core.tickets.approver");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_approvers" && e.Message.Contains("record.title", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_expiry");
        Assert.Contains(result.Errors, e => e.Target == "tenant__finance/finance" && e.Message.Contains("condition", StringComparison.Ordinal));
    }

    [Fact]
    public void Policies_need_an_entity_with_an_approval_binding()
    {
        var policy = Policy();
        policy.Entity = "core.tickets.ticket";
        var result = Compile("1.3.0", new AddApprovalPolicy { Policy = policy });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_request_entity");
    }

    [Fact]
    public void Base_policy_stages_cannot_be_replaced_and_used_policies_cannot_be_removed()
    {
        var replace = Compile("1.3.0", new UpdateApprovalPolicy { Policy = "core.tickets.approval.manager", Set = new ApprovalPolicyPatch { Stages = [] } });
        Assert.Equal("overlay.base_immutable", Assert.Single(replace.Errors).Code);

        var remove = Compile("1.3.0", new AddApprovalPolicy { Policy = Policy() }, new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "tenant__finance" } }, new RemoveApprovalPolicy { Policy = "tenant__finance" });
        Assert.Contains(remove.Errors, e => e.Code == "overlay.in_use");

        var relabel = Compile("1.3.0", new UpdateApprovalPolicy { Policy = "core.tickets.approval.manager", Set = new ApprovalPolicyPatch { Label = "Line manager approval" } });
        Assert.True(relabel.Succeeded);
    }

    [Fact]
    public void A_migration_must_keep_pending_approvals_or_cancel_them()
    {
        var result = Compile(
            "1.3.0",
            new SetActiveInstancePolicy { Workflow = RequestFlow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["awaiting_approval"] = "submitted" } } });
        Assert.Contains(result.Errors, e => e.Code == "config.strands_approvals");

        var cancel = Compile(
            "1.3.0",
            new SetActiveInstancePolicy { Workflow = RequestFlow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["awaiting_approval"] = "cancelled" } } });
        Assert.DoesNotContain(cancel.Errors, e => e.Code == "config.strands_approvals");
    }

    [Fact]
    public void Weakening_approval_is_a_permission_change_but_adding_a_policy_is_not()
    {
        Assert.False(new AddApprovalPolicy { Policy = Policy() }.AffectsPermissions);
        Assert.True(new UpdateApprovalPolicy { Policy = "x", Set = new ApprovalPolicyPatch { Stages = [] } }.AffectsPermissions);
        Assert.False(new UpdateApprovalPolicy { Policy = "x", Set = new ApprovalPolicyPatch { Label = "X" } }.AffectsPermissions);
        Assert.True(new RemoveApprovalPolicy { Policy = "x" }.AffectsPermissions);
        Assert.True(new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "" } }.AffectsPermissions);
        Assert.False(new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "tenant__finance" } }.AffectsPermissions);
    }

    [Fact]
    public void Approval_ops_describe_themselves_and_round_trip_as_json()
    {
        OverlayOp op = new AddApprovalPolicy { Policy = Policy() };
        Assert.Equal("Add approval policy \"Finance approval\" with 2 stage(s): Manager → Finance", op.Describe());
        var json = SpecJson.Serialize(op);
        Assert.Contains("\"op\":\"approval_policy.add\"", json, StringComparison.Ordinal);
        var back = Assert.IsType<AddApprovalPolicy>(SpecJson.Deserialize<OverlayOp>(json));
        Assert.Equal("finance", back.Policy.Stages[1].Id);
    }
}
