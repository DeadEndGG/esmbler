namespace Esmbler.Kernel.Tenancy;

/// <summary>
/// Authenticated execution context. Tenant and environment come only from the authenticated session
/// (REQ-TEN-003): never from record IDs or user-controlled arguments.
/// </summary>
public sealed record RequestContext
{
    public required Guid TenantId { get; init; }

    public required Guid EnvironmentId { get; init; }

    /// <summary>The human (or service) principal on whose behalf the call runs.</summary>
    public required Guid PrincipalId { get; init; }

    /// <summary>Set when an AI agent acts for <see cref="PrincipalId"/> (REQ-AUTHZ-007).</summary>
    public Guid? AgentId { get; init; }

    /// <summary>Delegated scopes of the agent token; null for direct human sessions (all of the user's permissions).</summary>
    public IReadOnlySet<string>? Scopes { get; init; }

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    /// <summary>ui, api, mcp, worker, system.</summary>
    public string Channel { get; init; } = "api";

    public string ActorRef => AgentId is { } a ? $"agent:{a}" : $"user:{PrincipalId}";

    public bool HasScope(string scope) => Scopes is null || Scopes.Contains(scope);
}

/// <summary>Agent scopes (delegated authority). A tool requires its scope AND the user's own permission.</summary>
public static class AgentScopes
{
    public const string ConfigRead = "config:read";
    public const string ConfigEdit = "config:edit";
    public const string PublishRequest = "config:publish:request";
    public const string RecordsRead = "records:read";
    public const string RecordsWrite = "records:write";
    public const string AccessExplain = "access:explain";

    public static IReadOnlyList<string> All { get; } = [ConfigRead, ConfigEdit, PublishRequest, RecordsRead, RecordsWrite, AccessExplain];

    /// <summary>
    /// Default delegation for the in-app builder chat. No record access by default (threat model T-23): the model works
    /// on configuration and synthetic sample data; reading real records is an explicit opt-in (records:read).
    /// </summary>
    public static IReadOnlyList<string> Builder { get; } = [ConfigRead, ConfigEdit, PublishRequest, AccessExplain];
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
