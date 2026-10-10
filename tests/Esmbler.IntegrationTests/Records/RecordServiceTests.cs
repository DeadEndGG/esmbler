using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;

namespace Esmbler.IntegrationTests.Records;

/// <summary>REQ-REC-003/004/007, REQ-EVT-001, REQ-AUTHZ-002/004/005, REQ-BIZ-002: the record engine end to end on real Postgres.</summary>
[Collection(PlatformCollection.Name)]
public class RecordServiceTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    private RecordService Records => f.Get<RecordService>();

    [Fact]
    public async Task Demo_tenants_have_sample_tickets_with_numbers()
    {
        var page = await Records.QueryAsync(f.As(f.Acme, "alex"), new RecordQuery { Entity = Ticket, PageSize = 100 }, default);
        Assert.True(page.Total >= 9);
        Assert.All(page.Items, r => Assert.StartsWith("TCK-", r.Number));
        Assert.Contains(page.Items, r => r.Display.ContainsKey("requester"));
    }

    [Fact]
    public async Task Requester_sees_only_related_tickets_and_no_hidden_fields()
    {
        var dana = f.As(f.Acme, "dana");
        var page = await Records.QueryAsync(dana, new RecordQuery { Entity = Ticket, PageSize = 100 }, default);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, r => Assert.Equal(f.Acme.Users["dana"].ToString(), r.Data["requester"]!.GetValue<string>()));
        Assert.All(page.Items, r => Assert.False(r.Data.ContainsKey("estimated_effort")));
        Assert.Equal(page.Items.Count, page.Total);

        var ex = await Assert.ThrowsAsync<KernelException>(() => Records.QueryAsync(dana, new RecordQuery { Entity = Ticket, Filters = ["record.estimated_effort == '4'"] }, default));
        Assert.Equal("invalid_filter", ex.Code);
    }

    [Fact]
    public async Task Create_update_conflict_and_history()
    {
        var sam = f.As(f.Acme, "sam");
        var created = await Records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Keyboard missing keys", ["requester"] = f.Acme.Users["jordan"].ToString(), ["category"] = "hardware" }, null, null, default);
        Assert.Equal(1, created.Revision);
        Assert.Equal("new", created.Data["status"]!.GetValue<string>());
        Assert.Equal(f.Acme.Groups["service_desk"].ToString(), created.Data["assignment_group"]!.GetValue<string>());

        var updated = await Records.UpdateAsync(sam, Ticket, created.Id, 1, new JsonObject { ["priority"] = "p2" }, null, null, default);
        Assert.Equal(2, updated.Revision);

        var stale = await Assert.ThrowsAsync<KernelException>(() => Records.UpdateAsync(sam, Ticket, created.Id, 1, new JsonObject { ["priority"] = "p4" }, null, null, default));
        Assert.Equal("record_conflict", stale.Code);
        Assert.Equal(409, stale.Status);

        var history = await Records.HistoryAsync(sam, Ticket, created.Id, default);
        Assert.Equal(2, history.Count);
        Assert.Contains(history[0].Changes, c => c.Field == "priority" && c.To!.GetValue<string>() == "p2");
    }

    [Fact]
    public async Task Idempotency_key_replays_the_same_result()
    {
        var sam = f.As(f.Acme, "sam");
        var key = Guid.NewGuid().ToString();
        var body = new JsonObject { ["title"] = "Retry me please", ["requester"] = f.Acme.Users["dana"].ToString() };
        var first = await Records.CreateAsync(sam, Ticket, body, null, key, default);
        var second = await Records.CreateAsync(sam, Ticket, (JsonObject)body.DeepClone(), null, key, default);
        Assert.Equal(first.Id, second.Id);
        var reused = await Assert.ThrowsAsync<KernelException>(() => Records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Different body" }, null, key, default));
        Assert.Equal("idempotency_key_reused", reused.Code);
    }

    [Fact]
    public async Task Requester_cannot_read_others_tickets_by_id()
    {
        var sam = f.As(f.Acme, "sam");
        var other = await Records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Private to Jordan", ["requester"] = f.Acme.Users["jordan"].ToString() }, null, null, default);
        var ex = await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(f.As(f.Acme, "dana"), Ticket, other.Id, default));
        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task Work_notes_are_hidden_from_requesters()
    {
        var sam = f.As(f.Acme, "sam");
        var t = await Records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Laptop battery swelling", ["requester"] = f.Acme.Users["riley"].ToString() }, null, null, default);
        var sanitizer = f.Get<IRichTextSanitizer>();
        await Records.AddActivityAsync(sam, Ticket, t.Id, "work_note", "Internal: vendor RMA 4471", sanitizer, default);
        await Records.AddActivityAsync(sam, Ticket, t.Id, "public_reply", "<p>We ordered a replacement.</p><script>alert(1)</script>", sanitizer, default);
        var riley = f.As(f.Acme, "riley");
        var seen = await Records.ActivityAsync(riley, Ticket, t.Id, default);
        Assert.Single(seen);
        Assert.Equal("public_reply", seen[0].Kind);
        Assert.DoesNotContain("<script", seen[0].Body);
        await Assert.ThrowsAsync<KernelException>(() => Records.AddActivityAsync(riley, Ticket, t.Id, "work_note", "sneaky", sanitizer, default));
    }

    [Fact]
    public async Task Transition_flow_resolve_and_confirm_by_requester()
    {
        var sam = f.As(f.Acme, "sam");
        var t = await Records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Monitor flicker", ["requester"] = f.Acme.Users["dana"].ToString() }, null, null, default);
        var started = await Records.TransitionAsync(sam, Ticket, t.Id, t.Revision, "start", [], null, null, default);
        Assert.Equal(f.Acme.Users["sam"].ToString(), started.Data["assignee"]!.GetValue<string>());
        var resolved = await Records.TransitionAsync(sam, Ticket, t.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Replaced cable" }, null, null, default);
        Assert.Equal("resolved", resolved.Data["status"]!.GetValue<string>());
        var dana = f.As(f.Acme, "dana");
        var mine = await Records.GetAsync(dana, Ticket, t.Id, default);
        Assert.Contains(mine.Transitions!, x => x.Id == "confirm");
        var closed = await Records.TransitionAsync(dana, Ticket, t.Id, mine.Revision, "confirm", [], null, null, default);
        Assert.Equal("closed", closed.Data["status"]!.GetValue<string>());
        Assert.True(closed.Data["confirmed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Query_filters_sort_and_search_respect_semantics()
    {
        var alex = f.As(f.Acme, "alex");
        var open = await Records.QueryAsync(alex, new RecordQuery { Entity = Ticket, Filters = ["!(record.status in ['resolved', 'closed', 'cancelled'])"], Sort = new SortSpec("priority", false), PageSize = 100 }, default);
        Assert.All(open.Items, r => Assert.DoesNotContain(r.Data["status"]!.GetValue<string>(), new[] { "resolved", "closed", "cancelled" }));
        var priorities = open.Items.Select(r => r.Data["priority"]!.GetValue<string>()).ToList();
        Assert.Equal(priorities.Order(StringComparer.Ordinal), priorities);

        var unassigned = await Records.QueryAsync(alex, new RecordQuery { Entity = Ticket, Filters = ["!has(record.assignee)"], PageSize = 100 }, default);
        Assert.All(unassigned.Items, r => Assert.False(r.Data.ContainsKey("assignee")));

        var search = await Records.QueryAsync(alex, new RecordQuery { Entity = Ticket, Search = "VPN", PageSize = 100 }, default);
        Assert.Contains(search.Items, r => r.Data["title"]!.GetValue<string>().Contains("VPN", StringComparison.Ordinal));

        var byNumber = await Records.QueryAsync(alex, new RecordQuery { Entity = Ticket, Search = "TCK-000001" }, default);
        Assert.Single(byNumber.Items);
    }

    [Fact]
    public async Task Deleted_records_can_be_restored_by_people_who_may_delete_them()
    {
        // REQ-REC-006: soft delete, then restore within the window; others can't restore, and other tenants can't see it.
        var t = await f.NewTenantAsync("rs");
        var admin = f.As(t, "admin");
        var created = await Records.CreateAsync(admin, Ticket, new JsonObject { ["title"] = "Printer on fire", ["requester"] = t.Users["dana"].ToString() }, null, null, default);
        await Records.DeleteAsync(admin, Ticket, created.Id, created.Revision, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(admin, Ticket, created.Id, default))).Status);

        var requester = await Assert.ThrowsAsync<KernelException>(() => Records.RestoreAsync(f.As(t, "dana"), Ticket, created.Id, default));
        Assert.Contains(requester.Status, new[] { 403, 404 });
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Records.RestoreAsync(f.As(f.Globex, "alex"), Ticket, created.Id, default))).Status);

        var restored = await Records.RestoreAsync(admin, Ticket, created.Id, default);
        Assert.Equal(created.Revision + 2, restored.Revision);
        Assert.Equal("Printer on fire", restored.Data["title"]!.GetValue<string>());
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Records.RestoreAsync(admin, Ticket, created.Id, default))).Status);
    }

    [Fact]
    public async Task Tenants_are_isolated_through_the_service_layer()
    {
        var acmeTicket = (await Records.QueryAsync(f.As(f.Acme, "alex"), new RecordQuery { Entity = Ticket }, default)).Items[0];
        // Alex is a member of both tenants; switching tenant never exposes the other tenant's record IDs.
        var ex = await Assert.ThrowsAsync<KernelException>(() => Records.GetAsync(f.As(f.Globex, "alex"), Ticket, acmeTicket.Id, default));
        Assert.Equal(404, ex.Status);
        var globexTitles = (await Records.QueryAsync(f.As(f.Globex, "alex"), new RecordQuery { Entity = Ticket, PageSize = 100 }, default)).Items.Select(i => i.Id);
        Assert.DoesNotContain(acmeTicket.Id, globexTitles);
    }

    [Fact]
    public async Task Agent_with_read_scope_only_cannot_write()
    {
        var identity = f.Get<Kernel.Infrastructure.Identity.IdentityService>();
        var agent = await identity.EnsureAgentAsync(f.Acme.Users["alex"], f.Acme.TenantId, default);
        var ctx = f.As(f.Acme, "alex", agent: agent, scopes: new HashSet<string> { AgentScopes.RecordsRead });
        var page = await Records.QueryAsync(ctx, new RecordQuery { Entity = Ticket }, default);
        Assert.NotEmpty(page.Items);
        var ex = await Assert.ThrowsAsync<KernelException>(() => Records.CreateAsync(ctx, Ticket, new JsonObject { ["title"] = "Agent wrote this" }, null, null, default));
        Assert.Equal(403, ex.Status);

        var foreignAgent = f.As(f.Acme, "dana", agent: agent, scopes: new HashSet<string>(AgentScopes.All));
        var denied = await Assert.ThrowsAsync<KernelException>(() => Records.QueryAsync(foreignAgent, new RecordQuery { Entity = Ticket }, default));
        Assert.Equal(403, denied.Status);
    }
}
