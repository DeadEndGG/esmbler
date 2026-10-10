using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Esmbler.IntegrationTests.Api;

/// <summary>
/// Regression tests for the Gate 1 security review (threat model T-23, T-40, T-50, T-53): sign-in throttling,
/// refresh-token races and disabled accounts, builder-only reads, and no record data in the default AI delegation.
/// </summary>
[Collection(PlatformCollection.Name)]
public class SecurityHardeningTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    private static string Slug(ProvisionedTenant t) => PlatformFixture.Slugs[t.TenantId];

    [Fact]
    public async Task Sign_in_is_rate_limited_per_address()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Esmbler:Auth:LoginAttemptsPerMinute", "3"));
        var client = limited.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { email = $"nobody{i}@example.com", password = "wrong" })).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Figure_requests_are_rate_limited_per_person_and_lists_take_at_most_twenty_filters()
    {
        // ADR 0019 / T-71: figures aggregate many rows, so each person gets a bounded number of requests per minute,
        // and a list or group request carries a bounded number of filters.
        var t = await platform.NewTenantAsync("fq", "1.6.0");
        var signedIn = await Factory.SignInAsync($"sam@{Slug(t)}.example", Slug(t));
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Esmbler:MetricRequestsPerMinute", "2"));
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Authorization = signedIn.DefaultRequestHeaders.Authorization;
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.GetAsync("/api/metrics/core.tickets.metric.open")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);

        var filters = string.Join("&", Enumerable.Range(0, 21).Select(_ => "filter=" + Uri.EscapeDataString("record.priority == 'p1'")));
        var tooMany = await signedIn.GetAsync($"/api/records/core.tickets.ticket?{filters}");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("too_many_filters", (await tooMany.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await signedIn.GetAsync($"/api/records/core.tickets.ticket/groups?field=priority&{filters}")).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_temporarily()
    {
        var t = await platform.NewTenantAsync("lk");
        var email = $"riley@{Slug(t)}.example";
        var client = Factory.CreateClient();
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "guess" + i })).StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/auth/login", new { email, password = ProvisioningService.DemoPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("rate_limited", (await locked.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Concurrent_redemption_of_one_refresh_token_yields_at_most_one_successor()
    {
        var t = await platform.NewTenantAsync("rr");
        var client = Factory.CreateClient(new() { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = $"jordan@{Slug(t)}.example", password = ProvisioningService.DemoPassword });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("esmbler_refresh=", StringComparison.Ordinal)).Split(';')[0];

        var attempts = Enumerable.Range(0, 6).Select(_ =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
            req.Headers.Add("Cookie", cookie);
            req.Headers.Add("X-Esmbler-Csrf", "1");
            return client.SendAsync(req);
        });
        var results = await Task.WhenAll(attempts);
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK) <= 1, string.Join(", ", results.Select(r => r.StatusCode)));
    }

    [Fact]
    public async Task Member_directory_and_publish_requests_are_scoped()
    {
        var t = await platform.NewTenantAsync("sc");
        var requester = await Factory.SignInAsync($"dana@{Slug(t)}.example", Slug(t));
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/admin/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/admin/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/config/publish-requests/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/modules")).StatusCode);

        // Pickers show names only, to any member.
        var people = await requester.GetFromJsonAsync<JsonArray>("/api/directory/people", ApiFactory.Json);
        Assert.NotEmpty(people!);
        Assert.All(people!, p => Assert.Null(p!["email"]));

        var admin = await Factory.SignInAsync($"admin@{Slug(t)}.example", Slug(t));
        var members = await admin.GetFromJsonAsync<JsonArray>("/api/admin/members", ApiFactory.Json);
        Assert.Contains(members!, m => m!["email"] is not null);
    }

    [Fact]
    public async Task Role_targets_must_belong_to_the_tenant()
    {
        var a = await platform.NewTenantAsync("ta");
        var b = await platform.NewTenantAsync("tb");
        var admin = await Factory.SignInAsync($"admin@{Slug(a)}.example", Slug(a));
        var outsider = b.Users["dana"];
        var res = await admin.PostAsJsonAsync("/api/admin/roles/assign", new { role = "core.tickets.agent", principalId = outsider });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var foreignGroup = b.Groups["service_desk"];
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/admin/groups/{foreignGroup}/members", new { principalId = a.Users["dana"] })).StatusCode);
    }

    [Fact]
    public void Builder_delegation_has_no_record_access_by_default() =>
        Assert.DoesNotContain(AgentScopes.RecordsRead, AgentScopes.Builder);

    [Fact]
    public void Hosts_refuse_the_public_development_signing_key_unless_allowed()
    {
        const string devKey = "dev-only-signing-key-change-me-0123456789abcdef";
        Assert.Throws<InvalidOperationException>(() => Hosting.Auth.TokenService.CreateKey(devKey, allowDevelopmentKey: false));
        Assert.NotNull(Hosting.Auth.TokenService.CreateKey(devKey, allowDevelopmentKey: true));
        Assert.NotNull(Hosting.Auth.TokenService.CreateKey("a-real-secret-key-of-sufficient-length-0123456789", allowDevelopmentKey: false));
    }
}
