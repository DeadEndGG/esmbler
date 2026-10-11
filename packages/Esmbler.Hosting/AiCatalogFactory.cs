using System.Globalization;
using Esmbler.Kernel.Infrastructure.Assistant;
using Microsoft.Extensions.Configuration;

namespace Esmbler.Hosting;

/// <summary>
/// Builds the operator's AI model catalog and named credentials from configuration (ADR 0024). Keys come only from
/// configuration or environment variables (ANTHROPIC_API_KEY, OPENAI_API_KEY, AZURE_OPENAI_API_KEY with
/// AZURE_OPENAI_ENDPOINT), never from workspace data. Operators mark a model as evaluated after the builder evaluation
/// suite passed against it (Esmbler__Assistant__EvaluatedModels__0=gpt-6.1-sol), or replace the catalog
/// (Esmbler__Assistant__Catalog__0__Id=...). A model paid by one customer's own key names the workspaces it is for
/// (Esmbler__Assistant__Catalog__0__AllowedTenants__0=acme); others never see it. Esmbler__Assistant__MaxDailyBudgetUsd
/// (default 100) caps what a workspace may set. Prices are estimates for budgets, per million tokens in US dollars.
/// </summary>
public static class AiCatalogFactory
{
    public static readonly AiModelOption[] Defaults =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", "anthropic", "claude-opus-5-5", "anthropic-default", "medium", 5m, 25m, true, "claude-opus-4-8"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "anthropic", "claude-sonnet-5-5", "anthropic-default", "medium", 3m, 15m, false),
        new("gpt-6.1-sol", "GPT-6.1 Sol", "openai", "gpt-6.1-sol", "openai-default", "medium", 2m, 10m, false),
        new("gpt-6-astra", "GPT-6 Astra", "openai", "gpt-6-astra", "openai-default", "medium", 10m, 50m, false),
    ];

    public static AiCatalog From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Esmbler:Assistant");
        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var credentials = new Dictionary<string, AiCredential>(StringComparer.Ordinal)
        {
            ["anthropic-default"] = new("anthropic", section["AnthropicApiKey"] is { Length: > 0 } a ? a : Env("ANTHROPIC_API_KEY"), null),
            ["openai-default"] = new("openai", section["OpenAIApiKey"] is { Length: > 0 } o ? o : Env("OPENAI_API_KEY"), null),
            ["azure-openai-default"] = new("azure-openai", Env("AZURE_OPENAI_API_KEY"), Env("AZURE_OPENAI_ENDPOINT") is { } e && Uri.TryCreate(e, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null),
        };
        foreach (var c in section.GetSection("Credentials").GetChildren())
        {
            var key = c["ApiKey"] is { Length: > 0 } k ? k : c["ApiKeyVariable"] is { Length: > 0 } variable ? Env(variable) : null;
            Uri? endpoint = c["Endpoint"] is { } ep && Uri.TryCreate(ep, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null;
            credentials[c.Key] = new AiCredential(c["Provider"] ?? "anthropic", key, endpoint);
        }

        var catalog = section.GetSection("Catalog").GetChildren().ToList();
        var models = catalog.Count == 0
            ? Defaults.ToList()
            : catalog.Select(m => new AiModelOption(
                m["Id"] ?? m.Key,
                m["Label"] ?? m["Id"] ?? m.Key,
                m["Provider"] ?? "anthropic",
                m["Model"] ?? m["Id"] ?? m.Key,
                m["Credential"] ?? $"{m["Provider"] ?? "anthropic"}-default",
                m["Effort"] ?? "medium",
                decimal.TryParse(m["InputPerMillion"], NumberStyles.Number, CultureInfo.InvariantCulture, out var i) ? i : 0,
                decimal.TryParse(m["OutputPerMillion"], NumberStyles.Number, CultureInfo.InvariantCulture, out var o) ? o : 0,
                bool.TryParse(m["Evaluated"], out var ev) && ev,
                m["FallbackModel"],
                m.GetSection("AllowedTenants").GetChildren().Select(c => c.Value).OfType<string>().ToList())).ToList();
        var evaluated = section.GetSection("EvaluatedModels").GetChildren().Select(c => c.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        models = [.. models.Select(m => evaluated.Contains(m.Id) ? m with { Evaluated = true } : m)];

        // The legacy single-model settings still name the default (ADR 0013).
        var legacy = section["Model"];
        var defaultModel = section["DefaultModel"] ?? (legacy is not null && models.Exists(m => m.Id == legacy) ? legacy : null);
        var ceiling = decimal.TryParse(section["MaxDailyBudgetUsd"], NumberStyles.Number, CultureInfo.InvariantCulture, out var max) && max >= 0 ? max : 100m;
        return new AiCatalog(models, credentials, defaultModel, ceiling);
    }
}
