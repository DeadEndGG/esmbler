using System.Text.Json;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Assistant;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Assistant;

/// <summary>
/// REQ-AI-006, REQ-AISAFE-004, REQ-ADM-002 (ADR 0024): each workspace chooses its AI model from the operator's catalog;
/// only permission administrators change it, never agents; keys never appear in settings, usage or audit; usage is
/// metered per workspace and a daily budget stops the assistant; workspaces never see each other's settings or spend.
/// </summary>
[Collection(PlatformCollection.Name)]
public class AiSettingsTests(PlatformFixture f)
{
    private const string AnthropicKey = "sk-ant-test-0123456789";
    private const string OpenAIKey = "sk-openai-test-9876543210";

    private static readonly AiModelOption Opus = new("claude-opus-5-5", "Claude Opus 5.5", "anthropic", "claude-opus-5-5", "anthropic-default", "medium", 5m, 25m, true);
    private static readonly AiModelOption Sol = new("gpt-6.1-sol", "GPT-6.1 Sol", "openai", "gpt-6.1-sol", "openai-default", "medium", 2m, 10m, true);
    private static readonly AiModelOption Astra = new("gpt-6-astra", "GPT-6 Astra", "openai", "gpt-6-astra", "openai-default", "medium", 10m, 50m, false);
    private static readonly AiModelOption Gemini = new("gemini", "Gemini", "openai", "gemini", "gemini-key", "medium", 1m, 1m, true);
    private static readonly AiModelOption OwnKey = new("claude-own", "Opus (own key)", "anthropic", "claude-opus-5-5", "anthropic-default", "medium", 5m, 25m, true, null, ["nobody-else"]);

    private AiSettingsService Service() => new(
        f.Get<Database>(),
        f.Get<ConfigStore>(),
        f.Get<AccessService>(),
        new AiCatalog([Opus, Sol, Astra, Gemini, OwnKey], new Dictionary<string, AiCredential> { ["anthropic-default"] = new("anthropic", AnthropicKey, null), ["openai-default"] = new("openai", OpenAIKey, null) }, "claude-opus-5-5", 1000m),
        f.Get<IClock>());

    [Fact]
    public async Task Two_workspaces_use_different_providers_and_never_see_each_others_settings_or_spend()
    {
        var acme = await f.NewTenantAsync("aia", "1.9.0");
        var globex = await f.NewTenantAsync("aib", "1.9.0");
        var service = Service();
        var admin = f.As(acme, "admin");

        // Nothing chosen: the operator's default.
        var initial = await service.GetAsync(admin, default);
        Assert.Equal("claude-opus-5-5", initial.Effective!.Id);
        Assert.Equal(20m, initial.DailyBudgetUsd);
        Assert.Equal("off", initial.ExternalAssistants);

        var saved = await service.SaveAsync(admin, new AiSettingsRequest("gpt-6.1-sol", "high", 5m, false, "non_production", null), default);
        Assert.Equal(("gpt-6.1-sol", "high", 1), (saved.Effective!.Id, saved.Effort, saved.Version));
        var acmeResolved = await service.ResolveAsync(acme.TenantId, acme.Environments["prod"], default);
        Assert.Equal(("openai", "high", OpenAIKey), (acmeResolved.Model!.Provider, acmeResolved.Effort, acmeResolved.Credential!.ApiKey));
        var globexResolved = await service.ResolveAsync(globex.TenantId, globex.Environments["prod"], default);
        Assert.Equal(("anthropic", AnthropicKey), (globexResolved.Model!.Provider, globexResolved.Credential!.ApiKey));

        // Usage is metered per workspace; Globex's spend is not Acme's.
        await service.RecordUsageAsync(admin, "c1", "gpt-6.1-sol", "openai", "gpt-6.1-sol", 1_000_000, 100_000, 0, 0, default);
        Assert.Equal(3m, (await service.GetAsync(admin, default)).SpentTodayUsd);
        Assert.Equal(0m, (await service.GetAsync(f.As(globex, "admin"), default)).SpentTodayUsd);
        Assert.True(await service.WithinBudgetAsync(acme.TenantId, acme.Environments["prod"], default));
        await service.RecordUsageAsync(f.As(acme, "admin", env: "sandbox"), "c2", "gpt-6.1-sol", "openai", "gpt-6.1-sol", 1_000_000, 0, 0, 0, default);
        Assert.False(await service.WithinBudgetAsync(acme.TenantId, acme.Environments["prod"], default)); // one budget across environments
        Assert.True(await service.WithinBudgetAsync(globex.TenantId, globex.Environments["prod"], default));

        // A budget of zero turns the AI off.
        await service.SaveAsync(f.As(globex, "admin"), new AiSettingsRequest(null, null, 0m, false, "off", null), default);
        Assert.False(await service.WithinBudgetAsync(globex.TenantId, globex.Environments["prod"], default));

        // Keys never appear in what people see or in the audit log.
        var shown = JsonSerializer.Serialize(await service.GetAsync(admin, default));
        Assert.DoesNotContain(OpenAIKey, shown, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicKey, shown, StringComparison.Ordinal);
        var audit = await f.Get<Database>().InTenantAsync(acme.TenantId, acme.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "ai.settings_save").Select(a => a.Details).ToListAsync(), default);
        Assert.Single(audit);
        Assert.DoesNotContain(audit, d => d!.Contains("sk-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_permission_administrators_choose_evaluated_configured_models()
    {
        var t = await f.NewTenantAsync("aic", "1.9.0");
        var service = Service();
        var admin = f.As(t, "admin");
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => service.GetAsync(f.As(t, "builder"), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => service.SaveAsync(f.As(t, "builder"), new AiSettingsRequest("gpt-6.1-sol", null, 5, false, "off", null), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => service.SaveAsync(f.As(t, "admin", agent: Guid.NewGuid()), new AiSettingsRequest("gpt-6.1-sol", null, 5, false, "off", null), default))).Status);

        var notEvaluated = await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(admin, new AiSettingsRequest("gpt-6-astra", null, 5, false, "off", null), default));
        Assert.Contains(notEvaluated.Errors, e => e.Code == "not_evaluated");
        var noKey = await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(admin, new AiSettingsRequest("gemini", null, 5, false, "off", null), default));
        Assert.Contains(noKey.Errors, e => e.Code == "not_configured");
        // Another workspace's own-key model is neither offered nor accepted, and budgets stay under the platform's ceiling.
        Assert.DoesNotContain((await service.GetAsync(admin, default)).Models, m => m.Id == "claude-own");
        Assert.Contains((await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(admin, new AiSettingsRequest("claude-own", null, 5, false, "off", null), default))).Errors, e => e.Field == "modelId");
        Assert.Contains((await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(admin, new AiSettingsRequest(null, null, 1001, false, "off", null), default))).Errors, e => e.Field == "dailyBudgetUsd");
        var bad = await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(admin, new AiSettingsRequest("nope", "turbo", -1, false, "everywhere", null), default));
        Assert.Equal(["modelId", "effort", "dailyBudgetUsd", "externalAssistants"], bad.Errors.Select(e => e.Field));

        var first = await service.SaveAsync(admin, new AiSettingsRequest("claude-opus-5-5", null, 10, true, "off", null), default);
        Assert.Equal(409, (await Assert.ThrowsAsync<KernelException>(() => service.SaveAsync(admin, new AiSettingsRequest(null, null, 10, false, "off", first.Version + 5), default))).Status);
    }
}
