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

    /// <summary>
    /// The IT ticketing starter (REQ-WS-001). Group keys match the release's assignment rules: everything goes to
    /// <c>service_desk</c> unless a rule routes it to <c>network</c>. Only the receiving team's name has no default.
    /// </summary>
    public IReadOnlyList<StarterSpec> Starters { get; } =
    [
        new StarterSpec
        {
            Id = "ticketing",
            Name = "IT ticketing",
            Description = "Tickets with priorities, categories and assignment, a ticket explorer, a dashboard of workload and SLA issues, service-level targets, and a service catalog with approvals.",
            Keywords = ["ticket", "ticketing", "help desk", "helpdesk", "service desk", "it support", "incident", "support"],
            Groups =
            [
                new StarterGroupSpec
                {
                    Key = "service_desk",
                    Roles = [Contracts.TicketsIds.AgentRole],
                    CreatorJoins = true,
                    Question = "Which team receives new tickets?",
                    Help = "Tickets that no other rule routes go to this team. You join it; add people later in People and roles.",
                    Example = "IT Support",
                },
                new StarterGroupSpec { Key = "network", Name = "Network", Roles = [Contracts.TicketsIds.AgentRole] },
            ],
        },
    ];
}
