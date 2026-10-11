using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Assistant;
using Esmbler.Assistant.Models;
using Esmbler.Kernel.Infrastructure.Assistant;

namespace Esmbler.UnitTests.Assistant;

/// <summary>
/// REQ-AI-005, REQ-AI-006, REQ-AI-008, REQ-AISAFE-001 (ADR 0024): the provider-neutral tool loop and its model clients.
/// The loop keeps budgets, refusals and metering whatever the provider; clients translate exactly and never leak
/// provider bodies or keys.
/// </summary>
public class ModelProviderTests
{
    private static readonly ToolDefinition AddField = new("add_field", "Adds a field.", JsonDocument.Parse("""{"type":"object","properties":{"label":{"type":"string"}},"required":["label"]}""").RootElement, false);

    [Fact]
    public async Task The_loop_runs_tool_calls_returns_results_to_the_model_and_finishes_with_its_answer()
    {
        var model = new FakeModelClient(FakeModelClient.Calls(("add_field", """{"label":"Vendor"}""")), FakeModelClient.Says("Added Vendor."));
        var tools = new Gateway("""{"steps":[{"step":3,"summary":"Added Vendor","targets":["core.tickets.ticket/tenant__vendor"]}]}""");
        var sink = new ListSink();
        var meter = new CountingMeter();
        var outcome = await new ToolLoopPlanner(model, meter).RunAsync(new AssistantRequest("c1", "Add a Vendor field", [], [new ConversationTurn("user", "hi"), new ConversationTurn("assistant", "Hello")]), tools, sink, new AssistantBudget(), default);

        Assert.Equal("done", outcome);
        Assert.Equal([("add_field", "Vendor")], tools.Calls);
        Assert.Contains(sink.Events, e => e.Type == AssistantEvent.Types.Step && e.Seq == 3 && e.Targets!.Contains("core.tickets.ticket/tenant__vendor"));
        Assert.Equal("Added Vendor.", sink.Events[^1].Text);
        Assert.Equal(2, meter.Recorded);

        // The second request carries the call and its result, history as plain text, and the shared instructions.
        var second = model.Requests[1];
        Assert.Equal(ToolLoopPlanner.SystemPrompt, second.Instructions);
        Assert.IsType<UserText>(second.Items[0]);
        Assert.IsType<AssistantText>(second.Items[1]);
        var call = Assert.Single(second.Items.OfType<ToolCall>());
        Assert.Equal(call.CallId, Assert.Single(second.Items.OfType<ToolResult>()).CallId);
    }

    [Fact]
    public async Task Refusals_budgets_and_bad_arguments_end_or_correct_the_turn_the_same_way_for_every_provider()
    {
        var refused = new ListSink();
        Assert.Equal("refused", await new ToolLoopPlanner(new FakeModelClient(new ModelResponse(StopKind.Refusal, [], ModelUsage.None("m")))).RunAsync(new AssistantRequest("c", "x", [], []), new Gateway("{}"), refused, new AssistantBudget(), default));
        Assert.Equal("I can't help with that request.", refused.Events[^1].Text);

        var looping = new FakeModelClient([.. Enumerable.Range(0, 5).Select(_ => FakeModelClient.Calls(("add_field", "{}")))]);
        await Assert.ThrowsAsync<BudgetExceededException>(() => new ToolLoopPlanner(looping).RunAsync(new AssistantRequest("c", "x", [], []), new Gateway("{}"), new ListSink(), new AssistantBudget(MaxModelTurns: 3), default));
        Assert.Equal(3, looping.Requests.Count);

        await Assert.ThrowsAsync<BudgetExceededException>(() => new ToolLoopPlanner(new FakeModelClient(), new CountingMeter { Exhausted = true }).RunAsync(new AssistantRequest("c", "x", [], []), new Gateway("{}"), new ListSink(), new AssistantBudget(), default));

        // Arguments that are not a JSON object go back to the model as a tool error; no tool runs.
        var malformed = new FakeModelClient(FakeModelClient.Calls(("add_field", "not json")), FakeModelClient.Says("Sorry."));
        var tools = new Gateway("{}");
        await new ToolLoopPlanner(malformed).RunAsync(new AssistantRequest("c", "x", [], []), tools, new ListSink(), new AssistantBudget(), default);
        Assert.Empty(tools.Calls);
        Assert.True(malformed.Requests[1].Items.OfType<ToolResult>().Single().IsError);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolLoopPlanner(new FakeModelClient()).RunAsync(new AssistantRequest("c", "x", [], []), new Gateway("{}"), new ListSink(), new AssistantBudget(), cts.Token));
    }

    [Fact]
    public void OpenAI_requests_keep_no_state_at_the_provider_and_replay_reasoning_within_the_turn()
    {
        var client = new OpenAIResponsesModelClient(new ModelSettings { Model = "gpt-6.1-sol", Effort = "high", MaxOutputTokens = 8000 }, OpenAIEndpoint.OpenAI("sk-test"));
        var reasoning = """{"type":"reasoning","id":"rs_1","encrypted_content":"opaque","summary":[]}""";
        var body = client.Body(new ModelRequest("Be helpful.", [new UserText("Add Vendor"), new ProviderState("openai", reasoning), new ToolCall("call_1", "add_field", """{"label":"Vendor"}"""), new ToolResult("call_1", "{\"ok\":true}", false)], [AddField]));

        Assert.Equal("gpt-6.1-sol", body["model"]!.GetValue<string>());
        Assert.Equal("Be helpful.", body["instructions"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("reasoning.encrypted_content", body["include"]![0]!.GetValue<string>());
        Assert.Equal("high", body["reasoning"]!["effort"]!.GetValue<string>());
        var tool = body["tools"]![0]!;
        Assert.Equal(("function", "add_field", false), (tool["type"]!.GetValue<string>(), tool["name"]!.GetValue<string>(), tool["strict"]!.GetValue<bool>()));
        var input = body["input"]!.AsArray();
        Assert.Equal(["message", "reasoning", "function_call", "function_call_output"], input.Select(i => i!["type"]!.GetValue<string>()));
        Assert.Equal("opaque", input[1]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("call_1", input[3]!["call_id"]!.GetValue<string>());

        // Another provider's state is never sent to OpenAI.
        Assert.Throws<InvalidOperationException>(() => OpenAIResponsesModelClient.Input([new ProviderState("anthropic", "{}")]));
    }

    [Fact]
    public void OpenAI_answers_become_tool_calls_text_refusals_and_usage()
    {
        var calls = OpenAIResponsesModelClient.Parse(JsonNode.Parse("""
            {"status":"completed","model":"gpt-6.1-sol-2026-09-01","output":[
              {"type":"reasoning","id":"rs_1","encrypted_content":"x","summary":[],"status":"completed"},
              {"type":"function_call","call_id":"call_9","name":"add_field","arguments":"{\"label\":\"Vendor\"}"}],
             "usage":{"input_tokens":1200,"output_tokens":80,"input_tokens_details":{"cached_tokens":1000}}}
            """)!.AsObject(), "gpt-6.1-sol");
        Assert.Equal(StopKind.ToolUse, calls.Stop);
        Assert.IsType<ProviderState>(calls.Output[0]);
        Assert.DoesNotContain("status", ((ProviderState)calls.Output[0]).Json, StringComparison.Ordinal);
        Assert.Equal(new ToolCall("call_9", "add_field", """{"label":"Vendor"}"""), calls.Output[1]);
        Assert.Equal(new ModelUsage(1200, 80, 1000, "gpt-6.1-sol-2026-09-01"), calls.Usage);

        var text = OpenAIResponsesModelClient.Parse(JsonNode.Parse("""{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done."}]}]}""")!.AsObject(), "m");
        Assert.Equal((StopKind.EndTurn, "Done."), (text.Stop, ((AssistantText)text.Output[0]).Text));
        var refusal = OpenAIResponsesModelClient.Parse(JsonNode.Parse("""{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"No."}]}]}""")!.AsObject(), "m");
        Assert.Equal(StopKind.Refusal, refusal.Stop);
        var cut = OpenAIResponsesModelClient.Parse(JsonNode.Parse("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""")!.AsObject(), "m");
        Assert.Equal(StopKind.MaxTokens, cut.Stop);
    }

    [Fact]
    public async Task Provider_errors_never_show_the_providers_answer_the_endpoint_or_the_key()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid request: secret-ticket-text and sk-live-123"}}""");
        var client = new OpenAIResponsesModelClient(new ModelSettings { Model = "gpt-6.1-sol" }, OpenAIEndpoint.AzureOpenAI(new Uri("https://contoso.openai.azure.com"), "azure-key-456"), handler);
        var error = await Assert.ThrowsAsync<AssistantUnavailableException>(() => client.CompleteAsync(new ModelRequest("i", [new UserText("x")], []), default));
        Assert.DoesNotContain("secret-ticket-text", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", error.Message, StringComparison.Ordinal);

        // Azure takes the key in api-key, at the v1 path; the key never appears in the endpoint's text.
        Assert.Equal("azure-key-456", handler.Last!.Headers.GetValues("api-key").Single());
        Assert.Null(handler.Last.Headers.Authorization);
        Assert.Equal("https://contoso.openai.azure.com/openai/v1/responses", handler.Last.RequestUri!.ToString());
        Assert.DoesNotContain("azure-key-456", OpenAIEndpoint.AzureOpenAI(new Uri("https://contoso.openai.azure.com"), "azure-key-456").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", new AiCredential("openai", "sk-live-123", null).ToString(), StringComparison.Ordinal);

        var busy = new OpenAIResponsesModelClient(new ModelSettings { Model = "m" }, OpenAIEndpoint.OpenAI("sk-x"), new StubHandler(HttpStatusCode.TooManyRequests, "{}"));
        Assert.Contains("busy", (await Assert.ThrowsAsync<AssistantUnavailableException>(() => busy.CompleteAsync(new ModelRequest("i", [], []), default))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_transcripts_group_into_alternating_messages_with_thinking_replayed()
    {
        var messages = AnthropicModelClient.Messages(
        [
            new UserText("hi"),
            new ProviderState("anthropic", """{"type":"thinking","thinking":"plan","signature":"sig"}"""),
            new AssistantText("Let me look."),
            new ToolCall("tu_1", "describe_entity", """{"entity":"core.tickets.ticket"}"""),
            new ToolResult("tu_1", "{}", false),
            new AssistantText("Done."),
        ]);
        Assert.Equal(4, messages.Count);
        Assert.Throws<InvalidOperationException>(() => AnthropicModelClient.Messages([new ProviderState("openai", "{}")]));
    }

    [Fact]
    public void Costs_are_estimated_from_catalog_prices_and_only_configured_evaluated_models_are_defaults()
    {
        var sol = new AiModelOption("gpt-6.1-sol", "Sol", "openai", "gpt-6.1-sol", "openai-default", "medium", 2m, 10m, false);
        var opus = new AiModelOption("claude-opus-5-5", "Opus", "anthropic", "claude-opus-5-5", "anthropic-default", "medium", 5m, 25m, true);
        Assert.Equal(0.0024m, AiCatalog.Cost(sol, 1000, 0, 0) + AiCatalog.Cost(sol, 0, 0, 0) + 0.0004m);
        Assert.Equal((200 * 2m + 1000 * 0.2m + 100 * 10m) / 1_000_000m, AiCatalog.Cost(sol, 1200, 100, 1000));

        var none = new AiCatalog([sol, opus], new Dictionary<string, AiCredential> { ["openai-default"] = new("openai", "k", null) }, null);
        Assert.Null(none.Default); // Sol has a key but no evaluation; Opus is evaluated but has no key.
        var both = new AiCatalog([sol, opus], new Dictionary<string, AiCredential> { ["anthropic-default"] = new("anthropic", "k", null) }, "gpt-6.1-sol");
        Assert.Equal("claude-opus-5-5", both.Default!.Id);
        Assert.False(both.Available(sol));
    }

    private sealed class Gateway(string result) : IToolGateway
    {
        public List<(string Tool, string? Label)> Calls { get; } = [];

        public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDefinition>>([AddField]);

        public Task<ToolOutcome> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
        {
            Calls.Add((tool, arguments["label"]?.GetValue<string>()));
            return Task.FromResult(new ToolOutcome(false, result));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ListSink : IAssistantSink
    {
        public List<AssistantEvent> Events { get; } = [];

        public Task EmitAsync(AssistantEvent e, CancellationToken ct)
        {
            Events.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingMeter : IModelUsageMeter
    {
        public bool Exhausted { get; init; }

        public int Recorded { get; private set; }

        public Task EnsureWithinBudgetAsync(CancellationToken ct) => Exhausted ? throw new BudgetExceededException("budget") : Task.CompletedTask;

        public Task RecordAsync(string provider, ModelUsage usage, CancellationToken ct)
        {
            Recorded++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
