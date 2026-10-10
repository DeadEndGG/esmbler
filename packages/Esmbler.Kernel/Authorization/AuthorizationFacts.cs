namespace Esmbler.Kernel.Authorization;

/// <summary>
/// Relationship facts about a principal, read from the relationship graph (OpenFGA, ADR 0004):
/// roles (direct or via nested groups) and transitive group memberships.
/// </summary>
public sealed record AuthorizationFacts
{
    public required Guid PrincipalId { get; init; }

    /// <summary>Role ID → how it was obtained ("direct" or "group:&lt;group key&gt;").</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Roles { get; init; }

    /// <summary>Transitive group memberships (group IDs).</summary>
    public required IReadOnlySet<Guid> Groups { get; init; }

    public static AuthorizationFacts None(Guid principal) => new()
    {
        PrincipalId = principal,
        Roles = new Dictionary<string, IReadOnlyList<string>>(),
        Groups = new HashSet<Guid>(),
    };
}

/// <summary>
/// Relationship graph port (OpenFGA adapter; in-memory fake for tests). One graph per tenant because identity is
/// tenant-wide. Implementations must fail closed by throwing <see cref="AuthorizationUnavailableException"/>.
/// </summary>
public interface IRelationshipGraph
{
    /// <summary>Roles (role IDs) and transitive groups of a principal, read with strong consistency.</summary>
    Task<(IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups)> GetFactsAsync(Guid tenantId, Guid principalId, CancellationToken cancellationToken);

    Task WriteAsync(Guid tenantId, IReadOnlyList<RelationshipTuple> writes, IReadOnlyList<RelationshipTuple> deletes, CancellationToken cancellationToken);

    /// <summary>Creates the tenant's graph (store + model) if needed.</summary>
    Task EnsureTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Checks a record relation using contextual tuples derived from the record (no stored record tuples).</summary>
    Task<bool> IsRelatedAsync(Guid tenantId, Guid principalId, Guid recordId, IReadOnlyList<RelationshipTuple> recordTuples, CancellationToken cancellationToken);
}

/// <summary>Where a tenant's relationship store lives (persisted on the tenant row).</summary>
public interface IAuthzStoreRegistry
{
    Task<(string StoreId, string ModelId)?> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    Task SaveAsync(Guid tenantId, string storeId, string modelId, CancellationToken cancellationToken);
}

/// <summary>A relationship fact, e.g. (user:U, member, group:G) or (group:G#member, assignee, role:R).</summary>
public sealed record RelationshipTuple(string User, string Relation, string Object)
{
    public static RelationshipTuple GroupMember(Guid user, Guid group) => new($"user:{user}", "member", $"group:{group}");

    public static RelationshipTuple NestedGroup(Guid child, Guid parent) => new($"group:{child}#member", "member", $"group:{parent}");

    public static RelationshipTuple UserRole(Guid user, string role) => new($"user:{user}", "assignee", $"role:{role}");

    public static RelationshipTuple GroupRole(Guid group, string role) => new($"group:{group}#member", "assignee", $"role:{role}");

    public static RelationshipTuple AgentActsFor(Guid agent, Guid user) => new($"user:{user}", "acts_for", $"agent:{agent}");
}

/// <summary>Raised when authorization facts cannot be established. Callers deny (fail closed, REQ-AUTHZ-006).</summary>
public sealed class AuthorizationUnavailableException(string message, Exception? inner = null)
    : KernelException("authorization_unavailable", message, 503)
{
    public Exception? Cause { get; } = inner;
}
