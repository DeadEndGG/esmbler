using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>Resolved configuration for one tenant environment: base modules → packages → tenant overlay.</summary>
public sealed class EffectiveConfig : SpecCatalog
{
    /// <summary>Exact module versions composed (the lockfile).</summary>
    public List<LockedModule> Modules { get; set; } = [];

    /// <summary>Which layer produced each element: element key → "core.tickets@1.0.0" or "tenant".</summary>
    public Dictionary<string, string> Provenance { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Element keys the tenant added or changed (drives "modified" badges and "what have we customized?").</summary>
    public List<string> Customized { get; set; } = [];
}

public sealed record LockedModule(string Id, string Version, string SpecHash);
