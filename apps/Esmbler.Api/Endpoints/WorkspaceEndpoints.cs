using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Services;

namespace Esmbler.Api.Endpoints;

/// <summary>Guided workspace creation (REQ-WS-001): starters and creating a workspace from one.</summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaces(this IEndpointRouteBuilder app)
    {
        // Needs only a signed-in person, not a selected workspace: this is how a new admin gets their first one.
        app.MapGet("/api/starters", (WorkspaceService workspaces) =>
            Results.Ok(new { enabled = workspaces.Policy.Enabled, starters = workspaces.Starters() })).WithTags("Workspaces").RequireAuthorization();

        app.MapPost("/api/workspaces", async (CreateWorkspaceRequest body, ClaimsPrincipal user, WorkspaceService workspaces, IdentityService identity, CancellationToken ct) =>
        {
            if (user.FindFirst(ClaimNames.Agent) is not null)
            {
                throw KernelException.Forbidden("AI agents cannot create workspaces.");
            }

            var principal = RequestContextFactory.PrincipalId(user);
            var created = await workspaces.CreateAsync(principal, await identity.DisplayNameAsync(principal, ct), body, ct);
            return Results.Created($"/api/workspaces/{created.Slug}", created);
        }).WithTags("Workspaces").RequireAuthorization().RequireRateLimiting(LoginThrottle.QueryPolicy);
    }
}
