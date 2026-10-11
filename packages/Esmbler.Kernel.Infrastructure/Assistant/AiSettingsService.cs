using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Assistant;

public sealed record AiModelView(string Id, string Label, string Provider, string Model, bool Available, bool Evaluated, decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>A workspace's AI settings as administrators see them: model choices and spend, never keys.</summary>
public sealed record AiSettingsView(
    string? ModelId,
    AiModelView? Effective,
    string? Effort,
    decimal DailyBudgetUsd,
    decimal SpentTodayUsd,
    bool AllowRecordsRead,
    string ExternalAssistants,
    IReadOnlyList<AiModelView> Models,
    int Version);

public sealed record AiSettingsRequest(string? ModelId, string? Effort, decimal DailyBudgetUsd, bool AllowRecordsRead, string ExternalAssistants, int? Version);

/// <summary>What the assistant runs with for one workspace.</summary>
public sealed record AiResolved(AiModelOption? Model, AiCredential? Credential, string Effort, decimal DailyBudgetUsd, bool AllowRecordsRead, string ExternalAssistants);

/// <summary>
/// Per-workspace AI provider settings, usage metering and the daily spend budget (ADR 0024, REQ-AI-006, KI-002). Only
/// permission administrators read or change settings (fresh facts for changes); agents are refused; changes are
/// audited without keys. A workspace can choose only evaluated models whose credential the operator configured.
/// </summary>
public sealed class AiSettingsService(Database database, ConfigStore configs, AccessService access, AiCatalog catalog, IClock clock)
{
    public static readonly string[] Efforts = ["low", "medium", "high", "max"];
    public static readonly string[] ExternalModes = ["off", "non_production", "all"];

    public Task<AiSettingsView> GetAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAdminAsync(db, ctx, fresh: false, ct);
            return await ViewAsync(db, ctx.TenantId, ct);
        }, ct);

    public Task<AiSettingsView> SaveAsync(RequestContext ctx, AiSettingsRequest request, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAdminAsync(db, ctx, fresh: true, ct);
            var slug = await SlugAsync(db, ctx.TenantId, ct);
            var errors = new List<FieldError>();
            if (request.ModelId is { } id)
            {
                if (catalog.Find(id) is not { } model || !model.AllowedFor(slug))
                {
                    errors.Add(new FieldError("modelId", "not_found", $"Choose one of the offered models: {string.Join(", ", catalog.Models.Select(m => m.Id))}."));
                }
                else if (!model.Evaluated)
                {
                    errors.Add(new FieldError("modelId", "not_evaluated", $"{model.Label} has not passed the builder evaluation on this platform yet.", "Choose an evaluated model or ask the operator to run the evaluation."));
                }
                else if (!catalog.Available(model))
                {
                    errors.Add(new FieldError("modelId", "not_configured", $"{model.Label} has no credential on this platform.", "Ask the operator to configure its credential, or choose another model."));
                }
            }

            if (request.Effort is { } effort && !Efforts.Contains(effort))
            {
                errors.Add(new FieldError("effort", "invalid", "Effort is low, medium, high or max."));
            }

            if (request.DailyBudgetUsd < 0 || request.DailyBudgetUsd > catalog.MaxDailyBudgetUsd)
            {
                errors.Add(new FieldError("dailyBudgetUsd", "invalid", $"Set a daily budget from 0 (no AI) to {catalog.MaxDailyBudgetUsd:0.##} US dollars, the platform's limit."));
            }

            if (!ExternalModes.Contains(request.ExternalAssistants))
            {
                errors.Add(new FieldError("externalAssistants", "invalid", "External assistants are off, non_production or all."));
            }

            if (errors.Count > 0)
            {
                throw new ValidationFailedException(errors);
            }

            // Locked so a concurrent consent (ADR 0025) sees either the old or the new policy, never a mix.
            var row = (await db.AiSettings.FromSqlInterpolated($"SELECT * FROM kernel.ai_settings FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
            var previous = row is null ? null : new { row.ModelId, row.Effort, budget = row.DailyBudgetUsd, row.AllowRecordsRead, row.ExternalAssistants };
            if (row is null)
            {
                row = new AiSettingsRow { TenantId = ctx.TenantId };
                db.AiSettings.Add(row);
            }
            else if (request.Version is { } version && version != row.Version)
            {
                throw KernelException.Conflict("stale_version", "Someone changed the AI settings meanwhile.", suggestion: "Reload and apply your change again.");
            }
            else
            {
                row.Version++;
            }

            row.ModelId = request.ModelId;
            row.Effort = request.Effort;
            row.DailyBudgetUsd = Math.Round(request.DailyBudgetUsd, 2);
            row.AllowRecordsRead = request.AllowRecordsRead;
            row.ExternalAssistants = request.ExternalAssistants;
            row.UpdatedBy = ctx.PrincipalId;
            row.UpdatedAt = clock.UtcNow;
            // Narrowing the policy ends the external assistants' connections it no longer allows (ADR 0025).
            var revoked = await RevokeDisallowedConnectionsAsync(db, ctx, row.ExternalAssistants, row.AllowRecordsRead, ct);
            AuditWriter.Add(db, ctx, "ai.settings_save", "ai_settings", ctx.TenantId.ToString(), null,
                new { previous, request.ModelId, request.Effort, budget = row.DailyBudgetUsd, request.AllowRecordsRead, request.ExternalAssistants, revokedConnections = revoked }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
            return await ViewAsync(db, ctx.TenantId, ct);
        }, ct);

    /// <summary>What the assistant should use for this workspace (no rights check: the caller is the platform itself).</summary>
    public Task<AiResolved> ResolveAsync(Guid tenantId, Guid environmentId, CancellationToken ct) =>
        database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            var row = await db.AiSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var slug = await SlugAsync(db, tenantId, ct);

            // A choice the operator no longer offers (not evaluated, no credential, not for this workspace) falls back to the default.
            var model = row?.ModelId is { } id && catalog.Find(id) is { } chosen && catalog.Usable(chosen, slug) ? chosen : catalog.DefaultFor(slug);
            return new AiResolved(model, model is null ? null : catalog.CredentialFor(model), row?.Effort ?? model?.Effort ?? "medium",
                row?.DailyBudgetUsd ?? 20, row?.AllowRecordsRead ?? false, row?.ExternalAssistants ?? "off");
        }, ct);

    /// <summary>Whether the workspace still has budget today (UTC day), across all its environments.</summary>
    public Task<bool> WithinBudgetAsync(Guid tenantId, Guid environmentId, CancellationToken ct) =>
        database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            var budget = await db.AiSettings.AsNoTracking().Select(s => (decimal?)s.DailyBudgetUsd).FirstOrDefaultAsync(ct) ?? 20;
            return budget > 0 && await SpentTodayAsync(db, ct) < budget;
        }, ct);

    /// <summary>Records one model request, priced with the catalog model the assistant resolved (<paramref name="modelOptionId"/>).</summary>
    public Task RecordUsageAsync(RequestContext ctx, string? conversationId, string? modelOptionId, string provider, string model, int freshInput, int output, int cacheRead, int cacheWrite, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var option = catalog.Find(modelOptionId);
            db.AiUsage.Add(new AiUsageRow
            {
                TenantId = ctx.TenantId,
                Id = Guid.NewGuid(),
                EnvironmentId = ctx.EnvironmentId,
                PrincipalId = ctx.PrincipalId,
                ConversationId = conversationId,
                Provider = provider,
                Model = model.Length > 100 ? model[..100] : model,
                InputTokens = freshInput,
                OutputTokens = output,
                CachedTokens = cacheRead,
                CostUsd = Math.Round(catalog.Cost(option, freshInput, output, cacheRead, cacheWrite), 6),
                At = clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    private async Task<int> RevokeDisallowedConnectionsAsync(KernelDbContext db, RequestContext ctx, string policy, bool allowRecordsRead, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var production = await db.Environments.AsNoTracking().Where(e => e.TenantId == ctx.TenantId && e.Kind == "production").Select(e => e.Id).ToListAsync(ct);
        var ended = await db.McpConnections.AsNoTracking()
            .Where(c => c.RevokedAt == null && (policy == "off" || (policy == "non_production" && production.Contains(c.EnvironmentId)) || (!allowRecordsRead && c.Scopes.Contains(AgentScopes.RecordsRead))))
            .Select(c => new { c.Id, c.AgentId }).ToListAsync(ct);
        if (ended.Count == 0)
        {
            return 0;
        }

        var ids = ended.Select(c => c.Id).ToList();
        var agents = ended.Select(c => c.AgentId).ToList();
        await db.McpConnections.Where(c => ids.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedBy, ctx.PrincipalId), ct);
        await db.OAuthRefreshTokens.Where(t => ids.Contains(t.ConnectionId) && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.Principals.Where(p => agents.Contains(p.Id) && p.AgentTenant == ctx.TenantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Disabled, true), ct);
        return ended.Count;
    }

    private async Task<AiSettingsView> ViewAsync(KernelDbContext db, Guid tenantId, CancellationToken ct)
    {
        var row = await db.AiSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var slug = await SlugAsync(db, tenantId, ct);
        var effective = row?.ModelId is { } id && catalog.Find(id) is { } chosen && catalog.Usable(chosen, slug) ? chosen : catalog.DefaultFor(slug);
        return new AiSettingsView(row?.ModelId, effective is null ? null : View(effective), row?.Effort, row?.DailyBudgetUsd ?? 20, await SpentTodayAsync(db, ct),
            row?.AllowRecordsRead ?? false, row?.ExternalAssistants ?? "off", [.. catalog.Models.Where(m => m.AllowedFor(slug)).Select(View)], row?.Version ?? 0);
    }

    private static async Task<string> SlugAsync(KernelDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Slug).FirstAsync(ct);

    private AiModelView View(AiModelOption m) => new(m.Id, m.Label, m.Provider, m.Model, catalog.Available(m), m.Evaluated, m.InputPerMillion, m.OutputPerMillion);

    private async Task<decimal> SpentTodayAsync(KernelDbContext db, CancellationToken ct)
    {
        var start = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        return await db.AiUsage.Where(u => u.At >= start).SumAsync(u => (decimal?)u.CostUsd, ct) ?? 0;
    }

    private async Task RequireAdminAsync(KernelDbContext db, RequestContext ctx, bool fresh, CancellationToken ct)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents cannot see or change AI provider settings.");
        }

        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh);
        if (!perms.Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden("AI provider settings are for permission administrators.");
        }
    }
}
