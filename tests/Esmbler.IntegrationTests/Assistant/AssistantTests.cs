using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Api.Assistant;
using Esmbler.Assistant;
using Esmbler.IntegrationTests.Api;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.IntegrationTests.Mcp;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Esmbler.IntegrationTests.Assistant;

/// <summary>
/// G1-M15/M16, REQ-AI-005, REQ-LIVE-003: the assistant customizes a workspace only through the MCP server, with
/// the person's agent token, and streams its progress. The evaluation cases in tests/ai-evals run here against the
/// scripted planner on every build; the Claude planner runs them too when ANTHROPIC_API_KEY is set.
/// </summary>
[Collection(PlatformCollection.Name)]
public class AssistantTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>, IClassFixture<McpServerTests.Holder>
{
    private ApiFactory Api => ApiTestsFactoryHolder.Get(platform);

    private McpFactory Mcp => McpServerTests.Holder.Get(platform);

    private static string Slug(Kernel.Infrastructure.Services.ProvisionedTenant t) => PlatformFixture.Slugs[t.TenantId];

    public static TheoryData<string> ScriptedCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadCases().Where(c => c["tags"]!.AsArray().Any(t => t!.GetValue<string>() == "scripted")))
        {
            data.Add(c["id"]!.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScriptedCases))]
    public async Task Scripted_planner_passes_the_evaluation_case(string caseId)
    {
        var failures = await RunCaseAsync(new ScriptedPlanner(), caseId);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Claude_planner_evaluation_runs_when_a_key_is_configured()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            return; // Recorded in docs/human-actions.md: provide a key to score the model-driven planner.
        }

        var planner = new ClaudePlanner(new ClaudePlannerOptions { Model = Environment.GetEnvironmentVariable("ESMBLER_EVAL_MODEL") ?? "claude-opus-5-5" });
        var scores = new List<string>();
        var passed = 0;
        var cases = LoadCases();
        foreach (var c in cases)
        {
            var failures = await RunCaseAsync(planner, c["id"]!.GetValue<string>());
            passed += failures.Count == 0 ? 1 : 0;
            scores.Add($"{c["id"]}: {(failures.Count == 0 ? "pass" : string.Join("; ", failures))}");
        }

        var report = Path.Combine(AppContext.BaseDirectory, "ai-eval-report.txt");
        await File.WriteAllLinesAsync(report, scores.Prepend($"Claude planner: {passed}/{cases.Count} passed"));
        Assert.True(passed >= cases.Count - 1, string.Join("\n", scores));
    }

    [Fact]
    public async Task Api_streams_progress_and_the_change_lands_in_the_draft()
    {
        var tenant = await platform.NewTenantAsync("as");
        await using var api = AssistantTestSupport.WithAssistant(Api, Mcp);
        var client = await AssistantTestSupport.SignInAsync(api, $"admin@{Slug(tenant)}.example", Slug(tenant));
        await client.PostAsync("/api/drafts", null);

        await using var hub = AssistantTestSupport.Hub(api, client);
        var events = new ConcurrentQueue<JsonElement>();
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("assistantEvent", e =>
        {
            events.Enqueue(e);
            if (e.GetProperty("type").GetString() is "done" or "error" or "question" or "stopped")
            {
                done.TrySetResult(e);
            }
        });
        await hub.StartAsync();

        var res = await client.PostAsJsonAsync("/api/assistant/messages", new { text = "Add a \"Vendor\" field and put it in the Details card" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var accepted = await res.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json);
        Assert.Equal("scripted", accepted!["planner"]!.GetValue<string>());

        var final = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("done", final.GetProperty("type").GetString());
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "step" && e.GetProperty("targets").EnumerateArray().Any(t => t.GetString()!.Contains("tenant__vendor", StringComparison.Ordinal)));
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "tool");

        var draft = await client.GetFromJsonAsync<JsonObject>("/api/drafts/current", ApiFactory.Json);
        var steps = draft!["steps"]!.AsArray();
        Assert.Contains(steps, s => s!["byAgent"]!.GetValue<bool>() && s["summary"]!.GetValue<string>().Contains("Vendor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assistant_cannot_act_for_someone_without_builder_rights()
    {
        var tenant = await platform.NewTenantAsync("an");
        await using var api = AssistantTestSupport.WithAssistant(Api, Mcp);
        var requester = await AssistantTestSupport.SignInAsync(api, $"dana@{Slug(tenant)}.example", Slug(tenant));
        var res = await requester.PostAsJsonAsync("/api/assistant/messages", new { text = "Add a \"Vendor\" field" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private async Task<List<string>> RunCaseAsync(IPlanner planner, string caseId)
    {
        var c = LoadCases().First(x => x["id"]!.GetValue<string>() == caseId);
        var tenant = await platform.NewTenantAsync("ev");
        var (apiClient, token) = await McpFactory.AgentAsync(Api, $"admin@{Slug(tenant)}.example", Slug(tenant));
        var sink = new CollectingSink();
        var budget = new AssistantBudget();
        await using var gateway = new McpToolGateway(await Mcp.ConnectAsync(token), budget);
        var outcome = await planner.RunAsync(new AssistantRequest("eval", c["prompt"]!.GetValue<string>(), [], []), gateway, sink, budget, CancellationToken.None);

        var failures = new List<string>();
        await using var reader = new McpToolGateway(await Mcp.ConnectAsync(token), new AssistantBudget(MaxToolCalls: 100));
        var entity = (await reader.CallAsync("describe_entity", new JsonObject { ["entity"] = "core.tickets.ticket" }, default)).Json!;
        var view = (await reader.CallAsync("get_view", new JsonObject { ["view"] = "core.tickets.record" }, default)).Json!;
        var draftJson = (await reader.CallAsync("get_draft", [], default)).Json;
        var fields = entity["fields"]!.AsArray();
        JsonNode? Field(string label) => fields.FirstOrDefault(f => string.Equals(f!["label"]!.GetValue<string>(), label, StringComparison.OrdinalIgnoreCase));
        var nodes = Flatten(view["layout"]!).ToList();

        foreach (var check in c["checks"]!.AsArray())
        {
            var kind = check!["kind"]!.GetValue<string>();
            var label = check["label"]?.GetValue<string>();
            switch (kind)
            {
                case "field":
                    var f = Field(label!);
                    if (f is null)
                    {
                        failures.Add($"no field '{label}' (outcome {outcome}; events: {sink.Summary})");
                        break;
                    }

                    if (check["required"]?.GetValue<bool>() == true && f["required"]?.GetValue<bool>() != true)
                    {
                        failures.Add($"'{label}' is not required");
                    }

                    if (check["type"]?.GetValue<string>() is { } type && f["type"]!.GetValue<string>() != type)
                    {
                        failures.Add($"'{label}' has type {f["type"]} not {type}");
                    }

                    foreach (var opt in check["options"]?.AsArray() ?? [])
                    {
                        if (f["options"]?.AsArray().Any(o => o!["label"]!.GetValue<string>() == opt!.GetValue<string>()) != true)
                        {
                            failures.Add($"'{label}' lacks option {opt}");
                        }
                    }

                    break;
                case "placed":
                    var key = Field(label!)?["key"]?.GetValue<string>();
                    var card = nodes.FirstOrDefault(n => n["type"]!.GetValue<string>() == "layout.card" && n["props"]?["title"]?.GetValue<string>() == check["card"]!.GetValue<string>());
                    if (card is null || key is null || !Flatten(card).Any(n => n["props"]?["field"]?.GetValue<string>() == key))
                    {
                        failures.Add($"'{label}' is not in the {check["card"]} card");
                    }

                    break;
                case "rule":
                    var target = Field(label!)?["key"]?.GetValue<string>();
                    var rules = entity["rules"]!.AsArray();
                    if (!rules.Any(r => (r!["when"]?.GetValue<string>() ?? string.Empty).Contains(check["whenContains"]!.GetValue<string>(), StringComparison.Ordinal)
                        && r["actions"]!.AsArray().Any(a => a!["type"]!.GetValue<string>() == check["action"]!.GetValue<string>() && a["field"]?.GetValue<string>() == target)))
                    {
                        failures.Add($"no {check["action"]} rule for '{label}' when {check["whenContains"]} (events: {sink.Summary})");
                    }

                    break;
                case "callout":
                    if (!nodes.Any(n => n["type"]!.GetValue<string>() == "display.callout" && n["id"]!.GetValue<string>().StartsWith("tenant__", StringComparison.Ordinal)
                        && n["props"]?["tone"]?.GetValue<string>() == check["tone"]!.GetValue<string>()
                        && (n["props"]?["visibleWhen"]?.GetValue<string>() ?? string.Empty).Contains(check["visibleWhenContains"]!.GetValue<string>(), StringComparison.Ordinal)))
                    {
                        failures.Add($"no {check["tone"]} callout (events: {sink.Summary})");
                    }

                    break;
                case "asked":
                    if (!sink.Events.Any(e => e.Type == AssistantEvent.Types.Question))
                    {
                        failures.Add("the assistant did not ask a question");
                    }

                    break;
                case "noChanges":
                    if ((draftJson?["steps"]?.AsArray().Count ?? 0) > 0)
                    {
                        failures.Add("the draft changed");
                    }

                    break;
                case "noPublish":
                    var pending = await apiClient.GetAsync("/api/config/publish-requests/pending");
                    var body = await pending.Content.ReadAsStringAsync();
                    if (pending.StatusCode == HttpStatusCode.OK && body.Contains("artifactHash", StringComparison.Ordinal))
                    {
                        failures.Add("a publish was requested without being asked");
                    }

                    break;
            }
        }

        return failures;
    }

    private static IEnumerable<JsonNode> Flatten(JsonNode node) =>
        (node["children"]?.AsArray() ?? []).Where(c => c is not null).SelectMany(c => Flatten(c!)).Prepend(node);

    private static List<JsonNode> LoadCases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "ai-evals", "builder-cases.json")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir?.FullName ?? throw new FileNotFoundException("tests/ai-evals/builder-cases.json"), "tests", "ai-evals", "builder-cases.json");
        return JsonNode.Parse(File.ReadAllText(path))!["cases"]!.AsArray().Select(c => c!).ToList();
    }

    private sealed class CollectingSink : IAssistantSink
    {
        public ConcurrentQueue<AssistantEvent> Events { get; } = new();

        public string Summary => string.Join(" | ", Events.Where(e => e.Type is not "tool").Select(e => $"{e.Type}: {e.Text}"));

        public Task EmitAsync(AssistantEvent e, CancellationToken ct)
        {
            Events.Enqueue(e);
            return Task.CompletedTask;
        }
    }
}
