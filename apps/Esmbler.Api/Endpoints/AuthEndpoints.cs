using System.Security.Claims;
using Esmbler.Hosting;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Tenancy;

namespace Esmbler.Api.Endpoints;

public sealed record LoginRequest(string Email, string Password);

public sealed record SwitchRequest(string Tenant, string? Environment);

public sealed record AgentTokenRequest(IReadOnlyList<string>? Scopes);

/// <summary>Development/pilot authentication (ADR 0011). SSO arrives in Gate 2.</summary>
public static class AuthEndpoints
{
    public const string RefreshCookie = "esmbler_refresh";
    public const string CsrfHeader = "X-Esmbler-Csrf";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest body, IdentityService identity, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            var user = await identity.VerifyPasswordAsync(body.Email ?? string.Empty, body.Password ?? string.Empty, ct)
                ?? throw new KernelException("invalid_credentials", "That email and password don't match.", 401);
            var workspaces = await identity.TenantsForAsync(user.Id, ct);
            var refresh = await tokens.CreateRefreshAsync(user.Id, null, null, ct);
            SetRefreshCookie(http, refresh);
            var access = tokens.IssueApi(user.Id, user.Name, null, null);
            return Results.Ok(new { access.AccessToken, access.ExpiresAt, user = new { user.Id, name = user.Name }, workspaces });
        }).AllowAnonymous();

        g.MapPost("/switch", async (SwitchRequest body, ClaimsPrincipal principal, IdentityService identity, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            RejectAgent(principal);
            var id = RequestContextFactory.PrincipalId(principal);
            var (tenant, env) = await identity.ResolveSwitchAsync(id, body.Tenant, body.Environment, ct);
            var name = principal.FindFirstValue("name") ?? string.Empty;
            var refresh = await tokens.CreateRefreshAsync(id, tenant.TenantId, env.EnvironmentId, ct);
            SetRefreshCookie(http, refresh);
            var access = tokens.IssueApi(id, name, tenant.TenantId, env.EnvironmentId);
            return Results.Ok(new { access.AccessToken, access.ExpiresAt, tenant = new { tenant.TenantId, tenant.Slug, tenant.Name }, environment = env });
        }).RequireAuthorization();

        g.MapPost("/refresh", async (IdentityService identity, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            RequireCsrfHeader(http);
            var raw = http.Request.Cookies[RefreshCookie] ?? throw new KernelException("invalid_refresh", "Your session expired. Sign in again.", 401);
            var (principalId, tenantId, envId, next) = await tokens.RotateRefreshAsync(raw, ct);
            var workspaces = await identity.TenantsForAsync(principalId, ct);
            if (tenantId is { } t && !workspaces.Any(w => w.TenantId == t))
            {
                throw new KernelException("invalid_refresh", "You no longer have access to that workspace. Sign in again.", 401);
            }

            SetRefreshCookie(http, next);
            var name = await identity.DisplayNameAsync(principalId, ct);
            var access = tokens.IssueApi(principalId, name, tenantId, envId);
            return Results.Ok(new { access.AccessToken, access.ExpiresAt, workspaces });
        }).AllowAnonymous();

        g.MapPost("/logout", async (TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            RequireCsrfHeader(http);
            if (http.Request.Cookies[RefreshCookie] is { } raw)
            {
                await tokens.RevokeRefreshAsync(raw, ct);
            }

            http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth" });
            return Results.NoContent();
        }).AllowAnonymous();

        // Mints a short-lived token for the user's AI agent, audience esmbler-mcp: the MCP server accepts nothing else.
        g.MapPost("/agent-token", async (AgentTokenRequest? body, ClaimsPrincipal principal, IdentityService identity, TokenService tokens, CancellationToken ct) =>
        {
            RejectAgent(principal);
            var ctx = RequestContextFactory.From(principal, "api");
            var requested = body?.Scopes is { Count: > 0 } s ? s : AgentScopes.Builder;
            var unknown = requested.Except(AgentScopes.All).ToList();
            if (unknown.Count > 0)
            {
                throw new KernelException("invalid_scope", $"Unknown scopes: {string.Join(", ", unknown)}.", 400, suggestion: string.Join(" ", AgentScopes.All));
            }

            var agent = await identity.EnsureAgentAsync(ctx.PrincipalId, ctx.TenantId, ct);
            var token = tokens.IssueAgent(ctx.PrincipalId, principal.FindFirstValue("name") ?? string.Empty, ctx.TenantId, ctx.EnvironmentId, agent, requested);
            return Results.Ok(new { token.AccessToken, token.ExpiresAt, agentId = agent, scopes = requested });
        }).RequireAuthorization();
    }

    private static void RejectAgent(ClaimsPrincipal principal)
    {
        if (principal.FindFirst(ClaimNames.Agent) is not null)
        {
            throw KernelException.Forbidden("AI agents cannot switch workspaces or mint tokens.");
        }
    }

    private static void RequireCsrfHeader(HttpContext http)
    {
        // A custom header cannot be sent cross-site without a CORS preflight, which this API never grants.
        if (!http.Request.Headers.ContainsKey(CsrfHeader))
        {
            throw new KernelException("csrf", "Missing anti-forgery header.", 400);
        }
    }

    private static void SetRefreshCookie(HttpContext http, string value) =>
        http.Response.Cookies.Append(RefreshCookie, value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromHours(8),
        });
}
