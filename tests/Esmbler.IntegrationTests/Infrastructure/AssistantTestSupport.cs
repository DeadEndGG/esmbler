using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Esmbler.Api.Assistant;
using Esmbler.Api.Endpoints;
using Esmbler.Assistant;
using Esmbler.Kernel.Infrastructure.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Esmbler.IntegrationTests.Infrastructure;

/// <summary>An API host whose assistant reaches the in-memory MCP server, plus sign-in and hub helpers.</summary>
public static class AssistantTestSupport
{
    public static WebApplicationFactory<KernelExceptionHandler> WithAssistant(ApiFactory api, McpFactory mcp) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAssistantGatewayFactory>(new InMemoryGatewayFactory(mcp))));

    public static async Task<HttpClient> SignInAsync(WebApplicationFactory<KernelExceptionHandler> api, string email, string tenant)
    {
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = ProvisioningService.DemoPassword });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sw = await client.PostAsJsonAsync("/api/auth/switch", new { tenant, environment = "prod" });
        sw.EnsureSuccessStatusCode();
        var scoped = (await sw.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scoped);
        return client;
    }

    public static HubConnection Hub(WebApplicationFactory<KernelExceptionHandler> api, HttpClient client) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult(client.DefaultRequestHeaders.Authorization?.Parameter);
            })
            .Build();

    private sealed class InMemoryGatewayFactory(McpFactory mcp) : IAssistantGatewayFactory
    {
        public async Task<IToolGateway> ConnectAsync(string agentToken, AssistantBudget budget, CancellationToken ct) =>
            new McpToolGateway(await mcp.ConnectAsync(agentToken), budget);
    }
}
