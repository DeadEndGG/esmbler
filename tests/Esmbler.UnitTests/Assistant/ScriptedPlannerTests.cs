using System.Text.Json.Nodes;
using Esmbler.Assistant;

namespace Esmbler.UnitTests.Assistant;

/// <summary>G1-M15: the scripted planner's language handling and its behaviour when it can't help.</summary>
public class ScriptedPlannerTests
{
    [Theory]
    [InlineData("vendor", "Vendor", "tenant__vendor", true)]
    [InlineData("the impact", "Customer impact", "tenant__customer_impact", true)]
    [InlineData("impact description", "Description", "description", false)]
    [InlineData("assignee", "Assignee", "assignee", true)]
    [InlineData("assignment group", "Assignment group", "assignment_group", true)]
    [InlineData("priority", "Category", "category", false)]
    public void Field_phrases_match_labels_and_keys(string phrase, string label, string key, bool matches) =>
        Assert.Equal(matches, ScriptedPlanner.Score(phrase, label, key) >= 0.5);

    [Theory]
    [InlineData("Email, Phone and Portal", new[] { "Email", "Phone", "Portal" })]
    [InlineData("\"Low\", \"High\"", new[] { "Low", "High" })]
    [InlineData("hardware or software", new[] { "hardware", "software" })]
    public void Lists_split_on_commas_and_conjunctions(string text, string[] expected) =>
        Assert.Equal(expected, ScriptedPlanner.SplitList(text));

    [Fact]
    public async Task Unknown_requests_get_a_question_and_no_tool_calls()
    {
        var tools = new RecordingGateway();
        var sink = new ListSink();
        var outcome = await new ScriptedPlanner().RunAsync(new AssistantRequest("c1", "Make it nicer", [], []), tools, sink, new AssistantBudget(), default);
        Assert.Equal("question", outcome);
        Assert.Empty(tools.Calls);
        var question = Assert.Single(sink.Events, e => e.Type == AssistantEvent.Types.Question);
        Assert.NotEmpty(question.Options!);
    }

    [Fact]
    public async Task Cancellation_stops_before_any_change()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ScriptedPlanner().RunAsync(new AssistantRequest("c1", "Add a \"Vendor\" field", [], []), new RecordingGateway(), new ListSink(), new AssistantBudget(), cts.Token));
    }

    [Fact]
    public void Tool_progress_is_described_in_plain_language()
    {
        Assert.Equal("Adding the field \"Vendor\"…", ToolNarration.Describe("add_field", new JsonObject { ["label"] = "Vendor" }));
        Assert.Equal("Reading the fields of Tickets…", ToolNarration.Describe("describe_entity", new JsonObject { ["entity"] = "core.tickets.ticket" }));
    }

    private sealed class RecordingGateway : IToolGateway
    {
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDefinition>>([]);

        public Task<ToolOutcome> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
        {
            Calls.Add(tool);
            return Task.FromResult(new ToolOutcome(true, "{\"error\":{\"code\":\"unavailable\",\"message\":\"not in this test\"}}"));
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
}
