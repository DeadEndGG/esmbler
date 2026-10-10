using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Specs;

namespace Esmbler.Modules.Tickets;

/// <summary>
/// Default Tickets module (reference implementation). Entirely declarative: entities, rules, workflow,
/// pages, roles, and migrations live in Releases/&lt;version&gt;/module.json and use only public SDK contracts.
/// </summary>
public sealed class TicketsModule : IModuleDefinition
{
    private static readonly IReadOnlyList<ModuleSpec> LoadedReleases = ModuleReleaseLoader.FromEmbedded(typeof(TicketsModule).Assembly, "tickets/");

    public string Id => Contracts.TicketsIds.Module;

    public string Name => "Tickets";

    public IReadOnlyList<ModuleSpec> Releases => LoadedReleases;
}
