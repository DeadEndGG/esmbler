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
}
