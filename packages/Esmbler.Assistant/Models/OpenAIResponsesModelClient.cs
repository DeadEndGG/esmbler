using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esmbler.Assistant.Models;

/// <summary>Where and how to reach an OpenAI Responses endpoint (OpenAI itself, or Azure OpenAI's v1 surface).</summary>
public sealed record OpenAIEndpoint(Uri BaseUrl, string ApiKey, bool Azure)
{
    public static readonly Uri OpenAIBaseUrl = new("https://api.openai.com/v1/");

    public static OpenAIEndpoint OpenAI(string apiKey) => new(OpenAIBaseUrl, apiKey, false);

    /// <summary>Azure OpenAI: {endpoint}/openai/v1/, key in the api-key header; the model is the deployment name.</summary>
    public static OpenAIEndpoint AzureOpenAI(Uri resource, string apiKey) => new(new Uri(resource, "/openai/v1/"), apiKey, true);

    public override string ToString() => $"OpenAIEndpoint {{ BaseUrl = {BaseUrl}, Azure = {Azure} }}"; // never the key
}

/// <summary>
/// OpenAI (and Azure OpenAI) through the Responses API (ADR 0024), called as documented REST so the request is exact:
/// function tools with <c>strict: false</c>, <c>store: false</c> (no conversation state kept at the provider), reasoning
/// effort, and reasoning items with encrypted content replayed within the turn. Errors never carry the provider's
/// response body, the endpoint or the key.
/// </summary>
public sealed class OpenAIResponsesModelClient(ModelSettings settings, OpenAIEndpoint endpoint, HttpMessageHandler? handler = null) : IModelClient
{
    public string Provider => endpoint.Azure ? "azure-openai" : "openai";

    public string Model => settings.Model;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = settings.Timeout;
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, "responses"))
        {
            Content = new StringContent(Body(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (endpoint.Azure)
        {
            message.Headers.Add("api-key", endpoint.ApiKey);
        }
        else
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AssistantUnavailableException("The AI service did not answer in time. Try again in a minute.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AssistantUnavailableException("The AI service is busy right now. Try again in a minute.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // The body can echo request data: never shown or logged.
                throw new AssistantUnavailableException($"The AI service returned an error ({(int)response.StatusCode}).");
            }

            JsonObject json;
            try
            {
                json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new AssistantUnavailableException("The AI service returned an answer that could not be read.");
            }

            return Parse(json, settings.Model);
        }
    }

    /// <summary>The request body (internal for translation tests).</summary>
    internal JsonObject Body(ModelRequest request) => new()
    {
        ["model"] = settings.Model,
        ["instructions"] = request.Instructions,
        ["input"] = Input(request.Items),
        ["tools"] = new JsonArray([.. request.Tools.Select(Tool)]),
        ["store"] = false,
        ["include"] = new JsonArray("reasoning.encrypted_content"),
        ["reasoning"] = new JsonObject { ["effort"] = settings.Effort },
        ["max_output_tokens"] = settings.MaxOutputTokens,
        ["parallel_tool_calls"] = false,
    };

    internal static JsonArray Input(IReadOnlyList<ModelItem> items)
    {
        var input = new JsonArray();
        foreach (var item in items)
        {
            input.Add(item switch
            {
                UserText u => new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = u.Text },
                AssistantText a => new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = a.Text }) },
                ToolCall c => new JsonObject { ["type"] = "function_call", ["call_id"] = c.CallId, ["name"] = c.Name, ["arguments"] = string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson },
                ToolResult r => new JsonObject { ["type"] = "function_call_output", ["call_id"] = r.CallId, ["output"] = r.Content },
                ProviderState { Provider: "openai" or "azure-openai" } p => JsonNode.Parse(p.Json),
                _ => throw new InvalidOperationException("Provider state from another provider cannot be replayed."),
            });
        }

        return input;
    }

    internal static ModelResponse Parse(JsonObject json, string model)
    {
        var output = new List<ModelItem>();
        var refused = false;
        foreach (var item in json["output"] as JsonArray ?? [])
        {
            switch (item?["type"]?.GetValue<string>())
            {
                case "reasoning":
                    // Replayed verbatim within the turn (encrypted content); never shown.
                    var state = item.DeepClone().AsObject();
                    state.Remove("status");
                    output.Add(new ProviderState("openai", state.ToJsonString()));
                    break;
                case "message":
                    foreach (var part in item["content"] as JsonArray ?? [])
                    {
                        switch (part?["type"]?.GetValue<string>())
                        {
                            case "output_text":
                                output.Add(new AssistantText(part["text"]?.GetValue<string>() ?? string.Empty));
                                break;
                            case "refusal":
                                refused = true;
                                break;
                        }
                    }

                    break;
                case "function_call":
                    output.Add(new ToolCall(item["call_id"]?.GetValue<string>() ?? string.Empty, item["name"]?.GetValue<string>() ?? string.Empty, item["arguments"]?.GetValue<string>() ?? "{}"));
                    break;
            }
        }

        var status = json["status"]?.GetValue<string>();
        var reason = json["incomplete_details"]?["reason"]?.GetValue<string>();
        var stop = refused || reason == "content_filter" ? StopKind.Refusal
            : output.Exists(i => i is ToolCall) ? StopKind.ToolUse
            : status == "incomplete" && reason == "max_output_tokens" ? StopKind.MaxTokens
            : StopKind.EndTurn;
        if (status == "failed")
        {
            throw new AssistantUnavailableException("The AI service could not finish this request.");
        }

        // OpenAI's input_tokens include cached tokens; normalize to uncached input plus cache reads. The model recorded is
        // the one configured (for Azure, the deployment name), which is what the catalog prices.
        var usage = json["usage"];
        var cached = usage?["input_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;
        return new ModelResponse(stop, output, new ModelUsage(
            Math.Max(0, (usage?["input_tokens"]?.GetValue<int>() ?? 0) - cached),
            usage?["output_tokens"]?.GetValue<int>() ?? 0,
            cached,
            model));
    }

    private static JsonObject Tool(ToolDefinition d) => new()
    {
        ["type"] = "function",
        ["name"] = d.Name,
        ["description"] = d.Description,
        ["parameters"] = d.InputSchema.ValueKind == JsonValueKind.Object ? JsonNode.Parse(d.InputSchema.GetRawText()) : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        // Our MCP schemas have optional properties and free-form objects: strict mode would be rejected or silently dropped.
        ["strict"] = false,
    };
}
