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
    string? FallbackModel = null);

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
public sealed class AiCatalog(IReadOnlyList<AiModelOption> models, IReadOnlyDictionary<string, AiCredential> credentials, string? defaultModel)
{
    public IReadOnlyList<AiModelOption> Models { get; } = models;

    public AiModelOption? Find(string? id) => id is null ? null : Models.FirstOrDefault(m => m.Id == id);

    /// <summary>Whether the model's credential is configured on this deployment.</summary>
    public bool Available(AiModelOption model) => credentials.TryGetValue(model.Credential, out var c) && c.Configured && c.Provider == ProviderFamily(model.Provider);

    /// <summary>The model used when a workspace chose none: the configured default if available, else the first available evaluated model.</summary>
    public AiModelOption? Default =>
        Find(defaultModel) is { } chosen && Available(chosen) ? chosen : Models.FirstOrDefault(m => m.Evaluated && Available(m));

    /// <summary>The credential for building a model client (the host only).</summary>
    public AiCredential? CredentialFor(AiModelOption model) => credentials.TryGetValue(model.Credential, out var c) && c.Configured ? c : null;

    /// <summary>Estimated cost of one request, in US dollars (cached input at a tenth of the input price).</summary>
    public static decimal Cost(AiModelOption? model, int input, int output, int cached)
    {
        if (model is null)
        {
            return 0;
        }

        var fresh = Math.Max(0, input - cached);
        return ((fresh * model.InputPerMillion) + (cached * model.InputPerMillion / 10m) + (output * model.OutputPerMillion)) / 1_000_000m;
    }

    private static string ProviderFamily(string provider) => provider == "azure-openai" ? "azure-openai" : provider;
}
