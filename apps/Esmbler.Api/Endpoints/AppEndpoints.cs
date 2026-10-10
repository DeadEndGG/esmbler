using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
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

        // Service catalog (ADR 0015): active items the caller may request. Requests are created with POST /api/records.
        app.MapGet("/api/catalog", (ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) =>
            experience.CatalogAsync(RequestContextFactory.From(user, "api"), ct)).WithTags("Catalog").RequireAuthorization();

        var config = app.MapGroup("/api/config").WithTags("Configuration").RequireAuthorization();
        config.MapGet("/entities/{entity}", (string entity, ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) =>
            experience.EntityAsync(RequestContextFactory.From(user, "api"), entity, ct));
        config.MapGet("/views/{view}", (string view, ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) =>
            experience.ViewAsync(RequestContextFactory.From(user, "api"), view, ct));
        config.MapGet("/elements", (IElementRegistry registry) => registry.All.OrderBy(e => e.Id, StringComparer.Ordinal));

        // Pickers: names only, for any member of the workspace.
        var directory = app.MapGroup("/api/directory").WithTags("Directory").RequireAuthorization();
        directory.MapGet("/people", (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) => identity.DirectoryPeopleAsync(RequestContextFactory.From(user, "api"), ct));
        directory.MapGet("/groups", (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) => identity.DirectoryGroupsAsync(RequestContextFactory.From(user, "api"), ct));

        var ops = app.MapGroup("/api/admin/operations").WithTags("Operations").RequireAuthorization();
        ops.MapGet("/queues", (ClaimsPrincipal user, OperationsService operations, CancellationToken ct) => operations.StatsAsync(RequestContextFactory.From(user, "api"), ct));
        ops.MapGet("/dead-letters", (ClaimsPrincipal user, OperationsService operations, CancellationToken ct) => operations.DeadLettersAsync(RequestContextFactory.From(user, "api"), ct));
        ops.MapPost("/{queue}/{id:guid}/replay", async (string queue, Guid id, ClaimsPrincipal user, OperationsService operations, CancellationToken ct) =>
        {
            await operations.ReplayAsync(RequestContextFactory.From(user, "api"), queue, id, ct);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization();
        admin.MapGet("/members", (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) => identity.MembersAsync(RequestContextFactory.From(user, "api"), ct));
        admin.MapGet("/roles", (ClaimsPrincipal user, ExperienceService experience, CancellationToken ct) => experience.RolesAsync(RequestContextFactory.From(user, "api"), ct));
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
