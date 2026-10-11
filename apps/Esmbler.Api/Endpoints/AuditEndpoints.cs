using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Services;

namespace Esmbler.Api.Endpoints;

public sealed record RetentionRequest(int RetentionDays);

public sealed record HoldRequest(string Reason);

/// <summary>
/// The audit log for administrators (ADR 0022, REQ-AUD-001): search, streamed export (rate limited, audited), the
/// tamper-evidence chain, retention and legal holds. Every right is checked by <see cref="AuditService"/>.
/// </summary>
public static class AuditEndpoints
{
    public static void MapAudit(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/audit").WithTags("Audit").RequireAuthorization();
        g.MapGet("/", (DateTimeOffset? from, DateTimeOffset? to, Guid? actor, string? operation, string? targetType, string? targetId, string? outcome, long? before, int? limit, ClaimsPrincipal user, AuditService audit, CancellationToken ct) =>
            audit.SearchAsync(RequestContextFactory.From(user, "api"), new AuditQuery(from, to, actor, operation, targetType, targetId, outcome, before, limit ?? 50), ct));
        g.MapGet("/export", async (string format, DateTimeOffset? from, DateTimeOffset? to, Guid? actor, string? operation, string? targetType, string? targetId, string? outcome, ClaimsPrincipal user, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var ctx = RequestContextFactory.From(user, "api");
            var query = new AuditQuery(from, to, actor, operation, targetType, targetId, outcome, null);
            // Rights, range and the audit entry come first, so refusals are ordinary problem details; then it streams.
            var prepared = await audit.PrepareExportAsync(ctx, format, query, ct);
            http.Response.ContentType = format == "csv" ? "text/csv; charset=utf-8" : "application/x-ndjson";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.{format}\"";
            await audit.WriteExportAsync(ctx, format, prepared, http.Response.Body, ct);
        }).RequireRateLimiting(LoginThrottle.ExportPolicy);
        g.MapGet("/chain", (ClaimsPrincipal user, AuditService audit, CancellationToken ct) => audit.ChainAsync(RequestContextFactory.From(user, "api"), ct));
        g.MapPost("/chain/verify", (ClaimsPrincipal user, AuditService audit, CancellationToken ct) => audit.VerifyNowAsync(RequestContextFactory.From(user, "api"), ct))
            .RequireRateLimiting(LoginThrottle.ExportPolicy);
        g.MapGet("/policy", (ClaimsPrincipal user, AuditService audit, CancellationToken ct) => audit.PolicyAsync(RequestContextFactory.From(user, "api"), ct));
        g.MapPut("/policy", (RetentionRequest body, ClaimsPrincipal user, AuditService audit, CancellationToken ct) => audit.SetRetentionAsync(RequestContextFactory.From(user, "api"), body.RetentionDays, ct));
        g.MapPost("/holds", (HoldRequest body, ClaimsPrincipal user, AuditService audit, CancellationToken ct) => audit.PlaceHoldAsync(RequestContextFactory.From(user, "api"), body.Reason, ct));
        g.MapPost("/holds/{id:guid}/release", (Guid id, ClaimsPrincipal user, AuditService audit, CancellationToken ct) => audit.ReleaseHoldAsync(RequestContextFactory.From(user, "api"), id, ct));
    }
}
