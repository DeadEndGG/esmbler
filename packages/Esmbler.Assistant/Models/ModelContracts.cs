namespace Esmbler.Assistant.Models;

/// <summary>
/// One model request to one provider (ADR 0024). Implementations translate a provider-neutral transcript to the
/// provider's API and back; they never call tools, never log request or response bodies, and turn provider failures into
/// <see cref="AssistantUnavailableException"/> without provider details, endpoints or keys.
/// </summary>
public interface IModelClient
{
    /// <summary>anthropic, openai, azure-openai or fake.</summary>
    string Provider { get; }

    string Model { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
}

/// <summary>Provider and model settings for one client, resolved by the host (never from tenant data directly).</summary>
public sealed record ModelSettings
{
    public required string Model { get; init; }

    /// <summary>Reasoning effort (low, medium, high, max).</summary>
    public string Effort { get; init; } = "medium";

    public int MaxOutputTokens { get; init; } = 16_000;

    /// <summary>One request's time limit (ADR 0013: 60 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Anthropic only: the model that re-serves a request the primary model declines.</summary>
    public string? FallbackModel { get; init; }
}

/// <summary>The transcript of one turn plus the tools; instructions go to the provider's system channel.</summary>
public sealed record ModelRequest(string Instructions, IReadOnlyList<ModelItem> Items, IReadOnlyList<ToolDefinition> Tools);

/// <summary>A provider-neutral transcript item.</summary>
public abstract record ModelItem;

public sealed record UserText(string Text) : ModelItem;

public sealed record AssistantText(string Text) : ModelItem;

/// <summary>The model asks for a tool; <see cref="CallId"/> pairs it with its <see cref="ToolResult"/>.</summary>
public sealed record ToolCall(string CallId, string Name, string ArgumentsJson) : ModelItem;

/// <summary>A tool's output, sent back only through the provider's tool-result channel (never as instructions).</summary>
public sealed record ToolResult(string CallId, string Content, bool IsError) : ModelItem;

/// <summary>
/// Opaque provider state replayed verbatim within the same turn and provider (Claude thinking with its signature, OpenAI
/// reasoning with encrypted content). Never shown to people and never carried across turns or providers.
/// </summary>
public sealed record ProviderState(string Provider, string Json) : ModelItem;

public enum StopKind
{
    EndTurn,
    ToolUse,
    MaxTokens,
    Refusal,
}

public sealed record ModelUsage(int InputTokens, int OutputTokens, int CachedInputTokens, string Model)
{
    public static ModelUsage None(string model) => new(0, 0, 0, model);
}

public sealed record ModelResponse(StopKind Stop, IReadOnlyList<ModelItem> Output, ModelUsage Usage);

/// <summary>Records what each model request used (metering and spend budgets, ADR 0024). Called after every request.</summary>
public interface IModelUsageMeter
{
    /// <summary>Throws <see cref="BudgetExceededException"/> when the tenant's spend budget is used up.</summary>
    Task EnsureWithinBudgetAsync(CancellationToken ct);

    Task RecordAsync(string provider, ModelUsage usage, CancellationToken ct);
}

/// <summary>No metering (tests, scripted runs).</summary>
public sealed class NoMeter : IModelUsageMeter
{
    public static readonly NoMeter Instance = new();

    public Task EnsureWithinBudgetAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RecordAsync(string provider, ModelUsage usage, CancellationToken ct) => Task.CompletedTask;
}
