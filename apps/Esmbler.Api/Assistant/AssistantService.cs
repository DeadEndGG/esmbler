using System.Collections.Concurrent;
using System.Security.Claims;
using Esmbler.Api.Endpoints;
using Esmbler.Assistant;
using Esmbler.Assistant.Models;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Assistant;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Tenancy;
using Microsoft.AspNetCore.SignalR;

namespace Esmbler.Api.Assistant;

/// <summary>
/// Bound from "Esmbler:Assistant". Provider: auto (each workspace's model from the catalog when its credential is
/// configured, otherwise the scripted planner) or scripted (always scripted). Models, credentials and keys: ADR 0024,
/// <see cref="Esmbler.Hosting.AiCatalogFactory"/>.
/// </summary>
public sealed class AssistantOptions
{
    public string Provider { get; set; } = "auto";

    public string McpUrl { get; set; } = "http://localhost:5090/mcp";

    /// <summary>Legacy (ADR 0013): names the default catalog model when Esmbler:Assistant:DefaultModel is not set.</summary>
    public string? Model { get; set; }

    /// <summary>Anthropic API key (Esmbler:Assistant:AnthropicApiKey, or the ANTHROPIC_API_KEY variable): the anthropic-default credential.</summary>
    public string? AnthropicApiKey { get; set; }

    public int MaxToolCalls { get; set; } = 30;

    public int MaxModelTurns { get; set; } = 15;

    public int MaxSeconds { get; set; } = 120;

    public int MaxMessagesPerMinute { get; set; } = 30;

    public int MaxMessageLength { get; set; } = 4000;
}

public sealed record AssistantSelectionDto(string View, string Node, string? Type, string? Label);

public sealed record AssistantMessageRequest(string? ConversationId, string Text, IReadOnlyList<AssistantSelectionDto>? Selection);

public sealed record AssistantAccepted(string ConversationId, string Planner);

/// <summary>Opens the MCP connection the assistant works through. Replaced in tests to reach the in-memory MCP host.</summary>
public interface IAssistantGatewayFactory
{
    Task<IToolGateway> ConnectAsync(string agentToken, AssistantBudget budget, CancellationToken ct);
}

public sealed class HttpAssistantGatewayFactory(AssistantOptions options, IHttpClientFactory http) : IAssistantGatewayFactory
{
    public async Task<IToolGateway> ConnectAsync(string agentToken, AssistantBudget budget, CancellationToken ct) =>
        await McpToolGateway.ConnectAsync(new Uri(options.McpUrl), agentToken, budget, http.CreateClient("mcp"), ct);
}

/// <summary>
/// Runs AI builder conversations (G1-M15). Each message mints a short-lived agent token for the signed-in person
/// (scopes ⊆ their rights), connects to the MCP server with it, and lets a planner work; progress streams to the
/// person's SignalR group. One running turn per conversation; Stop cancels at the next tool boundary.
/// </summary>
public sealed class AssistantService(
    AssistantOptions options,
    IdentityService identity,
    DraftService drafts,
    TokenService tokens,
    IAssistantGatewayFactory gateways,
    AiSettingsService ai,
    IHubContext<LiveHub> hub,
    ILogger<AssistantService> logger)
{
    private readonly ConcurrentDictionary<string, Conversation> conversations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> rate = new();

    /// <summary>Which planner and model this workspace's assistant uses (no keys).</summary>
    public async Task<object> StatusAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var ctx = RequestContextFactory.From(user, "ui");
        var resolved = await ai.ResolveAsync(ctx.TenantId, ctx.EnvironmentId, ct);
        var model = Uses(resolved) ? resolved.Model : null;
        return new { planner = model is null ? "scripted" : model.Provider == "anthropic" ? "claude" : model.Provider, model = model?.Model, provider = model?.Provider };
    }

    private bool Uses(AiResolved resolved) => options.Provider != "scripted" && resolved.Model is not null && resolved.Credential is not null;

    public async Task<AssistantAccepted> SendAsync(ClaimsPrincipal user, AssistantMessageRequest body, CancellationToken ct)
    {
        if (user.FindFirst(ClaimNames.Agent) is not null)
        {
            throw KernelException.Forbidden("AI agents can't start assistant conversations.");
        }

        var ctx = RequestContextFactory.From(user, "ui");
        var text = (body.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new KernelException("empty_message", "Type what you want to change.", 400, "text");
        }

        if (text.Length > options.MaxMessageLength)
        {
            throw new KernelException("message_too_long", $"Keep messages under {options.MaxMessageLength} characters.", 400, "text");
        }

        CheckRate(ctx.PrincipalId);
        Prune();

        // Fails unless the person may edit configuration: the assistant never has more rights than its principal.
        await drafts.CurrentAsync(ctx, ct);

        var conversation = body.ConversationId is { } id && conversations.TryGetValue(id, out var existing)
            ? existing
            : new Conversation(Guid.NewGuid().ToString("N"), ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId);
        if (conversation.TenantId != ctx.TenantId || conversation.EnvironmentId != ctx.EnvironmentId || conversation.PrincipalId != ctx.PrincipalId)
        {
            throw KernelException.NotFound("Conversation");
        }

        // Built before the turn is marked running, so a misconfigured model fails the request without leaving the conversation busy.
        var planner = CreatePlanner(await ai.ResolveAsync(ctx.TenantId, ctx.EnvironmentId, ct), ctx, conversation.Id);
        lock (conversation)
        {
            if (conversation.Running is not null)
            {
                throw KernelException.Conflict("assistant_busy", "The assistant is still working on your last message. Wait for it or press Stop.");
            }

            conversation.Running = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaxSeconds + 30));
        }

        conversation.LastActive = DateTimeOffset.UtcNow;
        conversations[conversation.Id] = conversation;
        var agent = await identity.EnsureAgentAsync(ctx.PrincipalId, ctx.TenantId, ct);
        var token = tokens.IssueAgent(ctx.PrincipalId, user.FindFirstValue("name") ?? string.Empty, ctx.TenantId, ctx.EnvironmentId, agent, AgentScopes.Builder).AccessToken;
        var request = new AssistantRequest(
            conversation.Id,
            text,
            (body.Selection ?? []).Take(10).Select(s => new SelectedElement(s.View, s.Node, s.Type, s.Label)).ToList(),
            conversation.History.ToList());
        conversation.History.Add(new ConversationTurn("user", text));
        _ = Task.Run(() => RunAsync(conversation, planner, request, token), CancellationToken.None);
        return new AssistantAccepted(conversation.Id, planner.Name);
    }

    public void Stop(ClaimsPrincipal user, string conversationId)
    {
        var ctx = RequestContextFactory.From(user, "ui");
        if (!conversations.TryGetValue(conversationId, out var c) || c.PrincipalId != ctx.PrincipalId || c.TenantId != ctx.TenantId)
        {
            throw KernelException.NotFound("Conversation");
        }

        lock (c)
        {
            c.Running?.Cancel();
        }
    }

    /// <summary>The workspace's catalog model through its provider's client, metered; scripted when none is configured.</summary>
    private IPlanner CreatePlanner(AiResolved resolved, RequestContext ctx, string conversationId)
    {
        if (!Uses(resolved))
        {
            return new ScriptedPlanner();
        }

        var model = resolved.Model!;
        var credential = resolved.Credential!;
        var settings = new ModelSettings { Model = model.Model, Effort = resolved.Effort, FallbackModel = model.FallbackModel };
        IModelClient client = model.Provider switch
        {
            "openai" => new OpenAIResponsesModelClient(settings, OpenAIEndpoint.OpenAI(credential.ApiKey!)),
            "azure-openai" when credential.Endpoint is { } endpoint => new OpenAIResponsesModelClient(settings, OpenAIEndpoint.AzureOpenAI(endpoint, credential.ApiKey!)),
            "anthropic" => new AnthropicModelClient(settings, credential.ApiKey),
            _ => throw new KernelException("ai_unavailable", "The AI model for this workspace is not configured correctly.", 503, "modelId", "Ask a permission administrator to choose another model in Settings > AI."),
        };
        return new ToolLoopPlanner(client, new AiUsageMeter(ai, ctx, conversationId, model.Id));
    }

    private async Task RunAsync(Conversation conversation, IPlanner planner, AssistantRequest request, string token)
    {
        var sink = new HubSink(hub, conversation.PrincipalId, conversation.History);
        var ct = conversation.Running!.Token;
        var budget = new AssistantBudget(options.MaxToolCalls, options.MaxModelTurns, options.MaxSeconds);
        try
        {
            await using var gateway = await gateways.ConnectAsync(token, budget, ct);
            var outcome = await planner.RunAsync(request, gateway, sink, budget, ct);
            logger.LogInformation("Assistant turn {Conversation} finished: {Outcome} ({Planner})", conversation.Id, outcome, planner.Name);
        }
        catch (OperationCanceledException)
        {
            await sink.EmitAsync(new AssistantEvent(conversation.Id, AssistantEvent.Types.Stopped), CancellationToken.None);
        }
        catch (BudgetExceededException ex)
        {
            await sink.EmitAsync(new AssistantEvent(conversation.Id, AssistantEvent.Types.Error, ex.Message), CancellationToken.None);
        }
        catch (AssistantUnavailableException ex)
        {
            await sink.EmitAsync(new AssistantEvent(conversation.Id, AssistantEvent.Types.Error, ex.Message), CancellationToken.None);
        }
#pragma warning disable CA1031 // A background turn must always end with an event the person can see.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Assistant turn {Conversation} failed", conversation.Id);
            await sink.EmitAsync(new AssistantEvent(conversation.Id, AssistantEvent.Types.Error, "Something went wrong while making the change. Nothing else was changed; try again."), CancellationToken.None);
        }
        finally
        {
            lock (conversation)
            {
                conversation.Running?.Dispose();
                conversation.Running = null;
            }
        }
    }

    /// <summary>Drops idle conversations; history is only short-term context, the draft is the durable record.</summary>
    private void Prune()
    {
        foreach (var (key, c) in conversations)
        {
            if (c.Running is null && DateTimeOffset.UtcNow - c.LastActive > TimeSpan.FromHours(2))
            {
                conversations.TryRemove(key, out _);
            }
        }

        foreach (var (principal, q) in rate)
        {
            lock (q)
            {
                if (q.Count == 0 || DateTimeOffset.UtcNow - q.Last() > TimeSpan.FromMinutes(1))
                {
                    rate.TryRemove(principal, out _);
                }
            }
        }
    }

    private void CheckRate(Guid principal)
    {
        var q = rate.GetOrAdd(principal, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            var now = DateTimeOffset.UtcNow;
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1))
            {
                q.Dequeue();
            }

            if (q.Count >= options.MaxMessagesPerMinute)
            {
                throw new KernelException("rate_limited", "You're sending messages faster than the assistant can work. Wait a moment.", 429);
            }

            q.Enqueue(now);
        }
    }

    private sealed class Conversation(string id, Guid tenantId, Guid environmentId, Guid principalId)
    {
        public string Id { get; } = id;

        public Guid TenantId { get; } = tenantId;

        public Guid EnvironmentId { get; } = environmentId;

        public Guid PrincipalId { get; } = principalId;

        public List<ConversationTurn> History { get; } = [];

        public CancellationTokenSource? Running { get; set; }

        public DateTimeOffset LastActive { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class HubSink(IHubContext<LiveHub> hub, Guid principal, List<ConversationTurn> history) : IAssistantSink
    {
        public async Task EmitAsync(AssistantEvent e, CancellationToken ct)
        {
            if (e.Type is AssistantEvent.Types.Done or AssistantEvent.Types.Message or AssistantEvent.Types.Question && e.Text is not null)
            {
                lock (history)
                {
                    history.Add(new ConversationTurn("assistant", e.Text!));
                }
            }

            await hub.Clients.Group(LiveHub.UserGroup(principal)).SendAsync("assistantEvent", e, ct);
        }
    }
}
