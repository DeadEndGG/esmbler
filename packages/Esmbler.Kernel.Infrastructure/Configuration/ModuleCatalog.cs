using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <summary>All module definitions shipped with this platform build (Registry pattern). The platform module is always included.</summary>
public sealed class ModuleCatalog
{
    private readonly Dictionary<string, IModuleDefinition> _modules;

    public ModuleCatalog(IEnumerable<IModuleDefinition> modules)
    {
        _modules = modules.Append(new PlatformModule()).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IModuleDefinition> Modules => _modules.Values;

    public IModuleDefinition Get(string id) =>
        _modules.GetValueOrDefault(id) ?? throw new KernelException("module_not_found", $"Module '{id}' is not available on this platform.", 404);

    public ModuleSpec Release(string moduleId, string version) =>
        Get(moduleId).Releases.FirstOrDefault(r => r.Version == version)
        ?? throw new KernelException("release_not_found", $"Module '{moduleId}' has no release {version}.", 404);

    public ModuleSpec Latest(string moduleId) => Get(moduleId).Releases[^1];

    /// <summary>
    /// Data migrations an upgrade from <paramref name="fromVersion"/> to <paramref name="toVersion"/> must run: those of
    /// every release after the installed one, up to and including the target, in release order. Skipping a version never
    /// skips its migrations.
    /// </summary>
    public IReadOnlyList<MigrationSpec> MigrationsBetween(string moduleId, string fromVersion, string toVersion)
    {
        var from = SemVer.Parse(fromVersion);
        var to = SemVer.Parse(toVersion);
        return Get(moduleId).Releases
            .Where(r => SemVer.Parse(r.Version) > from && SemVer.Parse(r.Version) <= to)
            .OrderBy(r => SemVer.Parse(r.Version))
            .SelectMany(r => r.Migrations)
            .ToList();
    }
}
