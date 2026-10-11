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
        // Persona preview (REQ-WS-001 step 5): what each of the starter's roles would see; computed, nothing created.
        app.MapGet("/api/starters/{id}/personas", (string id, WorkspaceService workspaces) => workspaces.Personas(id)).WithTags("Workspaces").RequireAuthorization().RequireRateLimiting(LoginThrottle.QueryPolicy);

        app.MapPost("/api/workspaces", async (CreateWorkspaceRequest body, ClaimsPrincipal user, WorkspaceService workspaces, IdentityService identity, CancellationToken ct) =>
        {
            if (user.FindFirst(ClaimNames.Agent) is not null)
            {
                throw KernelException.Forbidden("AI agents cannot create workspaces.");
            }

            var principal = RequestContextFactory.PrincipalId(user);
            var created = await workspaces.CreateAsync(principal, await identity.DisplayNameAsync(principal, ct), body, ct);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        }).WithTags("Workspaces").RequireAuthorization().RequireRateLimiting(LoginThrottle.WorkspacePolicy);
    }
}
