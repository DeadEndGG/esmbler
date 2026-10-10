using System.Text.RegularExpressions;
using Esmbler.Sdk.Notifications;

namespace Esmbler.Kernel.Infrastructure.Notifications;

/// <summary>Resolves secret references for a workspace (ADR 0021): channel settings hold names, never values.</summary>
public interface ISecretStore
{
    ISecretAccessor For(string tenantSlug);
}

/// <summary>
/// Development and single-host adapter: <c>ESMBLER_SECRET__&lt;workspace slug&gt;__&lt;NAME&gt;</c> environment variables. Names
/// are namespaced by workspace by construction, so a workspace can never read another's secrets or any other variable
/// (connection strings, signing keys). A vault adapter replaces it in production (human action).
/// </summary>
public sealed partial class EnvironmentSecretStore : ISecretStore
{
    public ISecretAccessor For(string tenantSlug) => new Accessor(tenantSlug);

    public static bool IsValidName(string name) => Name().IsMatch(name);

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,40}$")]
    private static partial Regex Name();

    private sealed class Accessor(string slug) : ISecretAccessor
    {
        public string? Get(string name) =>
            IsValidName(name) ? Environment.GetEnvironmentVariable($"ESMBLER_SECRET__{slug.Replace('-', '_').ToUpperInvariant()}__{name}") : null;
    }
}
