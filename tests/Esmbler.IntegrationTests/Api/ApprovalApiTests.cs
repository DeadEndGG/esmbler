using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Events;

namespace Esmbler.IntegrationTests.Api;

/// <summary>REQ-BIZ-004, REQ-AUTHZ-011: the approvals inbox, decisions, the approvals panel and delegation over HTTP (ADR 0016).</summary>
[Collection(PlatformCollection.Name)]
public class ApprovalApiTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    private async Task DrainAsync()
    {
        var dispatcher = platform.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }
    }

    private static async Task<JsonObject> SubmitLaptopAsync(HttpClient requester, string why)
    {
        var created = await requester.PostAsJsonAsync("/api/records/core.tickets.request", new { data = new { catalog_item = "core.tickets.catalog.new_laptop", variables = new { model = "mac", justification = why } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!;
    }

    [Fact]
    public async Task An_approver_decides_from_the_inbox_and_the_requester_sees_the_outcome()
    {
        var requester = await Factory.SignInAsync("taylor@globex.example", "globex");
        var request = await SubmitLaptopAsync(requester, "Design work " + Guid.NewGuid().ToString("N")[..6]);
        var id = request["id"]!.GetValue<string>();
        await DrainAsync();

        var approver = await Factory.SignInAsync("jamie@globex.example", "globex");
        var app = await approver.GetFromJsonAsync<JsonObject>("/api/app", ApiFactory.Json);
        Assert.True(app!["approvals"]!.GetValue<bool>());
        var count = await approver.GetFromJsonAsync<JsonObject>("/api/approvals/count", ApiFactory.Json);
        Assert.True(count!["pending"]!.GetValue<int>() >= 1);

        var inbox = await approver.GetFromJsonAsync<JsonObject>("/api/approvals", ApiFactory.Json);
        var item = inbox!["pending"]!.AsArray().Single(i => i!["recordId"]!.GetValue<string>() == id)!;
        Assert.Equal("Taylor Brooks", item["requester"]!.GetValue<string>());
        Assert.Equal("mac", item["record"]!["data"]!["variables"]!["model"]!.GetValue<string>());

        var silent = await approver.PostAsJsonAsync($"/api/approvals/steps/{item["stepId"]}/decision", new { decision = "rejected" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, silent.StatusCode);
        var selfDecision = await requester.PostAsJsonAsync($"/api/approvals/steps/{item["stepId"]}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.Forbidden, selfDecision.StatusCode);

        var decided = await approver.PostAsJsonAsync($"/api/approvals/steps/{item["stepId"]}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal("approved", (await decided.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["status"]!.GetValue<string>());
        await DrainAsync();

        var panel = await requester.GetFromJsonAsync<JsonArray>($"/api/records/core.tickets.request/{id}/approvals", ApiFactory.Json);
        Assert.Equal("approved", panel![0]!["status"]!.GetValue<string>());
        var mine = await requester.GetFromJsonAsync<JsonObject>($"/api/records/core.tickets.request/{id}", ApiFactory.Json);
        Assert.Equal("fulfilling", mine!["data"]!["status"]!.GetValue<string>());
        Assert.Equal("Jamie Ortiz", mine["display"]!["approvers"]!.GetValue<string>());
    }

    [Fact]
    public async Task People_delegate_their_approvals_and_end_the_delegation()
    {
        var approver = await Factory.SignInAsync("jamie@globex.example", "globex");
        var people = await approver.GetFromJsonAsync<JsonArray>("/api/directory/people", ApiFactory.Json);
        var quinn = people!.Single(p => p!["name"]!.GetValue<string>() == "Quinn Patel")!["id"]!.GetValue<string>();

        var bad = await approver.PostAsJsonAsync("/api/approvals/delegations", new { @delegate = quinn, startsAt = DateTimeOffset.UtcNow, endsAt = DateTimeOffset.UtcNow.AddDays(200) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var created = await approver.PostAsJsonAsync("/api/approvals/delegations", new { @delegate = quinn, startsAt = DateTimeOffset.UtcNow, endsAt = DateTimeOffset.UtcNow.AddDays(5) });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var delegation = await created.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        Assert.Equal("Quinn Patel", delegation!["delegate"]!.GetValue<string>());

        var delegate_ = await Factory.SignInAsync("quinn@globex.example", "globex");
        var theirs = await delegate_.GetFromJsonAsync<JsonArray>("/api/approvals/delegations", ApiFactory.Json);
        Assert.Contains(theirs!, d => d!["delegator"]!.GetValue<string>() == "Jamie Ortiz");
        var notTheirs = await delegate_.DeleteAsync($"/api/approvals/delegations/{delegation["id"]}");
        Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await approver.DeleteAsync($"/api/approvals/delegations/{delegation["id"]}")).StatusCode);
        Assert.Empty((await approver.GetFromJsonAsync<JsonArray>("/api/approvals/delegations", ApiFactory.Json))!);
    }
}
