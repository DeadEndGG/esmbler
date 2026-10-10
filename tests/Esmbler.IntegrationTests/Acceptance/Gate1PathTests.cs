using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Api;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.IntegrationTests.Mcp;
using Esmbler.Kernel.Infrastructure.Events;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Esmbler.IntegrationTests.Acceptance;

/// <summary>
/// The Gate 1 proof end to end through the public API (execution brief section 4; REQ-ACC-01, REQ-TEN-002,
/// REQ-LIVE-002): a tenant customizes the default Tickets module by chatting with the AI builder over MCP, watches
/// the draft change live, publishes, operates records under the new rules, takes the 1.1.0 upgrade without losing
/// its changes, and a second tenant is unaffected throughout.
/// </summary>
[Collection(PlatformCollection.Name)]
[Trait("Requirement", "REQ-ACC-01")]
public class Gate1PathTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>, IClassFixture<McpServerTests.Holder>
{
    private const string Ticket = "core.tickets.ticket";

    [Fact]
    public async Task Customize_with_ai_publish_operate_upgrade_and_isolate()
    {
        var a = await platform.NewTenantAsync("ga");
        var b = await platform.NewTenantAsync("gb");
        var slugA = PlatformFixture.Slugs[a.TenantId];
        var slugB = PlatformFixture.Slugs[b.TenantId];
        await using var api = AssistantTestSupport.WithAssistant(ApiTestsFactoryHolder.Get(platform), McpServerTests.Holder.Get(platform));
        var admin = await AssistantTestSupport.SignInAsync(api, $"admin@{slugA}.example", slugA);
        var bAdmin = await AssistantTestSupport.SignInAsync(api, $"admin@{slugB}.example", slugB);
        var bSpecBefore = await bAdmin.GetStringAsync("/api/config/views/core.tickets.record");

        // 1. Live editing while chatting: the builder watches the draft; the assistant's steps arrive as they happen.
        await using var hub = AssistantTestSupport.Hub(api, admin);
        var draftEvents = new ConcurrentQueue<JsonElement>();
        var assistantDone = new ConcurrentQueue<TaskCompletionSource<JsonElement>>();
        var current = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("draftChanged", e => draftEvents.Enqueue(e));
        hub.On<JsonElement>("assistantEvent", e =>
        {
            if (e.GetProperty("type").GetString() is "done" or "error" or "question")
            {
                current.TrySetResult(e);
            }
        });
        await hub.StartAsync();
        await admin.PostAsync("/api/drafts", null);
        await hub.InvokeAsync("WatchDraft");

        string? conversation = null;
        foreach (var message in new[]
        {
            "Add a \"Vendor\" field and put it in the Details card",
            "When category is access, require a justification",
            "Show a warning callout when a ticket is unassigned",
        })
        {
            current = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = await admin.PostAsJsonAsync("/api/assistant/messages", new { conversationId = conversation, text = message });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            conversation = (await sent.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["conversationId"]!.GetValue<string>();
            var result = await current.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.GetProperty("type").GetString() == "done", $"'{message}': {result}");
        }

        await WaitUntilAsync(() => draftEvents.Count(e => e.GetProperty("k").GetString() == "apply" && e.GetProperty("agent").GetBoolean()) >= 4);

        // The draft view shows the changes to the builder; the published page does not change yet.
        var draftView = await admin.GetFromJsonAsync<JsonObject>("/api/drafts/current/views/core.tickets.record", ApiFactory.Json);
        Assert.NotEmpty(draftView!["changedTargets"]!.AsArray());
        var published = await admin.GetStringAsync("/api/config/views/core.tickets.record");
        Assert.DoesNotContain("tenant__vendor", published, StringComparison.Ordinal);

        // 2. A person publishes the exact artifact they reviewed.
        var draft = await admin.GetFromJsonAsync<JsonObject>("/api/drafts/current", ApiFactory.Json);
        var publish = await admin.PostAsJsonAsync("/api/config/publish", new { artifactHash = draft!["artifactHash"]!.GetValue<string>(), expectedRevision = draft["publishedRevision"]!.GetValue<int>() });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var view = await admin.GetStringAsync("/api/config/views/core.tickets.record");
        Assert.Contains("tenant__vendor", view, StringComparison.Ordinal);
        Assert.Contains("visibleWhen", view, StringComparison.Ordinal);

        // 3. Operate records under the new rules.
        var requester = a.Users["dana"].ToString();
        var missing = await admin.PostAsJsonAsync($"/api/records/{Ticket}", new { data = new { title = "Payroll access please", category = "access", requester } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("tenant__justification", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var created = await admin.PostAsJsonAsync($"/api/records/{Ticket}", new { data = new { title = "Payroll access please", category = "access", requester, tenant__justification = "Quarter-end close", tenant__vendor = "Workday" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticket = (await created.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!;
        var id = ticket["id"]!.GetValue<string>();

        // 4. Upgrade the module to 1.1.0; customizations and data survive.
        var plan = await admin.PostAsJsonAsync("/api/modules/core.tickets/upgrade-plan", new { toVersion = "1.1.0" });
        Assert.True((await plan.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["compatible"]!.GetValue<bool>());
        var started = await (await admin.PostAsJsonAsync("/api/modules/core.tickets/upgrades", new { toVersion = "1.1.0" })).Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        var upgradeId = started!["id"]!.GetValue<string>();
        var runner = api.Services.GetRequiredService<JobRunner>();
        JsonObject? upgrade = null;
        for (var i = 0; i < 40 && upgrade?["status"]?.GetValue<string>() is null or "planned" or "migrating"; i++)
        {
            await runner.RunOnceAsync(default);
            upgrade = await admin.GetFromJsonAsync<JsonObject>($"/api/upgrades/{upgradeId}", ApiFactory.Json);
        }

        Assert.Equal("activated", upgrade!["status"]!.GetValue<string>());
        var entity = await admin.GetFromJsonAsync<JsonObject>($"/api/config/entities/{Ticket}", ApiFactory.Json);
        var keys = entity!["entity"]!["fields"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()).ToList();
        Assert.Contains("tenant__vendor", keys);
        Assert.Contains("tenant__justification", keys);
        Assert.Contains("impact", keys);
        var after = await admin.GetFromJsonAsync<JsonObject>($"/api/records/{Ticket}/{id}", ApiFactory.Json);
        Assert.Equal("Workday", after!["data"]!["tenant__vendor"]!.GetValue<string>());
        Assert.Contains("tenant__vendor", await admin.GetStringAsync("/api/config/views/core.tickets.record"), StringComparison.Ordinal);

        // 5. Tenant B never saw any of it.
        Assert.Equal(bSpecBefore, await bAdmin.GetStringAsync("/api/config/views/core.tickets.record"));
        var bTicket = await bAdmin.PostAsJsonAsync($"/api/records/{Ticket}", new { data = new { title = "Vendor field test", tenant__vendor = "Dell", requester = b.Users["dana"].ToString() } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bTicket.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.GetAsync($"/api/records/{Ticket}/{id}")).StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "condition not met within 5 seconds");
    }
}
