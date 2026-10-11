using System.Security.Claims;
using System.Text.Json.Nodes;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Records;
using Microsoft.AspNetCore.Mvc;

namespace Esmbler.Api.Endpoints;

public sealed record CreateRecordRequest(JsonObject Data, int? ConfigRevision);

public sealed record UpdateRecordRequest(int Revision, JsonObject Changes, int? ConfigRevision);

public sealed record TransitionRequest(int Revision, JsonObject? Changes, int? ConfigRevision);

public sealed record ActivityRequest(string Kind, string Body);

/// <summary>Record API (REQ-REC-*). Tenant and environment come from the token; IDs in the path never select a tenant.</summary>
public static class RecordEndpoints
{
    /// <summary>Most filter expressions one list, group or figure request may carry (bounded work per request).</summary>
    public const int MaxFilters = 20;

    public static IReadOnlyList<string> LimitFilters(string[]? filters) =>
        filters is null ? [] : filters.Length <= MaxFilters ? filters
            : throw new KernelException("too_many_filters", $"A list can combine at most {MaxFilters} filters.", 400, "filter", "Combine conditions with && in fewer filters.");

    public static void MapRecords(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/records/{entity}").WithTags("Records").RequireAuthorization();

        g.MapGet("/", (string entity, [FromQuery(Name = "filter")] string[]? filter, [FromQuery(Name = "sla")] string[]? sla, string? search, string? sort, string? dir, string? group, int? offset, int? size, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.QueryAsync(Ctx(user), new RecordQuery
            {
                Entity = entity,
                Filters = LimitFilters(filter),
                Sla = [.. (sla ?? []).Take(5).Select(SlaFilter.Parse)],
                Search = search,
                Sort = sort is null ? null : new SortSpec(sort, dir is "desc"),
                GroupBy = group,
                Offset = offset ?? 0,
                PageSize = size ?? 50,
            }, ct));

        // CSV of the explorer's result for this viewer (REQ-OPS-004): audited first, then streamed.
        g.MapGet("/export", async (string entity, [FromQuery(Name = "filter")] string[]? filter, [FromQuery(Name = "sla")] string[]? sla, string? search, string? sort, string? dir, ClaimsPrincipal user, RecordService records, HttpContext http, CancellationToken ct) =>
        {
            var ctx = Ctx(user);
            var query = new RecordQuery
            {
                Entity = entity,
                Filters = LimitFilters(filter),
                Sla = [.. (sla ?? []).Take(5).Select(SlaFilter.Parse)],
                Search = search,
                Sort = sort is null ? null : new SortSpec(sort, dir is "desc"),
            };
            await records.PrepareExportAsync(ctx, query, ct);
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{FileName(entity)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.csv\"";
            await records.WriteExportAsync(ctx, query, http.Response.Body, ct);
        }).RequireRateLimiting(LoginThrottle.RecordExportPolicy);

        g.MapPost("/", async (string entity, CreateRecordRequest body, [FromHeader(Name = "Idempotency-Key")] string? key, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
        {
            var created = await records.CreateAsync(Ctx(user), entity, body.Data ?? [], body.ConfigRevision, key, ct);
            return Results.Created($"/api/records/{entity}/{created.Id}", created);
        });

        g.MapGet("/{id:guid}", (string entity, Guid id, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.GetAsync(Ctx(user), entity, id, ct));

        g.MapPatch("/{id:guid}", (string entity, Guid id, UpdateRecordRequest body, [FromHeader(Name = "Idempotency-Key")] string? key, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.UpdateAsync(Ctx(user), entity, id, body.Revision, body.Changes ?? [], body.ConfigRevision, key, ct));

        g.MapPost("/{id:guid}/transitions/{transition}", (string entity, Guid id, string transition, TransitionRequest body, [FromHeader(Name = "Idempotency-Key")] string? key, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.TransitionAsync(Ctx(user), entity, id, body.Revision, transition, body.Changes ?? [], body.ConfigRevision, key, ct));

        g.MapDelete("/{id:guid}", async (string entity, Guid id, int revision, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
        {
            await records.DeleteAsync(Ctx(user), entity, id, revision, ct);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/restore", (string entity, Guid id, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.RestoreAsync(Ctx(user), entity, id, ct));

        g.MapGet("/{id:guid}/activity", (string entity, Guid id, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.ActivityAsync(Ctx(user), entity, id, ct));

        g.MapPost("/{id:guid}/activity", async (string entity, Guid id, ActivityRequest body, ClaimsPrincipal user, RecordService records, IRichTextSanitizer sanitizer, CancellationToken ct) =>
            Results.Created($"/api/records/{entity}/{id}/activity", await records.AddActivityAsync(Ctx(user), entity, id, body.Kind, body.Body ?? string.Empty, sanitizer, ct)));

        g.MapGet("/{id:guid}/history", (string entity, Guid id, ClaimsPrincipal user, RecordService records, CancellationToken ct) =>
            records.HistoryAsync(Ctx(user), entity, id, ct));

        // One-to-many side of a relationship: child records whose lookup points at this record (only readable ones).
        g.MapGet("/{id:guid}/related/{relationship}", async (string entity, Guid id, string relationship, int? offset, int? size, ClaimsPrincipal user, RecordService records, ExperienceService experience, CancellationToken ct) =>
        {
            var ctx = Ctx(user);
            var spec = await experience.EntityAsync(ctx, entity, ct);
            var rel = spec.Relationships.FirstOrDefault(r => r.Id == relationship && r.Target == entity) ?? throw KernelException.NotFound($"Relationship '{relationship}'");
            await records.GetAsync(ctx, entity, id, ct); // the parent must be readable
            return await records.QueryAsync(ctx, new RecordQuery { Entity = rel.Entity, Filters = [$"record.{rel.Field} == '{id}'"], Offset = offset ?? 0, PageSize = size ?? 10 }, ct);
        });

        app.MapGet("/api/access/explain", (string entity, Guid? record, Guid? principal, string? field, ClaimsPrincipal user, AccessExplainer explainer, CancellationToken ct) =>
            explainer.ExplainAsync(Ctx(user), entity, record, principal, field, ct)).WithTags("Access").RequireAuthorization();
    }

    private static Kernel.Tenancy.RequestContext Ctx(ClaimsPrincipal user) => RequestContextFactory.From(user, "api");

    /// <summary>A safe file name from an entity ID (letters, digits, dots, dashes and underscores only).</summary>
    public static string FileName(string id) => new([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(80)]);
}
