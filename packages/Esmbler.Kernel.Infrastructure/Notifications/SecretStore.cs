using System.Text.RegularExpressions;
using Esmbler.Sdk.Notifications;

namespace Esmbler.Kernel.Infrastructure.Notifications;

/// <summary>Resolves secret references for a workspace (ADR 0021): channel settings hold names, never values.</summary>
public interface ISecretStore
{
    /// <summary>The secrets of one workspace environment; a sandbox never reads production's.</summary>
    ISecretAccessor For(string tenantSlug, string environmentKey);
}

/// <summary>
/// Development and single-host adapter: <c>ESMBLER_SECRET__&lt;WORKSPACE&gt;__&lt;ENVIRONMENT&gt;__&lt;NAME&gt;</c> environment
/// variables (slug and environment key upper-cased, hyphens as underscores). Names are namespaced by workspace and
/// environment by construction, and secret names cannot contain "__", so a workspace or sandbox can never read another's
/// secrets or any other variable (connection strings, signing keys). A vault adapter replaces it in production (human action).
/// </summary>
public sealed partial class EnvironmentSecretStore : ISecretStore
{
    public ISecretAccessor For(string tenantSlug, string environmentKey) => new Accessor($"{Part(tenantSlug)}__{Part(environmentKey)}");

    private static string Part(string s) => s.Replace('-', '_').ToUpperInvariant();

    public static bool IsValidName(string name) => Name().IsMatch(name);

    [GeneratedRegex("^(?!.*__)[A-Z][A-Z0-9_]{1,40}(?<!_)$")]
    private static partial Regex Name();

    private sealed class Accessor(string scope) : ISecretAccessor
    {
        public string? Get(string name) =>
            IsValidName(name) ? Environment.GetEnvironmentVariable($"ESMBLER_SECRET__{scope}__{name}") : null;
    }
}
