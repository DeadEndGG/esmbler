using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Metrics;
using Esmbler.Kernel.Infrastructure.Records;
using Microsoft.AspNetCore.Mvc;

namespace Esmbler.Api.Endpoints;

/// <summary>Metrics and dashboards (ADR 0019): figures evaluated for the caller, and explorer group counts.</summary>
public static class MetricEndpoints
{
    public static void MapMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboards/{view}/data", (string view, string? tz, ClaimsPrincipal user, MetricService metrics, CancellationToken ct) =>
            metrics.DashboardAsync(RequestContextFactory.From(user, "api"), view, tz, ct)).WithTags("Metrics").RequireAuthorization().RequireRateLimiting(LoginThrottle.QueryPolicy);

        app.MapGet("/api/metrics/{id}", (string id, string? tz, ClaimsPrincipal user, MetricService metrics, CancellationToken ct) =>
            metrics.EvaluateAsync(RequestContextFactory.From(user, "api"), id, tz, ct)).WithTags("Metrics").RequireAuthorization().RequireRateLimiting(LoginThrottle.QueryPolicy);

        // A figure as CSV for this viewer (REQ-OPS-004); restricted figures are not found; audited.
        app.MapGet("/api/metrics/{id}/export", async (string id, string? tz, ClaimsPrincipal user, MetricService metrics, CancellationToken ct) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(await metrics.ExportCsvAsync(RequestContextFactory.From(user, "api"), id, tz, ct)), "text/csv; charset=utf-8", $"{RecordEndpoints.FileName(id)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.csv"))
            .WithTags("Metrics").RequireAuthorization().RequireRateLimiting(LoginThrottle.RecordExportPolicy);

        // Group headers for the explorer: same filters as the list, counted per value of a groupable field.
        app.MapGet("/api/records/{entity}/groups", (string entity, string field, [FromQuery(Name = "filter")] string[]? filter, [FromQuery(Name = "sla")] string[]? sla, string? search, ClaimsPrincipal user, MetricService metrics, CancellationToken ct) =>
            metrics.GroupsAsync(RequestContextFactory.From(user, "api"), new RecordQuery
            {
                Entity = entity,
                Filters = RecordEndpoints.LimitFilters(filter),
                Sla = [.. (sla ?? []).Take(5).Select(SlaFilter.Parse)],
                Search = search,
            }, field, ct)).WithTags("Metrics").RequireAuthorization().RequireRateLimiting(LoginThrottle.QueryPolicy);
    }
}
