using System.Text.Json.Nodes;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

public sealed class ArtifactManifest
{
    public required string ContentHash { get; init; }

    public required IReadOnlyList<LockedModule> Lockfile { get; init; }

    public required string OverlayHash { get; init; }

    public int OverlayOperations { get; init; }

    /// <summary>Renderer/SDK compatibility range the artifact was compiled for.</summary>
    public string Renderer { get; init; } = RendererVersion;

    public string Sdk { get; init; } = SdkVersion;

    public required IReadOnlyList<Diagnostic> Warnings { get; init; }

    public required PermissionImpact PermissionImpact { get; init; }

    /// <summary>Data migrations required before activation (declared by module releases).</summary>
    public IReadOnlyList<string> Migrations { get; init; } = [];

    public const string RendererVersion = "esmbler-renderer@^1.0";
    public const string SdkVersion = "esmbler-sdk@1.0.0";
}

public sealed class PermissionImpact
{
    public bool Changed { get; init; }

    public IReadOnlyList<string> Changes { get; init; } = [];
}

public sealed class CompileResult
{
    public required CompositionResult Composition { get; init; }

    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    public EffectiveConfig Config => Composition.Config;

    public ArtifactManifest? Manifest { get; init; }

    public bool Succeeded => Manifest is not null;

    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.IsError);
}

/// <summary>Compiles base modules + overlay into a validated, content-addressed artifact manifest.</summary>
public sealed class ConfigCompiler(ConfigValidator validator)
{
    public CompileResult Compile(IReadOnlyList<ModuleSpec> modules, IReadOnlyList<OverlayOp> overlay, EffectiveConfig? previous = null)
    {
        var composition = Composer.Compose(modules, overlay);
        var diagnostics = composition.Diagnostics.ToList();
        diagnostics.AddRange(validator.Validate(composition.Config));
        if (diagnostics.Any(d => d.IsError))
        {
            return new CompileResult { Composition = composition, Diagnostics = diagnostics };
        }

        var manifest = new ArtifactManifest
        {
            ContentHash = ContentHash(composition.Config),
            Lockfile = composition.Config.Modules,
            OverlayHash = Composer.Hash(SpecJson.Canonical(SpecJson.ToNode(overlay.ToList()))),
            OverlayOperations = overlay.Count,
            Warnings = diagnostics.Where(d => !d.IsError).ToList(),
            PermissionImpact = PermissionDiff(previous, composition.Config),
            Migrations = modules.SelectMany(m => m.Migrations.Select(x => $"{m.Id}@{m.Version}:{x.Id}")).ToList(),
        };
        return new CompileResult { Composition = composition, Diagnostics = diagnostics, Manifest = manifest };
    }

    /// <summary>Hash over the resolved configuration and lockfile (provenance excluded, it does not change behavior).</summary>
    public static string ContentHash(EffectiveConfig config)
    {
        var node = SpecJson.ToNode(config)!.AsObject();
        node.Remove("provenance");
        node.Remove("customized");
        return Composer.Hash(SpecJson.Canonical(node));
    }

    public static PermissionImpact PermissionDiff(EffectiveConfig? before, EffectiveConfig after)
    {
        if (before is null)
        {
            return new PermissionImpact();
        }

        var changes = new List<string>();
        var beforeSets = before.PermissionSets.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var ps in after.PermissionSets)
        {
            if (!beforeSets.TryGetValue(ps.Id, out var old))
            {
                changes.Add($"Added permission set {ps.Id}");
                continue;
            }

            if (!JsonNode.DeepEquals(SpecJson.ToNode(old), SpecJson.ToNode(ps)))
            {
                changes.Add($"Changed permission set {ps.Id}");
            }
        }

        changes.AddRange(beforeSets.Keys.Except(after.PermissionSets.Select(p => p.Id)).Select(id => $"Removed permission set {id}"));
        if (!JsonNode.DeepEquals(SpecJson.ToNode(before.Roles), SpecJson.ToNode(after.Roles)))
        {
            changes.Add("Roles changed");
        }

        return new PermissionImpact { Changed = changes.Count > 0, Changes = changes };
    }
}
