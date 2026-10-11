using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Alerts;

namespace Esmbler.Api.Endpoints;

public sealed record AlertActionRequest(int? Version, string? Note, Guid? Assignee);

/// <summary>
/// Alerts (ADR 0022): the alerts a person may see, acknowledging, resolving and assigning them, and maintenance windows
/// for people who manage alerts. The server decides visibility and rights on every call.
/// </summary>
public static class AlertEndpoints
{
    public static void MapAlerts(this IEndpointRouteBuilder app)
    {
        var alerts = app.MapGroup("/api/alerts").WithTags("Alerts").RequireAuthorization();
        alerts.MapGet("/", (string? status, bool? mine, int? limit, int? offset, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.ListAsync(RequestContextFactory.From(user, "api"), status, mine ?? false, limit ?? 50, offset ?? 0, ct));
        alerts.MapGet("/count", async (ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            new { open = await service.CountAsync(RequestContextFactory.From(user, "api"), ct) });
        alerts.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.GetAsync(RequestContextFactory.From(user, "api"), id, ct));
        alerts.MapPost("/{id:guid}/acknowledge", (Guid id, AlertActionRequest? body, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.AcknowledgeAsync(RequestContextFactory.From(user, "api"), id, body?.Version, ct));
        alerts.MapPost("/{id:guid}/resolve", (Guid id, AlertActionRequest? body, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.ResolveAsync(RequestContextFactory.From(user, "api"), id, body?.Note, body?.Version, ct));
        alerts.MapPost("/{id:guid}/assign", (Guid id, AlertActionRequest body, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.AssignAsync(RequestContextFactory.From(user, "api"), id, body.Assignee ?? throw new Kernel.KernelException("invalid_assignee", "Choose a person.", 400, "assignee"), body.Version, ct));

        var windows = app.MapGroup("/api/admin/alerts/suppressions").WithTags("Alerts").RequireAuthorization();
        windows.MapGet("/", (ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.SuppressionsAsync(RequestContextFactory.From(user, "api"), ct));
        windows.MapPost("/", (SuppressionRequest body, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
            service.SuppressAsync(RequestContextFactory.From(user, "api"), body, ct));
        windows.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, AlertService service, CancellationToken ct) =>
        {
            await service.CancelSuppressionAsync(RequestContextFactory.From(user, "api"), id, ct);
            return Results.NoContent();
        });
    }
}
