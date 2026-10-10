using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>
/// REQ-MIG-003, REQ-ACC-05 (workflow part): workflow definitions are versioned at publish; running instances stay
/// pinned, migrate through an explicit state map, or are cancelled, as the published policy says (ADR 0015).
/// </summary>
[Collection(PlatformCollection.Name)]
public class WorkflowVersioningTests(PlatformFixture f)
{
    private const string Request = "core.tickets.request";
    private const string Flow = "core.tickets.request_lifecycle";
    private const string Laptop = "core.tickets.catalog.new_laptop";

    private static readonly JsonObject LaptopAnswers = new() { ["model"] = "standard", ["justification"] = "Needed" };

    private async Task PublishAsync(ProvisionedTenant t, params OverlayOp[] ops)
    {
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var v = draft.Version;
        foreach (var op in ops)
        {
            v = (await drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var state = await drafts.CurrentAsync(admin, default);
        Assert.True(state!.ArtifactHash is not null, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        await f.Get<PublishService>().PublishAsync(admin, state.ArtifactHash!, state.PublishedRevision, default);
    }

    private async Task<RecordView> SubmitAsync(ProvisionedTenant t) =>
        await f.Get<RecordService>().CreateAsync(f.As(t, "dana"), Request, new JsonObject { ["catalog_item"] = Laptop, ["variables"] = LaptopAnswers.DeepClone() }, null, null, default);

    private Task<RecordView> GetAsync(ProvisionedTenant t, Guid id) => f.Get<RecordService>().GetAsync(f.As(t, "agent"), Request, id, default);

    private async Task<int?> PinnedVersionAsync(ProvisionedTenant t, Guid id) =>
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Records.Where(r => r.Id == id).Select(r => r.WorkflowVersion).FirstAsync(), default);

    private async Task RunJobsAsync()
    {
        var runner = f.Get<JobRunner>();
        while (await runner.RunOnceAsync(default) > 0)
        {
        }

        var dispatcher = f.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }
    }

    private static OverlayOp AddState(string id, string category, string label) =>
        new AddWorkflowState { Workflow = Flow, State = new WorkflowState { Id = id, Category = category }, Label = label, Tone = "accent.blue" };

    private static OverlayOp AddTransition(string id, string label, string[] from, string to) =>
        new AddTransition { Workflow = Flow, Transition = new WorkflowTransition { Id = id, Label = label, From = [.. from], To = to, Roles = ["core.tickets.agent"] } };

    [Fact]
    public async Task Running_instances_stay_on_their_version_while_new_ones_use_the_new_definition()
    {
        var t = await f.NewTenantAsync("pin", "1.2.0");
        var before = await SubmitAsync(t);
        var v1 = await PinnedVersionAsync(t, before.Id);
        Assert.NotNull(v1);

        await PublishAsync(
            t,
            AddState("tenant__triage", StateCategories.New, "Triage"),
            AddTransition("tenant__triage", "Send to triage", ["submitted"], "tenant__triage"));

        var after = await SubmitAsync(t);
        Assert.Equal(v1 + 1, await PinnedVersionAsync(t, after.Id));
        Assert.Equal(v1, await PinnedVersionAsync(t, before.Id));

        // The old instance keeps the old shape; the new one gets the new definition.
        var old = await GetAsync(t, before.Id);
        Assert.Contains(old.Transitions!, x => x.Id == "reject");
        Assert.DoesNotContain(old.Transitions!, x => x.Id == "tenant__triage");
        var fresh = await GetAsync(t, after.Id);
        Assert.Contains(fresh.Transitions!, x => x.Id == "tenant__triage");
        var refused = await Assert.ThrowsAsync<ValidationFailedException>(() => f.Get<RecordService>().TransitionAsync(f.As(t, "agent"), Request, before.Id, old.Revision, "tenant__triage", [], null, null, default));
        Assert.Contains(refused.Errors, e => e.Code == "unknown_transition");
    }

    [Fact]
    public async Task Disabling_or_restricting_a_transition_applies_to_running_instances_too()
    {
        // Authorization uses current rules: who may fire a transition is policy, not part of a pinned version.
        var t = await f.NewTenantAsync("restrict", "1.2.0");
        await PublishAsync(t, new AddTransition { Workflow = Flow, Transition = new WorkflowTransition { Id = "tenant__nudge", Label = "Nudge", From = ["submitted"], To = "submitted" } });
        var running = await SubmitAsync(t);
        var pinned = await PinnedVersionAsync(t, running.Id);
        var records = f.Get<RecordService>();
        running = await records.TransitionAsync(f.As(t, "dana"), Request, running.Id, running.Revision, "tenant__nudge", [], null, null, default);

        await PublishAsync(
            t,
            AddState("tenant__parked", StateCategories.Pending, "Parked"),
            new UpdateTransition { Workflow = Flow, Transition = "tenant__nudge", Set = new TransitionPatch { Roles = ["core.tickets.agent"] } },
            new UpdateTransition { Workflow = Flow, Transition = "reject", Set = new TransitionPatch { Enabled = false } });
        Assert.Equal(pinned, await PinnedVersionAsync(t, running.Id));

        var forbidden = await Assert.ThrowsAsync<KernelException>(() => records.TransitionAsync(f.As(t, "dana"), Request, running.Id, running.Revision, "tenant__nudge", [], null, null, default));
        Assert.Equal(403, forbidden.Status);
        var view = await GetAsync(t, running.Id);
        Assert.DoesNotContain(view.Transitions!, x => x.Id == "reject");
        var disabled = await Assert.ThrowsAsync<ValidationFailedException>(() => records.TransitionAsync(f.As(t, "agent"), Request, running.Id, view.Revision, "reject", [], null, null, default));
        Assert.Contains(disabled.Errors, e => e.Code == "transition_disabled");
    }

    [Fact]
    public async Task A_migrate_policy_applies_only_to_the_publish_that_sets_it()
    {
        var t = await f.NewTenantAsync("once", "1.2.0");
        var first = await SubmitAsync(t);
        await PublishAsync(
            t,
            AddState("tenant__triage", StateCategories.New, "Triage"),
            AddTransition("tenant__accept", "Accept", ["tenant__triage"], "submitted"),
            new SetActiveInstancePolicy { Workflow = Flow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["submitted"] = "tenant__triage" } } });
        await RunJobsAsync();
        Assert.Equal("tenant__triage", (await GetAsync(t, first.Id)).Data["status"]!.GetValue<string>());

        var second = await SubmitAsync(t);
        var pinned = await PinnedVersionAsync(t, second.Id);
        await PublishAsync(t, AddState("tenant__parked", StateCategories.Pending, "Parked"));
        await RunJobsAsync();

        Assert.Equal("submitted", (await GetAsync(t, second.Id)).Data["status"]!.GetValue<string>());
        Assert.Equal(pinned, await PinnedVersionAsync(t, second.Id));
    }

    [Fact]
    public async Task Migrating_requests_in_fulfillment_keeps_their_tasks_and_they_still_complete()
    {
        // Master spec "Migration and recovery": preserve fulfillment tasks across a workflow migration.
        var t = await f.NewTenantAsync("keep", "1.2.0");
        var records = f.Get<RecordService>();
        var request = await SubmitAsync(t);
        await records.TransitionAsync(f.As(t, "agent"), Request, request.Id, request.Revision, "start_fulfillment", [], null, null, default);
        await RunJobsAsync();
        var before = await PinnedVersionAsync(t, request.Id);

        await PublishAsync(
            t,
            AddState("tenant__triage", StateCategories.New, "Triage"),
            new SetActiveInstancePolicy { Workflow = Flow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["submitted"] = "tenant__triage" } } });
        await RunJobsAsync();
        Assert.NotEqual(before, await PinnedVersionAsync(t, request.Id));
        Assert.Equal("fulfilling", (await GetAsync(t, request.Id)).Data["status"]!.GetValue<string>());

        foreach (var key in new[] { "order", "image", "deliver" })
        {
            var page = await records.QueryAsync(f.As(t, "agent"), new RecordQuery { Entity = "core.tickets.request_task", Filters = [$"record.request == '{request.Id}'", $"record.task_key == '{key}'"] }, default);
            var task = Assert.Single(page.Items);
            Assert.Equal("open", task.Data["status"]!.GetValue<string>());
            await records.TransitionAsync(f.As(t, "agent"), "core.tickets.request_task", task.Id, task.Revision, "complete", [], null, null, default);
            await RunJobsAsync();
        }

        Assert.Equal("fulfilled", (await GetAsync(t, request.Id)).Data["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Migrate_policy_moves_running_instances_through_the_state_map_and_leaves_finished_ones()
    {
        var t = await f.NewTenantAsync("mig", "1.2.0");
        var running = await SubmitAsync(t);
        var finished = await SubmitAsync(t);
        var records = f.Get<RecordService>();
        await records.TransitionAsync(f.As(t, "agent"), Request, finished.Id, finished.Revision, "reject", [], null, null, default);

        await PublishAsync(
            t,
            AddState("tenant__triage", StateCategories.New, "Triage"),
            AddTransition("tenant__accept", "Accept", ["tenant__triage"], "submitted"),
            new SetActiveInstancePolicy { Workflow = Flow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["submitted"] = "tenant__triage" } } });
        await RunJobsAsync();

        var moved = await GetAsync(t, running.Id);
        Assert.Equal("tenant__triage", moved.Data["status"]!.GetValue<string>());
        Assert.Contains(moved.Transitions!, x => x.Id == "tenant__accept");
        Assert.Equal("rejected", (await GetAsync(t, finished.Id)).Data["status"]!.GetValue<string>());
        Assert.NotEqual(await PinnedVersionAsync(t, running.Id), await PinnedVersionAsync(t, finished.Id));

        var history = await records.HistoryAsync(f.As(t, "admin"), Request, running.Id, default);
        Assert.Contains(history, h => h.Operation == "record.workflow_migrate");
    }

    [Fact]
    public async Task Cancel_policy_cancels_running_instances_and_their_fulfillment_tasks()
    {
        var t = await f.NewTenantAsync("can", "1.2.0");
        var request = await SubmitAsync(t);
        await f.Get<RecordService>().TransitionAsync(f.As(t, "agent"), Request, request.Id, request.Revision, "start_fulfillment", [], null, null, default);
        await RunJobsAsync();

        await PublishAsync(
            t,
            AddTransition("tenant__escalate", "Escalate", ["fulfilling"], "fulfilling"),
            new SetActiveInstancePolicy { Workflow = Flow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Cancel, CancelState = "cancelled" } });
        await RunJobsAsync();

        Assert.Equal("cancelled", (await GetAsync(t, request.Id)).Data["status"]!.GetValue<string>());
        var tasks = await f.Get<RecordService>().QueryAsync(f.As(t, "agent"), new RecordQuery { Entity = "core.tickets.request_task", Filters = [$"record.request == '{request.Id}'"] }, default);
        Assert.NotEmpty(tasks.Items);
        Assert.All(tasks.Items, x => Assert.Equal("skipped", x.Data["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Draft_review_shows_running_instances_and_what_the_policy_does_to_them()
    {
        var t = await f.NewTenantAsync("imp", "1.2.0");
        await SubmitAsync(t);
        await SubmitAsync(t);
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var v = (await drafts.OpenAsync(admin, default)).Version;
        v = (await drafts.ApplyAsync(admin, AddState("tenant__triage", StateCategories.New, "Triage"), v, "ui", default)).Version;
        v = (await drafts.ApplyAsync(admin, new SetActiveInstancePolicy { Workflow = Flow, Policy = new ActiveInstancePolicy { Policy = ActiveInstancePolicies.Migrate, StateMap = new() { ["submitted"] = "tenant__triage" } } }, v, "ui", default)).Version;

        var state = await drafts.CurrentAsync(admin, default);
        var impact = Assert.Single(state!.WorkflowImpact!);
        Assert.Equal(Flow, impact.Workflow);
        Assert.Equal(ActiveInstancePolicies.Migrate, impact.Policy);
        Assert.True(impact.Running >= 2, $"{impact.Running} running");
        Assert.Equal(impact.Running, impact.Moving);
        Assert.Equal(0, impact.StayingPinned);
    }

    [Fact]
    public async Task Records_created_before_versioning_are_pinned_to_the_definition_they_ran_on()
    {
        var t = await f.NewTenantAsync("legacy", "1.2.0");
        var legacy = await SubmitAsync(t);
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Records.Where(r => r.Id == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.WorkflowVersion, (int?)null)), default);

        await PublishAsync(t, AddState("tenant__triage", StateCategories.New, "Triage"), AddTransition("tenant__triage", "Send to triage", ["submitted"], "tenant__triage"));

        Assert.NotNull(await PinnedVersionAsync(t, legacy.Id));
        Assert.DoesNotContain((await GetAsync(t, legacy.Id)).Transitions!, x => x.Id == "tenant__triage");
    }

    [Fact]
    public async Task Workflow_definitions_are_isolated_per_tenant_environment()
    {
        var a = await f.NewTenantAsync("wfa", "1.2.0");
        var b = await f.NewTenantAsync("wfb", "1.2.0");
        await SubmitAsync(a);
        await SubmitAsync(b);
        var db = f.Get<Database>();
        var seenByA = await db.InTenantAsync(a.TenantId, a.Environments["prod"], null, ctx => ctx.WorkflowDefinitions.Select(d => d.TenantId).Distinct().ToListAsync(), default);
        Assert.Equal([a.TenantId], seenByA);
        var sandbox = await db.InTenantAsync(a.TenantId, a.Environments["sandbox"], null, ctx => ctx.WorkflowDefinitions.Where(d => d.EnvironmentId == a.Environments["prod"]).CountAsync(), default);
        Assert.Equal(0, sandbox);
    }
}
