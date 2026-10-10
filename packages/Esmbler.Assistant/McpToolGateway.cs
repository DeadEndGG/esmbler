using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Esmbler.Assistant;

/// <summary>
/// The assistant's only way to change anything: an MCP client authenticated with a short-lived agent token
/// (audience esmbler-mcp, scopes ⊆ the person's rights). Counts calls against the turn budget.
/// </summary>
public sealed class McpToolGateway(McpClient client, AssistantBudget budget) : IToolGateway
{
    private int calls;

    public int Calls => calls;

    public static async Task<McpToolGateway> ConnectAsync(Uri endpoint, string agentToken, AssistantBudget budget, HttpClient? http = null, CancellationToken ct = default)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {agentToken}" },
        };
        var transport = http is null ? new HttpClientTransport(options) : new HttpClientTransport(options, http, ownsHttpClient: false);
        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        return new McpToolGateway(client, budget);
    }

    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken ct)
    {
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new ToolDefinition(t.Name, t.Description ?? string.Empty, t.JsonSchema, t.ProtocolTool.Annotations?.ReadOnlyHint ?? false)).ToList();
    }

    public async Task<ToolOutcome> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        if (Interlocked.Increment(ref calls) > budget.MaxToolCalls)
        {
            throw new BudgetExceededException($"Reached the limit of {budget.MaxToolCalls} tool calls for one request.");
        }

        var args = arguments.ToDictionary(p => p.Key, p => (object?)(p.Value is null ? null : JsonSerializer.Deserialize<JsonElement>(p.Value.ToJsonString())));
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = string.Join(string.Empty, result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return new ToolOutcome(result.IsError == true, text);
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
