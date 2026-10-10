using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;

namespace Esmbler.IntegrationTests.Api;

/// <summary>REQ-TEN-003, REQ-AISAFE-004, REQ-AUTHZ-009, REQ-AI-004: HTTP surface of the API host.</summary>
[Collection(PlatformCollection.Name)]
public class ApiTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    [Fact]
    public async Task Login_switch_and_list_tickets()
    {
        var client = await Factory.SignInAsync("sam@acme.example", "acme");
        var app = await client.GetFromJsonAsync<JsonObject>("/api/app", ApiFactory.Json);
        Assert.Equal("acme", app!["tenant"]!["slug"]!.GetValue<string>());
        Assert.Contains(app["navigation"]!.AsArray(), n => n!["entity"]!.GetValue<string>() == "core.tickets.ticket");

        var page = await client.GetFromJsonAsync<JsonObject>("/api/records/core.tickets.ticket?size=5&sort=priority", ApiFactory.Json);
        Assert.Equal(5, page!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Requests_without_a_selected_workspace_are_refused()
    {
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "sam@acme.example", password = Kernel.Infrastructure.Services.ProvisioningService.DemoPassword });
        var token = (await login.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/records/core.tickets.ticket");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        Assert.Equal("no_tenant_selected", problem!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Switching_to_a_tenant_you_do_not_belong_to_is_forbidden()
    {
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "sam@acme.example", password = Kernel.Infrastructure.Services.ProvisioningService.DemoPassword });
        var token = (await login.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.PostAsJsonAsync("/api/auth/switch", new { tenant = "globex" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Sign_in_page_shows_no_demo_account_unless_demo_data_is_seeded()
    {
        var options = await Factory.CreateClient().GetFromJsonAsync<JsonObject>("/api/auth/sign-in-options", ApiFactory.Json);
        Assert.NotNull(options);
        Assert.Null(options["demoAccount"]);
    }

    [Fact]
    public async Task Wrong_password_gets_a_plain_error()
    {
        var res = await Factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "sam@acme.example", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("invalid_credentials", (await res.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refresh_rotates_and_detects_reuse()
    {
        var client = Factory.CreateClient(new() { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "dana@acme.example", password = Kernel.Infrastructure.Services.ProvisioningService.DemoPassword });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("esmbler_refresh=", StringComparison.Ordinal)).Split(';')[0];

        async Task<HttpResponseMessage> Refresh(string c, bool csrf = true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
            req.Headers.Add("Cookie", c);
            if (csrf)
            {
                req.Headers.Add("X-Esmbler-Csrf", "1");
            }

            return await client.SendAsync(req);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(cookie, csrf: false)).StatusCode);
        var first = await Refresh(cookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("esmbler_refresh=", StringComparison.Ordinal)).Split(';')[0];
        Assert.NotEqual(cookie, rotated);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(cookie)).StatusCode); // reuse of the old token
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(rotated)).StatusCode); // family revoked after reuse
    }

    [Fact]
    public async Task Agent_tokens_are_rejected_by_the_api_audience()
    {
        var client = await Factory.SignInAsync("alex@acme.example", "acme");
        var res = await client.PostAsJsonAsync("/api/auth/agent-token", new { scopes = new[] { "config:read", "records:read" } });
        res.EnsureSuccessStatusCode();
        var agentToken = (await res.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["accessToken"]!.GetValue<string>();

        var agentClient = Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentClient.GetAsync("/api/app")).StatusCode);
    }

    [Fact]
    public async Task Validation_errors_are_actionable_problem_details()
    {
        var client = await Factory.SignInAsync("sam@acme.example", "acme");
        var res = await client.PostAsJsonAsync("/api/records/core.tickets.ticket", new { data = new { title = "Hi", priorty = "p1" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        Assert.Equal("validation_failed", problem!["code"]!.GetValue<string>());
        Assert.Contains(problem["errors"]!.AsArray(), e => e!["suggestion"]?.GetValue<string>() == "priority");
    }

    [Fact]
    public async Task Requester_view_omits_hidden_fields_and_explain_says_why()
    {
        var client = await Factory.SignInAsync("dana@acme.example", "acme");
        var view = await client.GetFromJsonAsync<JsonObject>("/api/config/views/core.tickets.record", ApiFactory.Json);
        var json = view!.ToJsonString();
        Assert.DoesNotContain("\"field\":\"estimated_effort\"", json);
        Assert.DoesNotContain("\"key\":\"labels\"", json);

        var explain = await client.GetFromJsonAsync<JsonObject>("/api/access/explain?entity=core.tickets.ticket&field=estimated_effort", ApiFactory.Json);
        Assert.Equal("hidden", explain!["fieldAccess"]!.GetValue<string>());
        Assert.Contains(explain["reasons"]!.AsArray(), r => r!["text"]!.GetValue<string>().Contains("Requester", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Openapi_document_is_generated()
    {
        var doc = await Factory.CreateClient().GetFromJsonAsync<JsonObject>("/openapi/v1.json", ApiFactory.Json);
        Assert.StartsWith("3.1", doc!["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(doc["paths"]!.AsObject().ContainsKey("/api/records/{entity}"));
    }
}

/// <summary>Shares one in-memory API host across the API test class.</summary>
public sealed class ApiTestsFactoryHolder : IDisposable
{
    private static ApiFactory? _factory;

    public static ApiFactory Get(PlatformFixture platform) => _factory ??= new ApiFactory(platform);

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
