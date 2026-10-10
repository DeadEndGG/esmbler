using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Sdk.Registry;

namespace Esmbler.Api.Endpoints;

public sealed record RoleChangeRequest(string Role, Guid? PrincipalId, Guid? GroupId);

public sealed record GroupMemberRequest(Guid PrincipalId);

public static class AppEndpoints
{
    public static void MapAppAndConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/app", (ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) =>
            experience.AppAsync(RequestContextFactory.From(user, "api"), ct)).WithTags("App").RequireAuthorization();

        var config = app.MapGroup("/api/config").WithTags("Configuration").RequireAuthorization();
        config.MapGet("/entities/{entity}", (string entity, ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) =>
            experience.EntityAsync(RequestContextFactory.From(user, "api"), entity, ct));
        config.MapGet("/views/{view}", (string view, ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) =>
            experience.ViewAsync(RequestContextFactory.From(user, "api"), view, ct));
        config.MapGet("/elements", (IElementRegistry registry) => registry.All.OrderBy(e => e.Id, StringComparer.Ordinal));

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization();
        admin.MapGet("/members", (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) => identity.MembersAsync(RequestContextFactory.From(user, "api"), ct));
        admin.MapGet("/groups", (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) => identity.GroupsAsync(RequestContextFactory.From(user, "api"), ct));
        admin.MapPost("/roles/assign", async (RoleChangeRequest body, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
        {
            await identity.AssignRoleAsync(RequestContextFactory.From(user, "api"), body.Role, body.PrincipalId, body.GroupId, ct);
            return Results.NoContent();
        });
        admin.MapPost("/roles/revoke", async (RoleChangeRequest body, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
        {
            await identity.RevokeRoleAsync(RequestContextFactory.From(user, "api"), body.Role, body.PrincipalId, body.GroupId, ct);
            return Results.NoContent();
        });
        admin.MapPost("/groups/{groupId:guid}/members", async (Guid groupId, GroupMemberRequest body, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
        {
            await identity.AddGroupMemberAsync(RequestContextFactory.From(user, "api"), groupId, body.PrincipalId, ct);
            return Results.NoContent();
        });
        admin.MapDelete("/groups/{groupId:guid}/members/{principalId:guid}", async (Guid groupId, Guid principalId, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
        {
            await identity.RemoveGroupMemberAsync(RequestContextFactory.From(user, "api"), groupId, principalId, ct);
            return Results.NoContent();
        });
        admin.MapPost("/agents/{agentId:guid}/disable", async (Guid agentId, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
        {
            await identity.SetAgentDisabledAsync(RequestContextFactory.From(user, "api"), agentId, true, ct);
            return Results.NoContent();
        });
    }
}
