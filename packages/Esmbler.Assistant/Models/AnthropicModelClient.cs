using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Esmbler.Assistant.Models;

/// <summary>
/// Claude through the Anthropic Messages API (ADR 0013, ADR 0024): prompt caching on the tools and system prefix, effort,
/// the server-side refusal fallback model, thinking blocks replayed within the turn. The API key comes from a named
/// credential the host resolves (configuration or ANTHROPIC_API_KEY), never from code or tenant data.
/// </summary>
public sealed class AnthropicModelClient(ModelSettings settings, string? apiKey) : IModelClient
{
    public string Provider => "anthropic";

    public string Model => settings.Model;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        // One request may take at most the configured time; the SDK retries transient failures twice.
        var client = string.IsNullOrEmpty(apiKey)
            ? new AnthropicClient { Timeout = settings.Timeout }
            : new AnthropicClient { ApiKey = apiKey, Timeout = settings.Timeout };
        BetaMessage response;
        try
        {
            var parameters = new MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = settings.MaxOutputTokens,
                // Tools then system form a stable prefix: cache it across the turns of a conversation.
                System = new List<BetaTextBlockParam> { new() { Text = request.Instructions, CacheControl = new BetaCacheControlEphemeral() } },
                Messages = Messages(request.Items),
                Tools = request.Tools.Select(ToTool).ToList(),
                OutputConfig = new BetaOutputConfig { Effort = settings.Effort },
            };
            if (settings.FallbackModel is { Length: > 0 } fallback)
            {
                parameters = parameters with
                {
                    Betas = ["server-side-fallback-2026-06-01"],
                    Fallbacks = new BetaFallbacksParam([new BetaFallbackParam { Model = fallback }]),
                };
            }

            response = await client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException)
        {
            throw new AssistantUnavailableException("The AI service is busy right now. Try again in a minute.");
        }
        catch (AnthropicApiException ex)
        {
            throw new AssistantUnavailableException($"The AI service returned an error ({ex.GetType().Name}).");
        }

        var output = new List<ModelItem>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                output.Add(new AssistantText(t.Text));
            }
            else if (block.TryPickThinking(out var thinking))
            {
                output.Add(new ProviderState(Provider, new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Thinking, ["signature"] = thinking.Signature }.ToJsonString()));
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                output.Add(new ProviderState(Provider, new JsonObject { ["type"] = "redacted_thinking", ["data"] = redacted.Data }.ToJsonString()));
            }
            else if (block.TryPickToolUse(out var use))
            {
                var args = new JsonObject();
                foreach (var (key, value) in use.Input)
                {
                    args[key] = JsonNode.Parse(value.GetRawText());
                }

                output.Add(new ToolCall(use.ID, use.Name, args.ToJsonString()));
            }
        }

        var stop = response.StopReason == "refusal" ? StopKind.Refusal
            : output.Exists(i => i is ToolCall) ? StopKind.ToolUse
            : response.StopReason == "max_tokens" ? StopKind.MaxTokens
            : StopKind.EndTurn;
        var usage = new ModelUsage((int)response.Usage.InputTokens, (int)response.Usage.OutputTokens, (int)(response.Usage.CacheReadInputTokens ?? 0), settings.Model);
        return new ModelResponse(stop, output, usage);
    }

    /// <summary>Groups the transcript into alternating messages: assistant items in one message, tool results in the next.</summary>
    internal static List<BetaMessageParam> Messages(IReadOnlyList<ModelItem> items)
    {
        var messages = new List<BetaMessageParam>();
        List<BetaContentBlockParam>? assistant = null;
        List<BetaContentBlockParam>? results = null;

        void Flush()
        {
            if (assistant is not null)
            {
                messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });
                assistant = null;
            }

            if (results is not null)
            {
                messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
                results = null;
            }
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case UserText u:
                    Flush();
                    messages.Add(new BetaMessageParam { Role = Role.User, Content = u.Text });
                    break;
                case AssistantText or ToolCall or ProviderState:
                    if (results is not null)
                    {
                        Flush();
                    }

                    assistant ??= [];
                    assistant.Add(item switch
                    {
                        AssistantText a => new BetaTextBlockParam { Text = a.Text },
                        ToolCall c => new BetaToolUseBlockParam { ID = c.CallId, Name = c.Name, Input = Arguments(c.ArgumentsJson) },
                        ProviderState { Provider: "anthropic" } p => State(p.Json),
                        _ => throw new InvalidOperationException("Provider state from another provider cannot be replayed."),
                    });
                    break;
                case ToolResult r:
                    if (assistant is not null)
                    {
                        messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });
                        assistant = null;
                    }

                    results ??= [];
                    results.Add(new BetaToolResultBlockParam { ToolUseID = r.CallId, Content = r.Content, IsError = r.IsError });
                    break;
            }
        }

        Flush();
        return messages;
    }

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
    }

    private static BetaContentBlockParam State(string json)
    {
        var node = JsonNode.Parse(json)!;
        return node["type"]?.GetValue<string>() == "redacted_thinking"
            ? new BetaRedactedThinkingBlockParam { Data = node["data"]!.GetValue<string>() }
            : new BetaThinkingBlockParam { Thinking = node["thinking"]!.GetValue<string>(), Signature = node["signature"]!.GetValue<string>() };
    }

    private static BetaToolUnion ToTool(ToolDefinition d)
    {
        var properties = new Dictionary<string, JsonElement>();
        var required = new List<string>();
        if (d.InputSchema.ValueKind == JsonValueKind.Object)
        {
            if (d.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                {
                    properties[p.Name] = p.Value.Clone();
                }
            }

            if (d.InputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                required.AddRange(req.EnumerateArray().Select(r => r.GetString()!).Where(r => r is not null));
            }
        }

        return new BetaTool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }
}
