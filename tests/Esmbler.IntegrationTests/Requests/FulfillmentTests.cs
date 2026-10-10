using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Events;

namespace Esmbler.IntegrationTests.Requests;

/// <summary>
/// REQ-BIZ-004, REQ-EVT-004, REQ-ACC-05 (fulfillment part), REQ-ACC-06: catalog requests with typed variables,
/// fulfillment tasks with dependencies and conditions, partial completion, rejection and cancellation, idempotent
/// reconciliation, and authorization of requesters versus agents (ADR 0015).
/// </summary>
[Collection(PlatformCollection.Name)]
public class FulfillmentTests(PlatformFixture f)
{
    private const string Request = "core.tickets.request";
    private const string Task = "core.tickets.request_task";
    private const string Laptop = "core.tickets.catalog.new_laptop";
    private const string Access = "core.tickets.catalog.software_access";
    private const string Onboarding = "core.tickets.catalog.onboarding";

    private async Task DrainAsync()
    {
        var dispatcher = f.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }
    }

    private async Task<List<RecordView>> TasksAsync(ProvisionedTenant tenant, Guid requestId)
    {
        var page = await f.Get<RecordService>().QueryAsync(f.As(tenant, "agent"), new RecordQuery { Entity = Task, Filters = [$"record.request == '{requestId}'"], PageSize = 50 }, default);
        return page.Items.ToList();
    }

    private static string Key(RecordView task) => task.Data["task_key"]!.GetValue<string>();

    private static string Status(RecordView r) => r.Data["status"]!.GetValue<string>();

    private async Task<RecordView> SubmitAsync(ProvisionedTenant tenant, string item, JsonObject variables, string user = "dana")
    {
        var records = f.Get<RecordService>();
        return await records.CreateAsync(f.As(tenant, user), Request, new JsonObject { ["catalog_item"] = item, ["variables"] = variables }, null, null, default);
    }

    private async Task<RecordView> StartAsync(ProvisionedTenant tenant, RecordView request)
    {
        var started = await f.Get<RecordService>().TransitionAsync(f.As(tenant, "agent"), Request, request.Id, request.Revision, "start_fulfillment", [], null, null, default);
        await DrainAsync();
        return started;
    }

    private async Task CompleteAsync(ProvisionedTenant tenant, RecordView task, string transition = "complete")
    {
        await f.Get<RecordService>().TransitionAsync(f.As(tenant, "agent"), Task, task.Id, task.Revision, transition, [], null, null, default);
        await DrainAsync();
    }

    [Fact]
    public async Task Tasks_run_in_dependency_order_and_the_request_completes_when_all_are_done()
    {
        var t = await f.NewTenantAsync("ful", "1.2.0");
        var request = await SubmitAsync(t, Laptop, new JsonObject { ["model"] = "mac", ["accessories"] = new JsonArray("dock"), ["needed_by"] = "2026-11-02", ["justification"] = "Design work" });
        Assert.Equal("New laptop", request.Data["title"]!.GetValue<string>());
        Assert.Equal("submitted", Status(request));
        Assert.Empty(await TasksAsync(t, request.Id));

        await StartAsync(t, request);
        var tasks = await TasksAsync(t, request.Id);
        Assert.Equal(["deliver", "image", "order"], tasks.Select(Key).Order());
        Assert.Equal("open", Status(tasks.Single(x => Key(x) == "order")));
        Assert.Equal("blocked", Status(tasks.Single(x => Key(x) == "image")));
        Assert.Equal(t.Groups["service_desk"].ToString(), tasks[0].Data["assignment_group"]!.GetValue<string>());

        await CompleteAsync(t, tasks.Single(x => Key(x) == "order"));
        tasks = await TasksAsync(t, request.Id);
        Assert.Equal("open", Status(tasks.Single(x => Key(x) == "image")));
        Assert.Equal("blocked", Status(tasks.Single(x => Key(x) == "deliver")));

        await CompleteAsync(t, tasks.Single(x => Key(x) == "image"));
        await CompleteAsync(t, (await TasksAsync(t, request.Id)).Single(x => Key(x) == "deliver"));

        var done = await f.Get<RecordService>().GetAsync(f.As(t, "dana"), Request, request.Id, default);
        Assert.Equal("fulfilled", Status(done));
        Assert.Equal("complete", done.Data["fulfillment_outcome"]!.GetValue<string>());
        Assert.NotNull(done.Data["fulfilled_at"]);
    }

    [Fact]
    public async Task Variables_are_typed_and_validated_on_every_write_path()
    {
        var t = await f.NewTenantAsync("var", "1.2.0");
        var bad = await Assert.ThrowsAsync<ValidationFailedException>(() => SubmitAsync(t, Laptop, new JsonObject
        {
            ["model"] = "commodore",
            ["needed_by"] = "next tuesday",
            ["colour"] = "red",
        }));
        Assert.Contains(bad.Errors, e => e.Field == "variables.model" && e.Code == "invalid_option");
        Assert.Contains(bad.Errors, e => e.Field == "variables.needed_by");
        Assert.Contains(bad.Errors, e => e.Field == "variables.colour" && e.Code == "unknown_variable");
        Assert.Contains(bad.Errors, e => e.Field == "variables.justification" && e.Code == "required");

        var ok = await SubmitAsync(t, Onboarding, new JsonObject { ["new_hire_name"] = "  Ada  ", ["start_date"] = "2026-12-01", ["department"] = "engineering" });
        Assert.Equal("Ada", ok.Data["variables"]!["new_hire_name"]!.GetValue<string>());
        Assert.True(ok.Data["variables"]!["needs_laptop"]!.GetValue<bool>());

        var change = await Assert.ThrowsAsync<ValidationFailedException>(() => f.Get<RecordService>().UpdateAsync(f.As(t, "dana"), Request, ok.Id, ok.Revision, new JsonObject { ["catalog_item"] = Laptop }, null, null, default));
        Assert.Contains(change.Errors, e => e.Field == "catalog_item");

        var unknown = await Assert.ThrowsAsync<ValidationFailedException>(() => SubmitAsync(t, "core.tickets.catalog.spaceship", []));
        Assert.Contains(unknown.Errors, e => e.Field == "catalog_item" && e.Code == "unknown_catalog_item");
    }

    [Fact]
    public async Task Conditional_tasks_are_created_only_when_their_condition_holds()
    {
        var t = await f.NewTenantAsync("cond", "1.2.0");
        var read = await StartAsync(t, await SubmitAsync(t, Access, new JsonObject { ["application"] = "crm", ["access_level"] = "read", ["reason"] = "Sales" }));
        var tasks = await TasksAsync(t, read.Id);
        Assert.Equal(["grant", "notify"], tasks.Select(Key).Order());
        Assert.Equal("open", Status(tasks.Single(x => Key(x) == "grant")));

        var admin = await StartAsync(t, await SubmitAsync(t, Access, new JsonObject { ["application"] = "erp", ["access_level"] = "admin", ["reason"] = "Finance close" }));
        tasks = await TasksAsync(t, admin.Id);
        Assert.Equal(["grant", "notify", "security_review"], tasks.Select(Key).Order());
        Assert.Equal("blocked", Status(tasks.Single(x => Key(x) == "grant")));
        Assert.Equal("open", Status(tasks.Single(x => Key(x) == "security_review")));
    }

    [Fact]
    public async Task Skipped_tasks_release_their_dependents_and_make_the_outcome_partial()
    {
        var t = await f.NewTenantAsync("skip", "1.2.0");
        var request = await StartAsync(t, await SubmitAsync(t, Onboarding, new JsonObject { ["new_hire_name"] = "Lin", ["start_date"] = "2026-12-01", ["department"] = "sales", ["needs_laptop"] = false }));
        var tasks = await TasksAsync(t, request.Id);
        Assert.Equal(["accounts", "badge", "welcome"], tasks.Select(Key).Order());

        await CompleteAsync(t, tasks.Single(x => Key(x) == "accounts"));
        await CompleteAsync(t, tasks.Single(x => Key(x) == "badge"), "skip");
        var welcome = (await TasksAsync(t, request.Id)).Single(x => Key(x) == "welcome");
        Assert.Equal("open", Status(welcome));
        await CompleteAsync(t, welcome);

        var done = await f.Get<RecordService>().GetAsync(f.As(t, "agent"), Request, request.Id, default);
        Assert.Equal("fulfilled", Status(done));
        Assert.Equal("partial", done.Data["fulfillment_outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancelling_a_request_cancels_its_unfinished_tasks()
    {
        var t = await f.NewTenantAsync("cancel", "1.2.0");
        var request = await StartAsync(t, await SubmitAsync(t, Laptop, new JsonObject { ["model"] = "standard", ["justification"] = "Broken screen" }));
        var tasks = await TasksAsync(t, request.Id);
        await CompleteAsync(t, tasks.Single(x => Key(x) == "order"));
        var current = await f.Get<RecordService>().GetAsync(f.As(t, "dana"), Request, request.Id, default);
        await f.Get<RecordService>().TransitionAsync(f.As(t, "dana"), Request, request.Id, current.Revision, "cancel", [], null, null, default);
        await DrainAsync();

        tasks = await TasksAsync(t, request.Id);
        Assert.Equal("done", Status(tasks.Single(x => Key(x) == "order")));
        Assert.All(tasks.Where(x => Key(x) != "order"), x => Assert.Equal("skipped", Status(x)));
        var after = await f.Get<RecordService>().GetAsync(f.As(t, "agent"), Request, request.Id, default);
        Assert.Equal("cancelled", Status(after));
    }

    [Fact]
    public async Task Replayed_and_duplicate_events_never_create_duplicate_tasks()
    {
        var t = await f.NewTenantAsync("dup", "1.2.0");
        var request = await StartAsync(t, await SubmitAsync(t, Laptop, new JsonObject { ["model"] = "standard", ["justification"] = "New starter" }));
        Assert.Equal(3, (await TasksAsync(t, request.Id)).Count);

        // A duplicate of the start event (new event ID, so the inbox does not catch it) and a stale task event.
        var consumer = f.Get<IEnumerable<IEventConsumer>>().OfType<FulfillmentConsumer>().Single();
        var db = f.Get<Database>();
        EventEnvelope Duplicate(Guid record, string entity) => new()
        {
            EventId = Guid.NewGuid(),
            Type = EventTypes.RecordTransitioned,
            TenantId = t.TenantId,
            EnvironmentId = t.Environments["prod"],
            Entity = entity,
            RecordId = record,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Actor = $"user:{t.Users["agent"]}",
            Payload = new JsonObject { ["to"] = "fulfilling" },
        };
        var tasks = await TasksAsync(t, request.Id);
        foreach (var e in new[] { Duplicate(request.Id, Request), Duplicate(request.Id, Request), Duplicate(tasks[0].Id, Task) })
        {
            await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, ctx => consumer.HandleAsync(e, ctx, default), default);
        }

        await DrainAsync();
        tasks = await TasksAsync(t, request.Id);
        Assert.Equal(3, tasks.Count);
        Assert.Single(tasks, x => Status(x) == "open");
    }

    [Fact]
    public async Task Requesters_cannot_fulfil_or_see_tasks_and_other_tenants_see_nothing()
    {
        var t = await f.NewTenantAsync("authz", "1.2.0");
        var request = await SubmitAsync(t, Laptop, new JsonObject { ["model"] = "standard", ["justification"] = "Mine" });
        var records = f.Get<RecordService>();
        var forbidden = await Assert.ThrowsAsync<KernelException>(() => records.TransitionAsync(f.As(t, "dana"), Request, request.Id, request.Revision, "start_fulfillment", [], null, null, default));
        Assert.Equal(403, forbidden.Status);
        var system = await Assert.ThrowsAsync<ValidationFailedException>(() => records.TransitionAsync(f.As(t, "agent"), Request, request.Id, request.Revision, "complete", [], null, null, default));
        Assert.Contains(system.Errors, e => e.Code == "unknown_transition");

        await StartAsync(t, request);
        var task = (await TasksAsync(t, request.Id))[0];
        var denied = await Assert.ThrowsAsync<KernelException>(() => records.GetAsync(f.As(t, "dana"), Task, task.Id, default));
        Assert.Equal(404, denied.Status);

        var other = await f.NewTenantAsync("other", "1.2.0");
        var hidden = await Assert.ThrowsAsync<KernelException>(() => records.GetAsync(f.As(other, "agent"), Request, request.Id, default));
        Assert.Equal(404, hidden.Status);
        Assert.Empty((await records.QueryAsync(f.As(other, "agent"), new RecordQuery { Entity = Task, PageSize = 50 }, default)).Items);
    }
}
