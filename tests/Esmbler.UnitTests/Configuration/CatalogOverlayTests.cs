using System.Text.Json.Nodes;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>
/// REQ-BIZ-004, REQ-MIG-003, REQ-CFG-001: catalog items, request bindings and workflow overlay operations (ADR 0015).
/// </summary>
public class CatalogOverlayTests
{
    private const string Request = "core.tickets.request";
    private const string RequestFlow = "core.tickets.request_lifecycle";
    private const string Laptop = "core.tickets.catalog.new_laptop";

    private static CatalogItemSpec TenantItem(string id = "tenant__parking") => new()
    {
        Id = id,
        Label = "Parking permit",
        Category = "Facilities",
        RequestEntity = Request,
        Variables =
        [
            new VariableSpec { Key = "plate", Label = "Licence plate", Type = FieldTypes.ShortText, Required = true, MaxLength = 12 },
            new VariableSpec { Key = "electric", Label = "Electric vehicle", Type = FieldTypes.Toggle },
        ],
        Tasks =
        [
            new FulfillmentTaskSpec { Id = "issue", Title = "Issue the permit", AssignmentGroup = "service_desk" },
            new FulfillmentTaskSpec { Id = "charger", Title = "Enable a charging bay", DependsOn = ["issue"], When = "variables.electric == true" },
        ],
    };

    [Fact]
    public void Release_1_2_0_ships_requests_tasks_and_three_catalog_items()
    {
        var result = Compile("1.2.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.Equal(3, cfg.CatalogItems.Count);
        Assert.Equal("core.tickets.request_task", cfg.FindEntity(Request)!.Request!.TaskEntity);
        Assert.True(cfg.Workflows.Find(w => w.Id == RequestFlow)!.FindTransition("complete")!.System);
        Assert.Equal("core.tickets@1.2.0", cfg.Provenance[Laptop]);
    }

    [Fact]
    public void Tenant_catalog_item_compiles_and_is_tracked_as_a_customization()
    {
        var result = Compile("1.2.0", new AddCatalogItem { Item = TenantItem() });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Equal("tenant", result.Config.Provenance["tenant__parking"]);
        Assert.Contains("tenant__parking", result.Config.Customized);
    }

    [Fact]
    public void New_catalog_item_ids_need_the_tenant_prefix()
    {
        var result = Compile("1.2.0", new AddCatalogItem { Item = TenantItem("parking") });
        Assert.Equal("overlay.namespace", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Circular_task_dependencies_are_rejected()
    {
        var item = TenantItem();
        item.Tasks[0].DependsOn = ["charger"];
        var result = Compile("1.2.0", new AddCatalogItem { Item = item });
        Assert.Contains(result.Errors, e => e.Code == "config.task_cycle");
    }

    [Fact]
    public void Unsupported_variable_types_missing_choice_lists_and_bad_conditions_are_rejected()
    {
        var item = TenantItem();
        item.Variables.Add(new VariableSpec { Key = "notes", Label = "Notes", Type = FieldTypes.RichText });
        item.Variables.Add(new VariableSpec { Key = "zone", Label = "Zone", Type = FieldTypes.SingleSelect });
        item.Tasks[1].When = "variables.electrik == true";
        var result = Compile("1.2.0", new AddCatalogItem { Item = item });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_variable_type");
        Assert.Contains(result.Errors, e => e.Code == "config.missing_choice_list");
        Assert.Contains(result.Errors, e => e.Target == "tenant__parking/charger");
    }

    [Fact]
    public void Catalog_items_must_target_an_entity_that_receives_requests()
    {
        var item = TenantItem();
        item.RequestEntity = "core.tickets.ticket";
        var result = Compile("1.2.0", new AddCatalogItem { Item = item });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_request_entity");
    }

    [Fact]
    public void Base_items_can_be_deactivated_but_their_questions_cannot_be_replaced()
    {
        var deactivate = Compile("1.2.0", new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { Active = false } });
        Assert.True(deactivate.Succeeded);
        Assert.False(deactivate.Config.FindCatalogItem(Laptop)!.Active);
        Assert.Equal("core.tickets@1.2.0", deactivate.Config.Provenance[Laptop]);

        var replace = Compile("1.2.0", new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { Variables = [] } });
        Assert.Equal("overlay.base_immutable", Assert.Single(replace.Errors).Code);

        var remove = Compile("1.2.0", new RemoveCatalogItem { Item = Laptop });
        Assert.Equal("overlay.base_immutable", Assert.Single(remove.Errors).Code);
    }

    [Fact]
    public void A_stale_expected_label_is_a_conflict()
    {
        var result = Compile("1.2.0", new UpdateCatalogItem
        {
            Item = Laptop,
            Set = new CatalogItemPatch { Label = "Laptop (new)" },
            Expected = new JsonObject { ["label"] = "Notebook" },
        });
        Assert.Contains(result.Errors, e => e.Code == "overlay.conflict");
    }

    [Fact]
    public void A_tenant_state_adds_its_status_option_and_a_transition_can_use_it()
    {
        var result = Compile(
            "1.2.0",
            new AddWorkflowState { Workflow = RequestFlow, State = new WorkflowState { Id = "tenant__awaiting_info", Category = StateCategories.Pending }, Label = "Awaiting information", Tone = "accent.amber" },
            new AddTransition { Workflow = RequestFlow, Transition = new WorkflowTransition { Id = "tenant__ask", Label = "Ask for information", From = ["submitted"], To = "tenant__awaiting_info" } },
            new AddTransition { Workflow = RequestFlow, Transition = new WorkflowTransition { Id = "tenant__resume", Label = "Resume", From = ["tenant__awaiting_info"], To = "submitted" } });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var flow = result.Config.Workflows.Find(w => w.Id == RequestFlow)!;
        Assert.NotNull(flow.FindState("tenant__awaiting_info"));
        Assert.NotNull(result.Config.FindChoiceList("core.tickets.request_status")!.Find("tenant__awaiting_info"));
        Assert.Contains($"{RequestFlow}/tenant__ask", result.Config.Customized);
    }

    [Fact]
    public void Base_transitions_accept_only_cosmetic_changes_and_system_transitions_stay_enabled()
    {
        var disable = Compile("1.2.0", new UpdateTransition { Workflow = RequestFlow, Transition = "reject", Set = new TransitionPatch { Enabled = false, Label = "Decline" } });
        Assert.True(disable.Succeeded, string.Join("\n", disable.Errors.Select(e => e.Message)));
        Assert.False(disable.Config.Workflows.Find(w => w.Id == RequestFlow)!.FindTransition("reject")!.Enabled);

        var reroute = Compile("1.2.0", new UpdateTransition { Workflow = RequestFlow, Transition = "reject", Set = new TransitionPatch { To = "cancelled" } });
        Assert.Equal("overlay.base_immutable", Assert.Single(reroute.Errors).Code);

        var system = Compile("1.2.0", new UpdateTransition { Workflow = RequestFlow, Transition = "complete", Set = new TransitionPatch { Enabled = false } });
        Assert.Equal("overlay.system_transition", Assert.Single(system.Errors).Code);
    }

    [Fact]
    public void Active_instance_policies_are_validated()
    {
        var badMap = Compile("1.2.0", new SetActiveInstancePolicy
        {
            Workflow = RequestFlow,
            Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["submitted"] = "triage" } },
        });
        Assert.Contains(badMap.Errors, e => e.Code == "config.not_found" && e.Message.Contains("triage", StringComparison.Ordinal));

        var badCancel = Compile("1.2.0", new SetActiveInstancePolicy
        {
            Workflow = RequestFlow,
            Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Cancel, CancelState = "fulfilled" },
        });
        Assert.Contains(badCancel.Errors, e => e.Code == "config.bad_policy");

        var ok = Compile("1.2.0", new SetActiveInstancePolicy
        {
            Workflow = RequestFlow,
            Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Cancel, CancelState = "cancelled" },
        });
        Assert.True(ok.Succeeded);
    }

    [Fact]
    public void Operations_describe_themselves_for_chat_steps_and_round_trip_as_json()
    {
        OverlayOp op = new AddCatalogItem { Item = TenantItem() };
        Assert.Equal("Add catalog item \"Parking permit\" with 2 questions and 2 tasks", op.Describe());
        var json = SpecJson.Serialize<OverlayOp>(op);
        Assert.Contains("\"op\":\"catalog_item.add\"", json, StringComparison.Ordinal);
        var back = Assert.IsType<AddCatalogItem>(SpecJson.Deserialize<OverlayOp>(json));
        Assert.Equal(2, back.Item.Tasks.Count);
    }
}
