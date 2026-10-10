using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Approvals;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Requests;

/// <summary>
/// REQ-BIZ-004, REQ-AUTHZ-011, REQ-ACC-05 (approval part), REQ-AISAFE-001: approvals between submission and
/// fulfillment with sequential and parallel stages, conditional stages, rejection, separation of duties, delegation,
/// expiry and escalation, cancellation, pinned policies, approver read access, and tenant isolation (ADR 0016).
/// </summary>
[Collection(PlatformCollection.Name)]
public class ApprovalTests(PlatformFixture f)
{
    private const string Request = "core.tickets.request";
    private const string Task = "core.tickets.request_task";
    private const string Laptop = "core.tickets.catalog.new_laptop";
    private const string Access = "core.tickets.catalog.software_access";
    private const string Onboarding = "core.tickets.catalog.onboarding";

    private static readonly JsonObject LaptopAnswers = new() { ["model"] = "standard", ["justification"] = "New starter" };

    private RecordService Records => f.Get<RecordService>();

    private ApprovalService Approvals => f.Get<ApprovalService>();

    private async Task DrainAsync()
    {
        var runner = f.Get<JobRunner>();
        var dispatcher = f.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0 | await runner.RunOnceAsync(default) > 0)
        {
        }
    }

    private async Task<RecordView> SubmitAsync(ProvisionedTenant t, string item, JsonObject answers, string user = "dana")
    {
        var created = await Records.CreateAsync(f.As(t, user), Request, new JsonObject { ["catalog_item"] = item, ["variables"] = answers.DeepClone() }, null, null, default);
        await DrainAsync();
        return await Records.GetAsync(f.As(t, "agent"), Request, created.Id, default);
    }

    private static string Status(RecordView r) => r.Data["status"]!.GetValue<string>();

    private async Task<RecordApprovalView> ApprovalAsync(ProvisionedTenant t, Guid id, string viewer = "agent") =>
        (await Approvals.ForRecordAsync(f.As(t, viewer), Request, id, default))[0];

    private async Task<ApprovalStepView> PendingStepAsync(ProvisionedTenant t, Guid id, string viewer = "agent") =>
        (await ApprovalAsync(t, id, viewer)).Stages.SelectMany(s => s.Steps).First(s => s.Status == "pending");

    private async Task DecideAsync(ProvisionedTenant t, string user, Guid stepId, string decision = "approved", string? comment = null)
    {
        await Approvals.DecideAsync(f.As(t, user), stepId, decision, comment, default);
        await DrainAsync();
    }

    private async Task<RecordView> GetAsync(ProvisionedTenant t, Guid id) => await Records.GetAsync(f.As(t, "agent"), Request, id, default);

    private async Task<List<RecordView>> TasksAsync(ProvisionedTenant t, Guid id) =>
        (await Records.QueryAsync(f.As(t, "agent"), new RecordQuery { Entity = Task, Filters = [$"record.request == '{id}'"], PageSize = 50 }, default)).Items.ToList();

    private async Task PublishAsync(ProvisionedTenant t, params OverlayOp[] ops)
    {
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var v = (await drafts.OpenAsync(admin, default)).Version;
        foreach (var op in ops)
        {
            v = (await drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var state = await drafts.CurrentAsync(admin, default);
        Assert.True(state!.ArtifactHash is not null, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        await f.Get<PublishService>().PublishAsync(admin, state.ArtifactHash!, state.PublishedRevision, default);
    }

    [Fact]
    public async Task A_request_waits_for_approval_and_fulfillment_starts_once_it_is_approved()
    {
        var t = await f.NewTenantAsync("appr", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        Assert.Equal("awaiting_approval", Status(request));
        Assert.Empty(await TasksAsync(t, request.Id));

        // Nobody can skip the approval by starting fulfillment by hand.
        var early = await Assert.ThrowsAsync<ValidationFailedException>(() => Records.TransitionAsync(f.As(t, "agent"), Request, request.Id, request.Revision, "start_fulfillment", [], null, null, default));
        Assert.Contains(early.Errors, e => e.Code is "invalid_transition" or "approval_required");

        var approval = await ApprovalAsync(t, request.Id);
        Assert.Equal("pending", approval.Status);
        Assert.Equal("Manager approval", approval.Stages[0].Label);
        var step = await PendingStepAsync(t, request.Id, "jamie");
        Assert.True(step.CanDecide);

        await DecideAsync(t, "jamie", step.Id);
        var approved = await GetAsync(t, request.Id);
        Assert.Equal("fulfilling", Status(approved));
        Assert.Equal(3, (await TasksAsync(t, request.Id)).Count);
        var done = await ApprovalAsync(t, request.Id);
        Assert.Equal("approved", done.Status);
        Assert.Equal("Jamie " + PlatformFixture.Slugs[t.TenantId], done.Stages[0].Steps.Single(s => s.Status == "approved").DecidedBy);
    }

    [Fact]
    public async Task A_request_without_an_approval_policy_goes_straight_to_the_agents()
    {
        var t = await f.NewTenantAsync("noappr", "1.3.0");
        var request = await SubmitAsync(t, Onboarding, new JsonObject { ["new_hire_name"] = "Ada", ["start_date"] = "2026-12-01", ["department"] = "engineering" });
        Assert.Equal("submitted", Status(request));
        Assert.Empty(await Approvals.ForRecordAsync(f.As(t, "agent"), Request, request.Id, default));
        await Records.TransitionAsync(f.As(t, "agent"), Request, request.Id, request.Revision, "start_fulfillment", [], null, null, default);
    }

    [Fact]
    public async Task Stages_run_in_order_and_a_conditional_stage_runs_only_when_its_condition_holds()
    {
        var t = await f.NewTenantAsync("stages", "1.3.0");
        var read = await SubmitAsync(t, Access, new JsonObject { ["application"] = "crm", ["access_level"] = "read", ["reason"] = "Sales" });
        await DecideAsync(t, "jamie", (await PendingStepAsync(t, read.Id)).Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, read.Id)));
        Assert.Equal("skipped", (await ApprovalAsync(t, read.Id)).Stages[1].Status);

        var admin = await SubmitAsync(t, Access, new JsonObject { ["application"] = "erp", ["access_level"] = "admin", ["reason"] = "Finance close" });
        Assert.Equal("waiting", (await ApprovalAsync(t, admin.Id)).Stages[1].Status);
        await DecideAsync(t, "quinn", (await PendingStepAsync(t, admin.Id)).Id);
        Assert.Equal("awaiting_approval", Status(await GetAsync(t, admin.Id)));
        var security = await PendingStepAsync(t, admin.Id, "builder");
        Assert.True(security.CanDecide);
        Assert.False((await PendingStepAsync(t, admin.Id, "jamie")).CanDecide);
        var notYours = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "jamie"), security.Id, "approved", null, default));
        Assert.Equal(403, notYours.Status);

        await DecideAsync(t, "builder", security.Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, admin.Id)));
    }

    [Fact]
    public async Task An_all_stage_needs_every_approver_and_one_person_decides_one_step_per_stage()
    {
        var t = await f.NewTenantAsync("parallel", "1.3.0");
        await PublishAsync(
            t,
            new AddApprovalPolicy
            {
                Policy = new ApprovalPolicySpec
                {
                    Id = "tenant__two_desks",
                    Label = "Both desks",
                    Entity = Request,
                    Stages =
                    [
                        new ApprovalStageSpec
                        {
                            Id = "desks",
                            Label = "Both desks",
                            Mode = ApprovalModes.All,
                            Approvers = [new ApproverSpec { Kind = ApproverKinds.Group, Ref = "service_desk" }, new ApproverSpec { Kind = ApproverKinds.Group, Ref = "network" }],
                        },
                    ],
                },
            },
            new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "tenant__two_desks" } });

        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var steps = (await ApprovalAsync(t, request.Id)).Stages[0].Steps;
        Assert.Equal(2, steps.Count);

        // The agent is in both groups: one decision only. Sam (service desk only) decides the other step.
        var network = steps.Single(s => s.Approver == "Network");
        var desk = steps.Single(s => s.Approver == "Service Desk");
        await DecideAsync(t, "agent", network.Id);
        Assert.Equal("awaiting_approval", Status(await GetAsync(t, request.Id)));
        var twice = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "agent"), desk.Id, "approved", null, default));
        Assert.Equal(403, twice.Status);
        await DecideAsync(t, "sam", desk.Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, request.Id)));
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_declines_the_request_and_creates_no_tasks()
    {
        var t = await f.NewTenantAsync("decline", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var step = await PendingStepAsync(t, request.Id);
        var silent = await Assert.ThrowsAsync<ValidationFailedException>(() => Approvals.DecideAsync(f.As(t, "jamie"), step.Id, "rejected", "  ", default));
        Assert.Contains(silent.Errors, e => e.Field == "comment");

        await DecideAsync(t, "jamie", step.Id, "rejected", "Use the spare in the cupboard.");
        Assert.Equal("rejected", Status(await GetAsync(t, request.Id)));
        Assert.Empty(await TasksAsync(t, request.Id));
        var view = await ApprovalAsync(t, request.Id, "dana");
        Assert.Equal("rejected", view.Status);
        Assert.Equal("Use the spare in the cupboard.", view.Stages[0].Steps.Single(s => s.Status == "rejected").Comment);

        var again = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "quinn"), view.Stages[0].Steps[0].Id, "approved", null, default));
        Assert.Equal(409, again.Status);
    }

    [Fact]
    public async Task Requesters_never_approve_their_own_request_even_as_approvers_or_through_a_delegate()
    {
        var t = await f.NewTenantAsync("sod", "1.3.0");
        var own = await SubmitAsync(t, Laptop, LaptopAnswers, user: "jamie");
        var step = await PendingStepAsync(t, own.Id);
        var self = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "jamie"), step.Id, "approved", null, default));
        Assert.Equal(403, self.Status);
        Assert.False((await PendingStepAsync(t, own.Id, "jamie")).CanDecide);
        Assert.DoesNotContain((await Approvals.InboxAsync(f.As(t, "jamie"), default)).Pending, i => i.RecordId == own.Id);

        // Jamie delegates to Riley: Riley may act for Jamie, but never on Jamie's own request.
        await Approvals.DelegateAsync(f.As(t, "jamie"), t.Users["riley"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), default);
        var viaDelegate = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "riley"), step.Id, "approved", null, default));
        Assert.Equal(403, viaDelegate.Status);

        await DecideAsync(t, "quinn", step.Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, own.Id)));
    }

    [Fact]
    public async Task A_delegate_decides_on_behalf_of_an_absent_approver_until_the_delegation_ends()
    {
        var t = await f.NewTenantAsync("deleg", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);

        // Riley is a requester, not an approver: no access before the delegation.
        await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(f.As(t, "riley"), Request, request.Id, default));
        var delegation = await Approvals.DelegateAsync(f.As(t, "quinn"), t.Users["riley"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), default);
        await DrainAsync();
        Assert.True(delegation.Active);

        var inbox = await Approvals.InboxAsync(f.As(t, "riley"), default);
        var item = Assert.Single(inbox.Pending, i => i.RecordId == request.Id);
        Assert.Equal("Quinn " + PlatformFixture.Slugs[t.TenantId], item.OnBehalfOf);
        Assert.NotNull(item.Record);

        var self = await Assert.ThrowsAsync<ValidationFailedException>(() => Approvals.DelegateAsync(f.As(t, "riley"), t.Users["riley"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), default));
        Assert.Contains(self.Errors, e => e.Field == "delegate");

        await DecideAsync(t, "riley", item.StepId);
        var view = await ApprovalAsync(t, request.Id);
        var decided = view.Stages[0].Steps.Single(s => s.Status == "approved");
        Assert.Equal("Riley " + PlatformFixture.Slugs[t.TenantId], decided.DecidedBy);
        Assert.Equal("Quinn " + PlatformFixture.Slugs[t.TenantId], decided.OnBehalfOf);

        // After revoking, Riley acts for nobody.
        await Approvals.RevokeDelegationAsync(f.As(t, "quinn"), delegation.Id, default);
        var next = await SubmitAsync(t, Laptop, LaptopAnswers);
        var nextStep = await PendingStepAsync(t, next.Id);
        var later = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "riley"), nextStep.Id, "approved", null, default));
        Assert.Equal(403, later.Status);
    }

    [Fact]
    public async Task An_expired_stage_escalates_once_and_then_declines_the_request()
    {
        var t = await f.NewTenantAsync("expire", "1.3.0");
        var db = f.Get<Database>();
        var env = t.Environments["prod"];
        async Task ExpireNowAsync()
        {
            await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.Jobs.Where(j => j.Kind == ApprovalService.ExpireJobKind && j.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1)), default), default);
            await DrainAsync();
        }

        var escalated = await SubmitAsync(t, Laptop, LaptopAnswers);
        await ExpireNowAsync();
        var view = await ApprovalAsync(t, escalated.Id);
        var escalation = Assert.Single(view.Stages[0].Steps, s => s.Escalation);
        Assert.Equal("pending", escalation.Status);
        Assert.Equal("Service Desk", escalation.Approver);

        // The escalation replaces the original approvers: their steps expired, and the service desk decides.
        Assert.All(view.Stages[0].Steps.Where(s => !s.Escalation), s => Assert.Equal("expired", s.Status));
        Assert.True((await ApprovalAsync(t, escalated.Id, "agent")).Stages[0].Steps.Single(s => s.Escalation).CanDecide);
        await DecideAsync(t, "agent", escalation.Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, escalated.Id)));

        var ignored = await SubmitAsync(t, Laptop, LaptopAnswers);
        await ExpireNowAsync();
        await ExpireNowAsync();
        Assert.Equal("rejected", Status(await GetAsync(t, ignored.Id)));
        var final = await ApprovalAsync(t, ignored.Id);
        Assert.Equal("rejected", final.Status);
        Assert.Contains("expired", final.OutcomeReason, StringComparison.Ordinal);
        Assert.All(final.Stages[0].Steps, s => Assert.Equal("expired", s.Status));
    }

    [Fact]
    public async Task Cancelling_a_request_cancels_its_pending_approval()
    {
        var t = await f.NewTenantAsync("cancelappr", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var mine = await Records.GetAsync(f.As(t, "dana"), Request, request.Id, default);
        await Records.TransitionAsync(f.As(t, "dana"), Request, request.Id, mine.Revision, "cancel", [], null, null, default);
        await DrainAsync();

        var view = await ApprovalAsync(t, request.Id);
        Assert.Equal("cancelled", view.Status);
        Assert.All(view.Stages[0].Steps, s => Assert.Equal("cancelled", s.Status));
        Assert.DoesNotContain((await Approvals.InboxAsync(f.As(t, "jamie"), default)).Pending, i => i.RecordId == request.Id);
        var timer = $"{ApprovalService.ExpireJobKind}:{view.Id}:0";
        var jobs = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.CountAsync(j => j.DedupKey == timer && j.Status == "pending"), default);
        Assert.Equal(0, jobs);
    }

    [Fact]
    public async Task Approvals_in_flight_keep_their_policy_when_it_changes()
    {
        var t = await f.NewTenantAsync("pinned", "1.3.0");
        var before = await SubmitAsync(t, Laptop, LaptopAnswers);
        await PublishAsync(
            t,
            new AddApprovalPolicy
            {
                Policy = new ApprovalPolicySpec
                {
                    Id = "tenant__network",
                    Label = "Network sign-off",
                    Entity = Request,
                    Stages = [new ApprovalStageSpec { Id = "network", Label = "Network sign-off", Approvers = [new ApproverSpec { Kind = ApproverKinds.Group, Ref = "network" }] }],
                },
            },
            new UpdateCatalogItem { Item = Laptop, Set = new CatalogItemPatch { ApprovalPolicy = "tenant__network" } });

        var after = await SubmitAsync(t, Laptop, LaptopAnswers);
        Assert.Equal("Manager approval", (await ApprovalAsync(t, before.Id)).PolicyLabel);
        Assert.Equal("Network sign-off", (await ApprovalAsync(t, after.Id)).PolicyLabel);
        await DecideAsync(t, "jamie", (await PendingStepAsync(t, before.Id)).Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, before.Id)));
    }

    [Fact]
    public async Task Approvers_read_the_requests_they_decide_but_cannot_change_them()
    {
        var t = await f.NewTenantAsync("readers", "1.3.0");
        var ticket = await Records.CreateAsync(f.As(t, "dana"), "core.tickets.ticket", new JsonObject { ["title"] = "Printer jam", ["description"] = "Paper stuck" }, null, null, default);
        await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(f.As(t, "jamie"), "core.tickets.ticket", ticket.Id, default));

        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var seen = await Records.GetAsync(f.As(t, "jamie"), Request, request.Id, default);
        Assert.Equal("standard", seen.Data["variables"]!["model"]!.GetValue<string>());
        var change = await Assert.ThrowsAnyAsync<KernelException>(() => Records.UpdateAsync(f.As(t, "jamie"), Request, request.Id, seen.Revision, new JsonObject { ["description"] = "Make it a MacBook" }, null, null, default));
        Assert.True(change.Status is 403 or 422, $"status {change.Status}");
        var list = await Records.QueryAsync(f.As(t, "jamie"), new RecordQuery { Entity = Request, PageSize = 50 }, default);
        Assert.Contains(list.Items, r => r.Id == request.Id);
    }

    [Fact]
    public async Task AI_agents_cannot_decide_and_other_workspaces_see_nothing()
    {
        var t = await f.NewTenantAsync("aiappr", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var step = await PendingStepAsync(t, request.Id);
        var agent = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "jamie", agent: Guid.NewGuid()), step.Id, "approved", null, default));
        Assert.Equal(403, agent.Status);

        var other = await f.NewTenantAsync("aiappr2", "1.3.0");
        var hidden = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(other, "jamie"), step.Id, "approved", null, default));
        Assert.Equal(404, hidden.Status);
        Assert.Empty((await Approvals.InboxAsync(f.As(other, "jamie"), default)).Pending);
        await Assert.ThrowsAsync<KernelException>(() => Approvals.ForRecordAsync(f.As(other, "agent"), Request, request.Id, default));
        Assert.Equal("awaiting_approval", Status(await GetAsync(t, request.Id)));
    }

    private static ApprovalPolicySpec TenantPolicy(string id, params ApprovalStageSpec[] stages) => new() { Id = id, Label = id, Entity = Request, Stages = [.. stages] };

    private async Task UsePolicyAsync(ProvisionedTenant t, ApprovalPolicySpec policy, string item = Laptop) =>
        await PublishAsync(t, new AddApprovalPolicy { Policy = policy }, new UpdateCatalogItem { Item = item, Set = new CatalogItemPatch { ApprovalPolicy = policy.Id } });

    private async Task ExpireTimersAsync(ProvisionedTenant t, string kind = ApprovalService.ExpireJobKind)
    {
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, ctx => ctx.Jobs.Where(j => j.Kind == kind && j.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1)), default), default);
        await DrainAsync();
    }

    [Fact]
    public async Task The_person_a_request_is_for_cannot_approve_it()
    {
        var t = await f.NewTenantAsync("forwhom", "1.3.0");
        var created = await Records.CreateAsync(f.As(t, "dana"), Request, new JsonObject { ["catalog_item"] = Laptop, ["variables"] = LaptopAnswers.DeepClone(), ["requested_for"] = t.Users["jamie"].ToString() }, null, null, default);
        await DrainAsync();
        var step = await PendingStepAsync(t, created.Id);
        var self = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "jamie"), step.Id, "approved", null, default));
        Assert.Equal(403, self.Status);
        Assert.DoesNotContain((await Approvals.InboxAsync(f.As(t, "jamie"), default)).Pending, i => i.RecordId == created.Id);

        // Changing who it is for after submission is refused, so the snapshot and the record agree.
        var mine = await Records.GetAsync(f.As(t, "dana"), Request, created.Id, default);
        var redirect = await Assert.ThrowsAsync<ValidationFailedException>(() => Records.UpdateAsync(f.As(t, "dana"), Request, created.Id, mine.Revision, new JsonObject { ["requested_for"] = t.Users["riley"].ToString() }, null, null, default));
        Assert.Contains(redirect.Errors, e => e.Field == "requested_for" && e.Code == "read_only");
        var notes = await Assert.ThrowsAsync<ValidationFailedException>(() => Records.UpdateAsync(f.As(t, "dana"), Request, created.Id, mine.Revision, new JsonObject { ["description"] = "Make it two laptops" }, null, null, default));
        Assert.Contains(notes.Errors, e => e.Field == "description");

        await DecideAsync(t, "quinn", step.Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, created.Id)));
    }

    [Fact]
    public async Task People_cannot_step_around_a_pending_approval_with_their_own_transitions()
    {
        var t = await f.NewTenantAsync("around", "1.3.0");
        const string Flow = "core.tickets.request_lifecycle";
        await PublishAsync(
            t,
            new AddWorkflowState { Workflow = Flow, State = new WorkflowState { Id = "tenant__triage", Category = StateCategories.New }, Label = "Triage", Tone = "accent.blue" },
            new AddTransition { Workflow = Flow, Transition = new WorkflowTransition { Id = "tenant__triage", Label = "Triage", From = ["submitted"], To = "tenant__triage", Roles = ["core.tickets.agent"] } },
            new AddTransition { Workflow = Flow, Transition = new WorkflowTransition { Id = "tenant__fast_track", Label = "Fast track", From = ["awaiting_approval", "tenant__triage"], To = "fulfilling", Roles = ["core.tickets.agent"] } });

        // Before the approval engine picks it up, the request is in intake: only reject or cancel are open to people.
        var fresh = await Records.CreateAsync(f.As(t, "dana"), Request, new JsonObject { ["catalog_item"] = Laptop, ["variables"] = LaptopAnswers.DeepClone() }, null, null, default);
        var triage = await Assert.ThrowsAsync<ValidationFailedException>(() => Records.TransitionAsync(f.As(t, "agent"), Request, fresh.Id, fresh.Revision, "tenant__triage", [], null, null, default));
        Assert.Contains(triage.Errors, e => e.Code == "approval_required");
        await DrainAsync();

        var waiting = await GetAsync(t, fresh.Id);
        Assert.Equal("awaiting_approval", Status(waiting));
        var fast = await Assert.ThrowsAsync<ValidationFailedException>(() => Records.TransitionAsync(f.As(t, "agent"), Request, fresh.Id, waiting.Revision, "tenant__fast_track", [], null, null, default));
        Assert.Contains(fast.Errors, e => e.Code == "approval_pending");
        Assert.Empty(await TasksAsync(t, fresh.Id));
    }

    [Fact]
    public async Task An_escalation_decides_alone_and_its_rejection_rejects_the_request()
    {
        var t = await f.NewTenantAsync("escrej", "1.3.0");
        await UsePolicyAsync(t, TenantPolicy(
            "tenant__two_keys",
            new ApprovalStageSpec
            {
                Id = "both",
                Label = "Both keys",
                Mode = ApprovalModes.All,
                Approvers = [new ApproverSpec { Kind = ApproverKinds.Role, Ref = "core.tickets.approver" }, new ApproverSpec { Kind = ApproverKinds.Group, Ref = "network" }],
                ExpiresAfterHours = 24,
                OnExpiry = ApprovalExpiry.Escalate,
                EscalateTo = [new ApproverSpec { Kind = ApproverKinds.Group, Ref = "service_desk" }],
            }));
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var role = (await ApprovalAsync(t, request.Id)).Stages[0].Steps.Single(s => s.ApproverKind == "role");
        await DecideAsync(t, "jamie", role.Id);
        await ExpireTimersAsync(t);

        var escalation = (await ApprovalAsync(t, request.Id)).Stages[0].Steps.Single(s => s.Escalation);
        await DecideAsync(t, "admin", escalation.Id, "rejected", "Not in this quarter's budget.");
        Assert.Equal("rejected", Status(await GetAsync(t, request.Id)));
        Assert.Empty(await TasksAsync(t, request.Id));
    }

    [Fact]
    public async Task An_unescalated_stage_that_expires_declines_the_request()
    {
        var t = await f.NewTenantAsync("exprej", "1.3.0");
        await UsePolicyAsync(t, TenantPolicy("tenant__quick", new ApprovalStageSpec { Id = "quick", Label = "Quick check", Approvers = [new ApproverSpec { Kind = ApproverKinds.Role, Ref = "core.tickets.approver" }], ExpiresAfterHours = 1 }));
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        await ExpireTimersAsync(t);
        Assert.Equal("rejected", Status(await GetAsync(t, request.Id)));
        Assert.Contains("expired", (await ApprovalAsync(t, request.Id)).OutcomeReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delegator_cannot_decide_a_second_step_after_their_delegate_decided_one()
    {
        var t = await f.NewTenantAsync("double", "1.3.0");
        await UsePolicyAsync(t, TenantPolicy("tenant__desks", new ApprovalStageSpec
        {
            Id = "desks",
            Label = "Both desks",
            Mode = ApprovalModes.All,
            Approvers = [new ApproverSpec { Kind = ApproverKinds.Group, Ref = "service_desk" }, new ApproverSpec { Kind = ApproverKinds.Group, Ref = "network" }],
        }));
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        await Approvals.DelegateAsync(f.As(t, "agent"), t.Users["riley"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2), default);
        var steps = (await ApprovalAsync(t, request.Id)).Stages[0].Steps;
        await DecideAsync(t, "riley", steps.Single(s => s.Approver == "Network").Id);

        var second = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "agent"), steps.Single(s => s.Approver == "Service Desk").Id, "approved", null, default));
        Assert.Equal(403, second.Status);
        var audited = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AnyAsync(a => a.Operation == "approval.decide" && a.Outcome == "denied" && a.TargetId == request.Id.ToString()), default);
        Assert.True(audited, "denied decisions are audited");
    }

    [Fact]
    public async Task A_delegate_reads_requests_only_while_the_delegation_is_active_and_never_through_a_chain()
    {
        var t = await f.NewTenantAsync("window", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        var later = await Approvals.DelegateAsync(f.As(t, "quinn"), t.Users["riley"], DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3), default);
        await DrainAsync();
        Assert.False(later.Active);
        await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(f.As(t, "riley"), Request, request.Id, default));

        // The delegation starts (its timer fires): Riley can read and decide for Quinn.
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.ApprovalDelegations.Where(d => d.Id == later.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.StartsAt, DateTimeOffset.UtcNow.AddMinutes(-1)), default), default);
        var startKey = $"{ApprovalService.DelegationJobKind}:{later.Id}:start";
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.Where(j => j.DedupKey == startKey).ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1)), default), default);
        await DrainAsync();
        Assert.Equal(request.Id, (await Records.GetAsync(f.As(t, "riley"), Request, request.Id, default)).Id);

        // Riley passes it on to Jordan: Jordan cannot act for Quinn.
        await Approvals.DelegateAsync(f.As(t, "riley"), t.Users["jordan"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), default);
        var step = await PendingStepAsync(t, request.Id);
        var chained = await Assert.ThrowsAsync<KernelException>(() => Approvals.DecideAsync(f.As(t, "jordan"), step.Id, "approved", null, default));
        Assert.Equal(403, chained.Status);

        // Revoking withdraws the access Riley had only as Quinn's delegate.
        await Approvals.RevokeDelegationAsync(f.As(t, "quinn"), later.Id, default);
        await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(f.As(t, "riley"), Request, request.Id, default));
        var tooFar = await Assert.ThrowsAsync<ValidationFailedException>(() => Approvals.DelegateAsync(f.As(t, "quinn"), t.Users["riley"], DateTimeOffset.UtcNow.AddDays(120), DateTimeOffset.UtcNow.AddDays(121), default));
        Assert.Contains(tooFar.Errors, e => e.Field == "endsAt");
    }

    [Fact]
    public async Task People_cannot_write_the_approvers_field()
    {
        var t = await f.NewTenantAsync("apprfield", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        foreach (var user in new[] { "dana", "agent" })
        {
            var current = await Records.GetAsync(f.As(t, user), Request, request.Id, default);
            var refused = await Assert.ThrowsAsync<ValidationFailedException>(() => Records.UpdateAsync(f.As(t, user), Request, request.Id, current.Revision, new JsonObject { ["approvers"] = new JsonArray(t.Users[user].ToString()) }, null, null, default));
            Assert.Contains(refused.Errors, e => e.Field == "approvers" && e.Code == "read_only");
        }
    }

    [Fact]
    public async Task Requests_in_intake_go_for_approval_when_their_item_gains_a_policy()
    {
        var t = await f.NewTenantAsync("gains", "1.3.0");
        var request = await SubmitAsync(t, Onboarding, new JsonObject { ["new_hire_name"] = "Ada", ["start_date"] = "2026-12-01", ["department"] = "engineering" });
        Assert.Equal("submitted", Status(request));

        await PublishAsync(t, new UpdateCatalogItem { Item = Onboarding, Set = new CatalogItemPatch { ApprovalPolicy = "core.tickets.approval.manager" } });
        await DrainAsync();
        Assert.Equal("awaiting_approval", Status(await GetAsync(t, request.Id)));
        Assert.Equal("pending", (await ApprovalAsync(t, request.Id)).Status);
    }

    [Fact]
    public async Task Migrating_the_workflow_keeps_pending_approvals_and_they_still_complete()
    {
        var t = await f.NewTenantAsync("migappr", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        await PublishAsync(
            t,
            new AddWorkflowState { Workflow = "core.tickets.request_lifecycle", State = new WorkflowState { Id = "tenant__on_hold", Category = StateCategories.Pending }, Label = "On hold", Tone = "accent.amber" },
            new SetActiveInstancePolicy { Workflow = "core.tickets.request_lifecycle", Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate } });
        await DrainAsync();

        Assert.Equal("awaiting_approval", Status(await GetAsync(t, request.Id)));
        await DecideAsync(t, "jamie", (await PendingStepAsync(t, request.Id)).Id);
        Assert.Equal("fulfilling", Status(await GetAsync(t, request.Id)));
        Assert.Equal(3, (await TasksAsync(t, request.Id)).Count);
    }

    [Fact]
    public async Task Approvals_stay_in_their_environment_and_admins_review_every_delegation()
    {
        var t = await f.NewTenantAsync("envappr", "1.3.0");
        var request = await SubmitAsync(t, Laptop, LaptopAnswers);
        Assert.DoesNotContain((await Approvals.InboxAsync(f.As(t, "jamie", env: "sandbox"), default)).Pending, i => i.RecordId == request.Id);
        await Assert.ThrowsAsync<KernelException>(() => Approvals.ForRecordAsync(f.As(t, "agent", env: "sandbox"), Request, request.Id, default));

        await Approvals.DelegateAsync(f.As(t, "quinn"), t.Users["riley"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2), default);
        Assert.Contains(await Approvals.DelegationsAsync(f.As(t, "admin"), default, all: true), d => d.DelegatorId == t.Users["quinn"]);
        var notAdmin = await Assert.ThrowsAsync<KernelException>(() => Approvals.DelegationsAsync(f.As(t, "jamie"), default, all: true));
        Assert.Equal(403, notAdmin.Status);
    }
}
