using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esmbler.Assistant;

/// <summary>Progress pushed to the person while the assistant works (REQ-LIVE-003). Types match the web client.</summary>
public sealed record AssistantEvent(
    string ConversationId,
    string Type,
    string? Text = null,
    string? Tool = null,
    int? Seq = null,
    IReadOnlyList<string>? Targets = null,
    IReadOnlyList<string>? Options = null,
    bool? Ok = null)
{
    public static class Types
    {
        public const string Thinking = "thinking";
        public const string Tool = "tool";
        public const string Step = "step";
        public const string Message = "message";
        public const string Question = "question";
        public const string Done = "done";
        public const string Error = "error";
        public const string Stopped = "stopped";
    }
}

public interface IAssistantSink
{
    Task EmitAsync(AssistantEvent e, CancellationToken ct);
}

/// <summary>A tool the assistant can call, as advertised by the MCP server.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema, bool ReadOnly);

/// <summary>Result of one tool call: the JSON text the server returned and whether it is an error.</summary>
public sealed record ToolOutcome(bool IsError, string Text)
{
    public JsonNode? Json
    {
        get
        {
            try
            {
                return JsonNode.Parse(Text);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public string? ErrorMessage => IsError ? Json?["error"]?["message"]?.GetValue<string>() ?? Text : null;

    public string? Suggestion => Json?["error"]?["suggestion"]?.GetValue<string>();
}

/// <summary>The MCP client as the planners see it.</summary>
public interface IToolGateway : IAsyncDisposable
{
    Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken ct);

    Task<ToolOutcome> CallAsync(string tool, JsonObject arguments, CancellationToken ct);
}

/// <summary>An element the person pointed at on the page (point-and-prompt, REQ-LIVE-004).</summary>
public sealed record SelectedElement(string View, string Node, string? Type, string? Label);

/// <summary>One prior exchange, kept so follow-ups ("make it required too") have context.</summary>
public sealed record ConversationTurn(string Role, string Text);

public sealed record AssistantRequest(
    string ConversationId,
    string Text,
    IReadOnlyList<SelectedElement> Selection,
    IReadOnlyList<ConversationTurn> History,
    string DefaultEntity = "core.tickets.ticket",
    string DefaultRecordView = "core.tickets.record");

/// <summary>Per-turn limits (REQ-AI-008): the planner stops cleanly when one is reached.</summary>
public sealed record AssistantBudget(int MaxToolCalls = 30, int MaxModelTurns = 15, int MaxSeconds = 120);

/// <summary>Turns a request into tool calls. Implementations report progress and finish with done/question/error.</summary>
public interface IPlanner
{
    string Name { get; }

    Task<string> RunAsync(AssistantRequest request, IToolGateway tools, IAssistantSink sink, AssistantBudget budget, CancellationToken ct);
}

/// <summary>Thrown when a budget runs out; the loop reports what was done so far.</summary>
public sealed class BudgetExceededException(string message) : Exception(message);
