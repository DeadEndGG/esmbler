using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Approvals;

namespace Esmbler.Api.Endpoints;

public sealed record DecisionRequest(string Decision, string? Comment);

public sealed record DelegationRequest(Guid Delegate, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Approvals (ADR 0016): the caller's inbox, decisions, a record's approvals panel, and out-of-office delegation.</summary>
public static class ApprovalEndpoints
{
    public static void MapApprovals(this IEndpointRouteBuilder app)
    {
        var approvals = app.MapGroup("/api/approvals").WithTags("Approvals").RequireAuthorization();
        approvals.MapGet("/", (ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
            service.InboxAsync(RequestContextFactory.From(user, "api"), ct));
        approvals.MapGet("/count", async (ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
            new { pending = await service.PendingCountAsync(RequestContextFactory.From(user, "api"), ct) });
        approvals.MapPost("/steps/{stepId:guid}/decision", (Guid stepId, DecisionRequest body, ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
            service.DecideAsync(RequestContextFactory.From(user, "api"), stepId, body.Decision, body.Comment, ct));
        // ?all=true: every current delegation in the workspace, for permission administrators (governance review).
        approvals.MapGet("/delegations", (bool? all, ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
            service.DelegationsAsync(RequestContextFactory.From(user, "api"), ct, all ?? false));
        approvals.MapPost("/delegations", (DelegationRequest body, ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
            service.DelegateAsync(RequestContextFactory.From(user, "api"), body.Delegate, body.StartsAt, body.EndsAt, ct));
        approvals.MapDelete("/delegations/{id:guid}", async (Guid id, ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
        {
            await service.RevokeDelegationAsync(RequestContextFactory.From(user, "api"), id, ct);
            return Results.NoContent();
        });

        app.MapGet("/api/records/{entity}/{id:guid}/approvals", (string entity, Guid id, ClaimsPrincipal user, ApprovalService service, CancellationToken ct) =>
            service.ForRecordAsync(RequestContextFactory.From(user, "api"), entity, id, ct)).WithTags("Approvals").RequireAuthorization();
    }
}
