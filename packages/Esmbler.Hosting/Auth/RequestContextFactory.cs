using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Esmbler.Kernel;
using Esmbler.Kernel.Tenancy;

namespace Esmbler.Hosting.Auth;

/// <summary>Builds the kernel RequestContext from validated token claims only (REQ-TEN-003, REQ-AI-006).</summary>
public static class RequestContextFactory
{
    public static Guid PrincipalId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new KernelException("unauthenticated", "Sign in to continue.", 401);

    public static RequestContext From(ClaimsPrincipal user, string channel, string? correlationId = null)
    {
        var principal = PrincipalId(user);
        if (!Guid.TryParse(user.FindFirstValue(ClaimNames.Tenant), out var tenant) || !Guid.TryParse(user.FindFirstValue(ClaimNames.Environment), out var env))
        {
            throw new KernelException("no_tenant_selected", "Choose a workspace first.", 401, suggestion: "Call POST /api/auth/switch with a tenant.");
        }

        Guid? agent = Guid.TryParse(user.FindFirstValue(ClaimNames.Agent), out var a) ? a : null;
        IReadOnlySet<string>? scopes = agent is null ? null : (user.FindFirstValue(ClaimNames.Scope) ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return new RequestContext
        {
            TenantId = tenant,
            EnvironmentId = env,
            PrincipalId = principal,
            AgentId = agent,
            Scopes = scopes,
            Channel = channel,
            CorrelationId = Guid.TryParse(correlationId, out var c) ? c : Guid.NewGuid(),
        };
    }
}
