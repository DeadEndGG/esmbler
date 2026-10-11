using Esmbler.Assistant;
using Esmbler.Assistant.Models;
using Esmbler.Kernel.Infrastructure.Assistant;
using Esmbler.Kernel.Tenancy;

namespace Esmbler.Api.Assistant;

/// <summary>
/// Meters the assistant's model requests for one turn (ADR 0024, KI-002): checks the workspace's daily spend budget
/// before each request and records what each request used.
/// </summary>
public sealed class AiUsageMeter(AiSettingsService ai, RequestContext ctx, string conversationId, string modelOptionId) : IModelUsageMeter
{
    public async Task EnsureWithinBudgetAsync(CancellationToken ct)
    {
        if (!await ai.WithinBudgetAsync(ctx.TenantId, ctx.EnvironmentId, ct))
        {
            throw new BudgetExceededException("This workspace has used today's AI budget. An administrator can raise it under AI assistant settings; it resets at midnight UTC.");
        }
    }

    public Task RecordAsync(string provider, ModelUsage usage, CancellationToken ct) =>
        ai.RecordUsageAsync(ctx, conversationId, modelOptionId, provider, usage.Model, usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.CacheWriteTokens, ct);
}
