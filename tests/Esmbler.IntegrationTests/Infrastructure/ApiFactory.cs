using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Api.Endpoints;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Esmbler.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API in memory against the platform fixture's database and relationship graph.</summary>
public sealed class ApiFactory(PlatformFixture platform) : WebApplicationFactory<KernelExceptionHandler>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Esmbler:Database:App", platform.Postgres.AppConnectionString);
        builder.UseSetting("Esmbler:Database:Migrator", platform.Postgres.MigratorConnectionString);
        builder.UseSetting("Esmbler:MigrateOnStartup", "false");
        builder.UseSetting("Esmbler:SeedDemoData", "false");
        builder.UseSetting("Esmbler:Auth:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("Esmbler:Auth:LoginAttemptsPerMinute", "100000");
        builder.UseSetting("Esmbler:Auth:SecureCookies", "false");
        builder.ConfigureTestServices(s => s.AddSingleton<IRelationshipGraph>(platform.Graph));
    }

    /// <summary>Logs in and switches to a tenant; returns a client carrying the tenant-scoped token.</summary>
    public async Task<HttpClient> SignInAsync(string email, string tenant, string environment = "prod")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = ProvisioningService.DemoPassword });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonObject>(Json))!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sw = await client.PostAsJsonAsync("/api/auth/switch", new { tenant, environment });
        sw.EnsureSuccessStatusCode();
        var scoped = (await sw.Content.ReadFromJsonAsync<JsonObject>(Json))!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scoped);
        return client;
    }
}
