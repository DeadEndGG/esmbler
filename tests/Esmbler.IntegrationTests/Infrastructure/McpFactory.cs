using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Mcp.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Esmbler.IntegrationTests.Infrastructure;

/// <summary>Hosts the real MCP server in memory against the platform fixture.</summary>
public sealed class McpFactory(PlatformFixture platform) : WebApplicationFactory<ToolRunner>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Esmbler:Database:App", platform.Postgres.AppConnectionString);
        builder.UseSetting("Esmbler:Auth:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.ConfigureTestServices(s => s.AddSingleton<IRelationshipGraph>(platform.Graph));
    }

    public async Task<McpClient> ConnectAsync(string bearerToken)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Server.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" },
            },
            CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, null, NullLoggerFactory.Instance);
    }

    /// <summary>Signs a user in through the API and mints an agent token for the MCP audience.</summary>
    public static async Task<(HttpClient Api, string AgentToken)> AgentAsync(ApiFactory api, string email, string tenant, params string[] scopes)
    {
        var client = await api.SignInAsync(email, tenant);
        var res = await client.PostAsJsonAsync("/api/auth/agent-token", new { scopes = scopes.Length == 0 ? null : scopes });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["accessToken"]!.GetValue<string>();
        return (client, token);
    }

    public static string ApiToken(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;
}
