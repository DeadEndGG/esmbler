namespace Esmbler.Kernel.Infrastructure.Assistant;

/// <summary>
/// A model the operator offers (ADR 0024): provider, model ID, the named credential that pays for it, prices for cost
/// estimates, and whether it passed the builder evaluation suite (only evaluated models can be chosen by workspaces).
/// </summary>
public sealed record AiModelOption(
    string Id,
    string Label,
    string Provider,
    string Model,
    string Credential,
    string Effort,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    bool Evaluated,
    string? FallbackModel = null,
    IReadOnlyList<string>? AllowedTenants = null)
{
    /// <summary>Whether a workspace may use this model: every workspace, unless the operator named the ones it is for (a tenant's own key).</summary>
    public bool AllowedFor(string tenantSlug) => AllowedTenants is not { Count: > 0 } list || list.Contains(tenantSlug, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A named credential: provider, key and (for Azure OpenAI) endpoint. Its text form never shows the key.</summary>
public sealed record AiCredential(string Provider, string? ApiKey, Uri? Endpoint)
{
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    public override string ToString() => $"AiCredential {{ Provider = {Provider}, Configured = {Configured}, Endpoint = {Endpoint} }}";
}

/// <summary>
/// The operator's model catalog and named credentials (from the platform's configuration, never from workspace data).
/// Keys stay inside this object: callers get them only to build a model client, never in views, logs or audit.
/// </summary>
public sealed class AiCatalog(IReadOnlyList<AiModelOption> models, IReadOnlyDictionary<string, AiCredential> credentials, string? defaultModel, decimal maxDailyBudgetUsd = 100m)
{
    /// <summary>The highest daily budget a workspace may set (operator ceiling, ASM).</summary>
    public decimal MaxDailyBudgetUsd { get; } = maxDailyBudgetUsd;

    public IReadOnlyList<AiModelOption> Models { get; } = models;

    public AiModelOption? Find(string? id) => id is null ? null : Models.FirstOrDefault(m => m.Id == id);

    /// <summary>Whether the model's credential is configured on this deployment.</summary>
    public bool Available(AiModelOption model) => credentials.TryGetValue(model.Credential, out var c) && c.Configured && c.Provider == ProviderFamily(model.Provider);

    /// <summary>
    /// Whether a workspace may choose or keep using a model: evaluated, its credential configured for the right provider,
    /// and offered to that workspace.
    /// </summary>
    public bool Usable(AiModelOption model, string tenantSlug) => model.Evaluated && Available(model) && model.AllowedFor(tenantSlug);

    /// <summary>The model used when a workspace chose none (or its choice is no longer usable): the configured default, else the first usable model.</summary>
    public AiModelOption? DefaultFor(string tenantSlug) =>
        Find(defaultModel) is { } chosen && Usable(chosen, tenantSlug) ? chosen : Models.FirstOrDefault(m => Usable(m, tenantSlug));

    /// <summary>The credential for building a model client (the host only); null unless it is configured for the model's provider.</summary>
    public AiCredential? CredentialFor(AiModelOption model) => credentials.TryGetValue(model.Credential, out var c) && c.Configured && c.Provider == ProviderFamily(model.Provider) ? c : null;

    /// <summary>
    /// Estimated cost of one request, in US dollars: uncached input at the input price, cache reads at a tenth of it,
    /// cache writes at 1.25 times it, output at the output price. A model the catalog does not know is charged at the
    /// highest catalog prices, so unknown usage never counts as free.
    /// </summary>
    public decimal Cost(AiModelOption? model, int freshInput, int output, int cacheRead, int cacheWrite)
    {
        var input = model?.InputPerMillion ?? Models.Select(m => m.InputPerMillion).DefaultIfEmpty(0).Max();
        var outputPrice = model?.OutputPerMillion ?? Models.Select(m => m.OutputPerMillion).DefaultIfEmpty(0).Max();
        return ((freshInput * input) + (cacheRead * input / 10m) + (cacheWrite * input * 1.25m) + (output * outputPrice)) / 1_000_000m;
    }

    private static string ProviderFamily(string provider) => provider == "azure-openai" ? "azure-openai" : provider;
}
