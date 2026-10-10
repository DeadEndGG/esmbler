using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;

namespace Esmbler.IntegrationTests.Api;

/// <summary>REQ-BIZ-004: the service catalog API and request submission over HTTP (ADR 0015).</summary>
[Collection(PlatformCollection.Name)]
public class CatalogApiTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    [Fact]
    public async Task Requesters_browse_the_catalog_and_submit_typed_requests()
    {
        var requester = await Factory.SignInAsync("taylor@globex.example", "globex");
        var app = await requester.GetFromJsonAsync<JsonObject>("/api/app", ApiFactory.Json);
        Assert.True(app!["catalog"]!.GetValue<bool>());
        Assert.Contains(app["navigation"]!.AsArray(), n => n!["entity"]!.GetValue<string>() == "core.tickets.request" && n["requests"]!.GetValue<bool>());

        var catalog = await requester.GetFromJsonAsync<JsonObject>("/api/catalog", ApiFactory.Json);
        var items = catalog!["items"]!.AsArray();
        Assert.Equal(["New laptop", "Software access", "New hire onboarding"], items.Select(i => i!["label"]!.GetValue<string>()));
        var laptop = items[0]!;
        Assert.Contains(laptop["variables"]!.AsArray(), v => v!["key"]!.GetValue<string>() == "model" && v["required"]!.GetValue<bool>());
        Assert.Equal(3, laptop["steps"]!.AsArray().Count);
        Assert.Contains(catalog["choiceLists"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "core.tickets.laptop_model");

        var invalid = await requester.PostAsJsonAsync("/api/records/core.tickets.request", new { data = new { catalog_item = laptop["id"]!.GetValue<string>(), variables = new { model = "abacus" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        var targets = problem!["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()).ToList();
        Assert.Contains("variables.model", targets);
        Assert.Contains("variables.justification", targets);

        var created = await requester.PostAsJsonAsync("/api/records/core.tickets.request", new { data = new { catalog_item = laptop["id"]!.GetValue<string>(), variables = new { model = "mac", justification = "Video editing" } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        Assert.Equal("New laptop", request!["display"]!["catalog_item"]!.GetValue<string>());
        Assert.Equal("submitted", request["data"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Workspaces_without_requests_have_an_empty_catalog()
    {
        var acme = await Factory.SignInAsync("dana@acme.example", "acme");
        var catalog = await acme.GetFromJsonAsync<JsonObject>("/api/catalog", ApiFactory.Json);
        Assert.Empty(catalog!["items"]!.AsArray());
        var app = await acme.GetFromJsonAsync<JsonObject>("/api/app", ApiFactory.Json);
        Assert.False(app!["catalog"]!.GetValue<bool>());
    }
}
