using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-EVT-008 (ADR 0017): the event type registry, declarative emits on transitions and rules, and their validation.</summary>
public class EventTypeTests
{
    private const string Ticket = "core.tickets.ticket";
    private const string Lifecycle = "core.tickets.lifecycle";

    private static EventTypeSpec Reopened(string id = "tenant__ticket_reopened") => new()
    {
        Id = id,
        Label = "Ticket reopened",
        SubjectEntity = Ticket,
        Fields = new() { ["priority"] = new EventFieldSpec { Type = EventFieldTypes.String, Required = true }, ["count"] = new EventFieldSpec { Type = EventFieldTypes.Number } },
        Severity = EventSeverities.Medium,
    };

    private static UpdateTransition Emit(string transition, EventEmitSpec emit) =>
        new() { Workflow = Lifecycle, Transition = transition, Set = new TransitionPatch { Emits = [emit] } };

    [Fact]
    public void Release_1_4_0_declares_ticket_events_and_publishes_them_from_transitions_and_rules()
    {
        var result = Compile("1.4.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.NotNull(cfg.FindEventType("core.tickets.ticket.escalated"));
        Assert.Contains(cfg.WorkflowFor(Ticket)!.Transitions.Single(t => t.Id == "escalate").Emits, e => e.Type == "core.tickets.ticket.escalated");
        Assert.Contains(cfg.Rules.Single(r => r.Id == "core.tickets.rules.p1_raised").Actions, a => a.Type == RuleActionTypes.EmitEvent);

        // Platform events are registered by the platform module.
        Assert.NotNull(cfg.FindEventType(Sdk.Events.EventTypes.ApprovalRequested));
        Assert.NotNull(cfg.FindEventType(Sdk.Events.EventTypes.RecordCreated));

        // The graph knows who publishes what, so removing an event type shows its publishers.
        var graph = DependencyGraph.Build(cfg);
        Assert.Contains("core.tickets.rules.p1_raised", graph.ImpactOf("core.tickets.ticket.priority_raised"));
    }

    [Fact]
    public void A_tenant_event_type_compiles_and_a_transition_can_publish_it()
    {
        var result = Compile("1.4.0", new AddEventType { EventType = Reopened() }, Emit("reopen", new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priority"] = "record.priority" } }));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Contains("tenant__ticket_reopened", result.Config.Customized);
    }

    [Fact]
    public void Event_types_are_validated_with_suggestions()
    {
        var bad = Reopened();
        bad.Severity = "urgent";
        bad.Sensitivity = "secret";
        bad.SubjectEntity = "core.tickets.tiket";
        bad.Fields["when"] = new EventFieldSpec { Type = "date" };
        var result = Compile("1.4.0", new AddEventType { EventType = bad });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_severity");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_sensitivity");
        Assert.Contains(result.Errors, e => e.Code == "config.not_found" && e.Suggestion == Ticket);
        Assert.Contains(result.Errors, e => e.Code == "config.bad_field_type");

        var large = Reopened();
        for (var i = 0; i < 31; i++)
        {
            large.Fields[$"f{i}"] = new EventFieldSpec { Type = EventFieldTypes.String };
        }

        Assert.Contains(Compile("1.4.0", new AddEventType { EventType = large }).Errors, e => e.Code == "config.too_large");
    }

    [Fact]
    public void Tenants_add_only_tenant_event_types_and_cannot_redefine_one()
    {
        string Status(params OverlayOp[] ops) => Compile("1.4.0", ops).Composition.Outcomes[^1].Status;
        Assert.Equal(OpStatus.Failed, Status(new AddEventType { EventType = Reopened("core.tickets.ticket.reopened") }));
        Assert.Equal(OpStatus.Failed, Status(new AddEventType { EventType = Reopened() }, new AddEventType { EventType = Reopened() }));
        Assert.Equal(OpStatus.Failed, Status(new RemoveEventType { EventType = "core.tickets.ticket.escalated" }));
        Assert.Equal(OpStatus.Applied, Status(new AddEventType { EventType = Reopened() }, new RemoveEventType { EventType = "tenant__ticket_reopened" }));
    }

    [Fact]
    public void Emits_must_name_a_known_type_about_the_same_entity_and_fill_its_required_fields()
    {
        var unknown = Compile("1.4.0", Emit("reopen", new EventEmitSpec { Type = "core.tickets.ticket.escalate", Payload = new() { ["priority"] = "record.priority" } }));
        Assert.Contains(unknown.Errors, e => e.Code == "config.not_found" && e.Suggestion == "core.tickets.ticket.escalated");

        var incomplete = Compile("1.4.0", new AddEventType { EventType = Reopened() }, Emit("reopen", new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priorty"] = "record.priority" } }));
        Assert.Contains(incomplete.Errors, e => e.Code == "config.unknown_payload_field" && e.Suggestion == "priority");
        Assert.Contains(incomplete.Errors, e => e.Code == "config.missing_payload_field");

        var badExpression = Compile("1.4.0", new AddEventType { EventType = Reopened() }, Emit("reopen", new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priority"] = "record.prioritee" } }));
        Assert.False(badExpression.Succeeded);

        var other = Reopened("tenant__task_done");
        other.SubjectEntity = "core.tickets.request_task";
        var wrongSubject = Compile("1.4.0", new AddEventType { EventType = other }, Emit("reopen", new EventEmitSpec { Type = "tenant__task_done", Payload = new() { ["priority"] = "record.priority" } }));
        Assert.Contains(wrongSubject.Errors, e => e.Code == "config.bad_event_subject");
    }

    [Fact]
    public void Tenants_add_events_to_a_module_transition_but_never_drop_the_modules_own()
    {
        var resolve = Compile("1.4.0").Config.WorkflowFor(Ticket)!.Transitions.Single(t => t.Id == "resolve");
        var mine = new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priority"] = "record.priority" } };
        var added = Compile("1.4.0", new AddEventType { EventType = Reopened() }, Emit("resolve", mine));
        Assert.Equal(OpStatus.Failed, added.Composition.Outcomes[^1].Status);

        var both = new UpdateTransition { Workflow = Lifecycle, Transition = "resolve", Set = new TransitionPatch { Emits = [.. resolve.Emits, mine] } };
        var kept = Compile("1.4.0", new AddEventType { EventType = Reopened() }, both);
        Assert.True(kept.Succeeded, string.Join("\n", kept.Errors.Select(e => e.Message)));
        Assert.Equal(["core.tickets.ticket.resolved", "tenant__ticket_reopened"], kept.Config.WorkflowFor(Ticket)!.Transitions.Single(t => t.Id == "resolve").Emits.Select(e => e.Type));
    }

    [Fact]
    public void Removing_an_event_type_that_is_still_published_fails_validation()
    {
        var result = Compile(
            "1.4.0",
            new AddEventType { EventType = Reopened() },
            Emit("reopen", new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priority"] = "record.priority" } }),
            new RemoveEventType { EventType = "tenant__ticket_reopened" });
        Assert.Contains(result.Errors, e => e.Code == "config.not_found" && e.Message.Contains("tenant__ticket_reopened", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_platform_publishes_platform_events_and_modules_stay_in_their_namespace()
    {
        var forged = Compile("1.4.0", Emit("reopen", new EventEmitSpec { Type = Sdk.Events.EventTypes.ApprovalApproved, Payload = new() { ["policy"] = "'x'" } }));
        Assert.Contains(forged.Errors, e => e.Code == "config.platform_event");
        var record = Compile("1.4.0", Emit("reopen", new EventEmitSpec { Type = Sdk.Events.EventTypes.RecordTransitioned, Payload = new() { ["to"] = "'resolved'" } }));
        Assert.Contains(record.Errors, e => e.Code == "config.platform_event");

        var tickets = Tickets("1.4.0");
        tickets.EventTypes.Add(new EventTypeSpec { Id = "platform.approval.bypassed", Label = "Bypassed" });
        tickets.EventTypes.Add(new EventTypeSpec { Id = "core.billing.invoice.paid", Label = "Paid" });
        var billing = new ModuleSpec
        {
            Id = "core.billing",
            Version = "1.0.0",
            Name = "Billing",
            Rules =
            [
                new RuleSpec
                {
                    Id = "core.billing.rules.borrowed_event",
                    Kind = RuleKinds.Conditional,
                    Entity = Ticket,
                    When = "record.priority == 'p2'",
                    Actions = [new RuleActionSpec { Type = RuleActionTypes.EmitEvent, Emit = new EventEmitSpec { Type = "core.tickets.ticket.priority_raised", Payload = new() { ["priority"] = "record.priority" } } }],
                },
            ],
        };
        var borrowed = Compiler.Compile([PlatformSpec, Tickets("1.4.0"), billing], []);
        Assert.Contains(borrowed.Errors, e => e.Code == "config.foreign_event" && e.Target == "core.billing.rules.borrowed_event");

        var result = Compiler.Compile([PlatformSpec, tickets], []);
        Assert.Contains(result.Errors, e => e.Code == "config.reserved_event_type" && e.Target == "platform.approval.bypassed");
        Assert.Contains(result.Errors, e => e.Code == "config.foreign_event_type" && e.Target == "core.billing.invoice.paid");
    }

    [Fact]
    public void Payloads_keep_hidden_fields_out_unless_the_event_is_restricted_and_match_declared_types()
    {
        // estimated_effort is hidden from requesters.
        var leaky = Reopened();
        leaky.Fields["effort"] = new EventFieldSpec { Type = EventFieldTypes.Number };
        var emit = new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priority"] = "record.priority", ["effort"] = "record.estimated_effort" } };
        Assert.Contains(Compile("1.4.0", new AddEventType { EventType = leaky }, Emit("reopen", emit)).Errors, e => e.Code == "config.payload_hidden_field");

        var restricted = Reopened();
        restricted.Fields["effort"] = new EventFieldSpec { Type = EventFieldTypes.Number };
        restricted.Sensitivity = EventSensitivities.Restricted;
        Assert.DoesNotContain(Compile("1.4.0", new AddEventType { EventType = restricted }, Emit("reopen", emit)).Errors, e => e.Code == "config.payload_hidden_field");

        var mismatch = Compile("1.4.0", new AddEventType { EventType = Reopened() }, Emit("reopen", new EventEmitSpec { Type = "tenant__ticket_reopened", Payload = new() { ["priority"] = "record.priority", ["count"] = "record.title" } }));
        Assert.Contains(mismatch.Errors, e => e.Code == "config.payload_type_mismatch");
    }

    [Fact]
    public void Form_rules_cannot_publish_events()
    {
        var rule = new RuleSpec
        {
            Id = "tenant__rules.form_emit",
            Kind = RuleKinds.Conditional,
            Scope = RuleScopes.Form,
            Entity = Ticket,
            When = "record.priority == 'p2'",
            Actions = [new RuleActionSpec { Type = RuleActionTypes.EmitEvent, Emit = new EventEmitSpec { Type = "core.tickets.ticket.priority_raised", Payload = new() { ["priority"] = "record.priority" } } }],
        };
        Assert.Contains(Compile("1.4.0", new AddRule { Rule = rule }).Errors, e => e.Code == "config.emit_needs_data_scope");
    }

    [Fact]
    public void A_rule_emit_event_action_needs_an_event()
    {
        var rule = new RuleSpec
        {
            Id = "tenant__rules.vip",
            Kind = RuleKinds.Conditional,
            Scope = RuleScopes.Data,
            Entity = Ticket,
            When = "record.priority == 'p2'",
            Actions = [new RuleActionSpec { Type = RuleActionTypes.EmitEvent }],
        };
        Assert.Contains(Compile("1.4.0", new AddRule { Rule = rule }).Errors, e => e.Code == "config.missing_event");
    }

    [Fact]
    public void Emissions_evaluate_payload_and_severity_and_skip_values_that_fail()
    {
        var cfg = Compile("1.4.0").Config;
        var entity = cfg.FindEntity(Ticket)!;
        var emit = cfg.WorkflowFor(Ticket)!.Transitions.Single(t => t.Id == "escalate").Emits[0];
        var vars = new Dictionary<string, object?>
        {
            ["record"] = new Dictionary<string, object?> { ["priority"] = "p2", ["escalation_reason"] = "Needs network" },
            ["prior"] = new Dictionary<string, object?> { ["status"] = "assigned" },
        };
        var e = EventEmission.Evaluate(cfg, entity, emit, vars, "core.tickets.lifecycle/escalate")!;
        Assert.Equal(EventSeverities.High, e.Severity);
        Assert.Equal("p2", e.Payload["priority"]!.GetValue<string>());
        Assert.Equal("assigned", e.Payload["from"]!.GetValue<string>());

        var reasonless = EventEmission.Evaluate(cfg, entity, emit, new Dictionary<string, object?>
        {
            ["record"] = new Dictionary<string, object?> { ["priority"] = "p2", ["escalation_reason"] = new string('x', 5000) },
            ["prior"] = new Dictionary<string, object?> { ["status"] = "assigned" },
        }, "core.tickets.lifecycle/escalate")!;
        Assert.Equal(EventEmission.MaxText, reasonless.Payload["reason"]!.GetValue<string>().Length);

        ((Dictionary<string, object?>)vars["record"]!)["priority"] = "p1";
        Assert.Equal(EventSeverities.Critical, EventEmission.Evaluate(cfg, entity, emit, vars, "core.tickets.lifecycle/escalate")!.Severity);
    }
}
