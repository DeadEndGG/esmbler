using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Esmbler.IntegrationTests.Api;

/// <summary>REQ-LIVE-002/004: draft changes reach watching builders quickly; only builders may watch.</summary>
[Collection(PlatformCollection.Name)]
public class LiveChannelTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    private HubConnection Hub(HttpClient client) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult(client.DefaultRequestHeaders.Authorization?.Parameter);
            })
            .Build();

    [Fact]
    public async Task Applied_command_reaches_the_watching_builder_within_a_second()
    {
        var tenant = await platform.NewTenantAsync("lv");
        var client = await Factory.SignInAsync($"admin@{SlugOf(tenant)}.example", SlugOf(tenant));
        var draft = await (await client.PostAsync("/api/drafts", null)).Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);

        await using var hub = Hub(client);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("draftChanged", p =>
        {
            if (p.GetProperty("k").GetString() == "apply")
            {
                received.TrySetResult(p);
            }
        });
        await hub.StartAsync();
        await hub.InvokeAsync("WatchDraft");

        var sw = Stopwatch.StartNew();
        var res = await client.PostAsJsonAsync("/api/drafts/current/commands", new
        {
            expectedVersion = draft!["version"]!.GetValue<int>(),
            op = new { op = "field.add", entity = "core.tickets.ticket", field = new { key = "tenant__vendor", type = "short_text", label = "Vendor" } },
        });
        res.EnsureSuccessStatusCode();
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, payload.GetProperty("v").GetInt32());
        Assert.Contains("core.tickets.ticket/tenant__vendor", payload.GetProperty("targets").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Requesters_cannot_watch_drafts()
    {
        var client = await Factory.SignInAsync("dana@acme.example", "acme");
        await using var hub = Hub(client);
        await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("WatchDraft"));
    }

    private static string SlugOf(Kernel.Infrastructure.Services.ProvisionedTenant t) => PlatformFixture.Slugs[t.TenantId];
}
