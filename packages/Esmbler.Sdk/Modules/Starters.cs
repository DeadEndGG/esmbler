namespace Esmbler.Sdk.Modules;

/// <summary>
/// A complete workspace a module offers to start from (REQ-WS-001), for example "IT ticketing". Creating a workspace
/// installs the module's latest release, which already connects forms, explorers, dashboards, navigation and
/// permissions; the starter only says which teams to create and which questions have no sensible default.
/// </summary>
public sealed class StarterSpec
{
    /// <summary>Stable ID, unique across modules, e.g. <c>ticketing</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>Words that suggest this starter when someone describes what they want (lowercase).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Teams created with the workspace. Group keys must match the keys the module's assignment rules use.</summary>
    public IReadOnlyList<StarterGroupSpec> Groups { get; init; } = [];
}

/// <summary>A team created with the workspace; its name is asked when it has no sensible default.</summary>
public sealed class StarterGroupSpec
{
    /// <summary>Group key (snake_case), e.g. <c>service_desk</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Name used when no question is asked.</summary>
    public string? Name { get; init; }

    /// <summary>Roles the group's members hold, e.g. the module's agent role.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Whether the person creating the workspace joins this team.</summary>
    public bool CreatorJoins { get; init; }

    /// <summary>When set, the name is a question without a default, e.g. "Which team receives new tickets?".</summary>
    public string? Question { get; init; }

    public string? Help { get; init; }

    /// <summary>An example answer shown in the empty input (never used as a value).</summary>
    public string? Example { get; init; }
}
