using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.Api.Endpoints;

public sealed record ApplyCommandRequest(JsonElement Op, int ExpectedVersion);

public sealed record VersionRequest(int ExpectedVersion);

public sealed record PublishRequest(string ArtifactHash, int ExpectedRevision);

public sealed record RollbackRequest(int ToRevision, int ExpectedRevision);

public sealed record UpgradeRequest(string ToVersion);

public sealed record SimulateRequest(JsonObject Data);

/// <summary>Builder API: drafts, live previews, publish, rollback, upgrades. The same Commands the MCP tools write.</summary>
public static class BuilderEndpoints
{
    public static void MapBuilder(this IEndpointRouteBuilder app)
    {
        var d = app.MapGroup("/api/drafts").WithTags("Drafts").RequireAuthorization();
        d.MapPost("/", (ClaimsPrincipal u, DraftService drafts, CancellationToken ct) => drafts.OpenAsync(Ctx(u), ct));
        d.MapGet("/current", async (ClaimsPrincipal u, DraftService drafts, CancellationToken ct) =>
            await drafts.CurrentAsync(Ctx(u), ct) is { } state ? Results.Ok(state) : Results.NoContent());
        d.MapDelete("/current", async (ClaimsPrincipal u, DraftService drafts, CancellationToken ct) =>
        {
            await drafts.DiscardAsync(Ctx(u), ct);
            return Results.NoContent();
        });
        d.MapPost("/current/commands", (ApplyCommandRequest body, ClaimsPrincipal u, DraftService drafts, CancellationToken ct) =>
            drafts.ApplyAsync(Ctx(u), ParseOp(body.Op), body.ExpectedVersion, "ui", ct));
        d.MapPost("/current/steps/{seq:int}/undo", (int seq, VersionRequest body, ClaimsPrincipal u, DraftService drafts, CancellationToken ct) =>
            drafts.SetUndoneAsync(Ctx(u), seq, true, body.ExpectedVersion, ct));
        d.MapPost("/current/steps/{seq:int}/redo", (int seq, VersionRequest body, ClaimsPrincipal u, DraftService drafts, CancellationToken ct) =>
            drafts.SetUndoneAsync(Ctx(u), seq, false, body.ExpectedVersion, ct));
        d.MapGet("/current/views/{view}", (string view, ClaimsPrincipal u, PreviewService preview, CancellationToken ct) => preview.ViewAsync(Ctx(u), view, ct));
        d.MapGet("/current/sample/{entity}", (string entity, ClaimsPrincipal u, PreviewService preview, CancellationToken ct) => preview.SampleAsync(Ctx(u), entity, ct));
        d.MapPost("/current/simulate/{entity}", (string entity, SimulateRequest body, ClaimsPrincipal u, PreviewService preview, CancellationToken ct) => preview.SimulateAsync(Ctx(u), entity, body.Data ?? [], ct));
        d.MapPost("/current/publish-requests", (ClaimsPrincipal u, PublishService publisher, CancellationToken ct) => publisher.RequestAsync(Ctx(u), ct));

        var c = app.MapGroup("/api/config").WithTags("Configuration").RequireAuthorization();
        c.MapGet("/publish-requests/pending", async (ClaimsPrincipal u, PublishService publisher, CancellationToken ct) =>
            await publisher.PendingAsync(Ctx(u), ct) is { } p ? Results.Ok(p) : Results.NoContent());
        c.MapPost("/publish", (PublishRequest body, ClaimsPrincipal u, PublishService publisher, CancellationToken ct) => publisher.PublishAsync(Ctx(u), body.ArtifactHash, body.ExpectedRevision, ct));
        c.MapGet("/revisions", (ClaimsPrincipal u, PublishService publisher, CancellationToken ct) => publisher.RevisionsAsync(Ctx(u), ct));
        c.MapPost("/rollback", (RollbackRequest body, ClaimsPrincipal u, PublishService publisher, CancellationToken ct) => publisher.RollbackAsync(Ctx(u), body.ToRevision, body.ExpectedRevision, ct));

        var m = app.MapGroup("/api/modules").WithTags("Modules").RequireAuthorization();
        m.MapGet("/", (ClaimsPrincipal u, UpgradeService upgrades, CancellationToken ct) => upgrades.AvailableAsync(Ctx(u), ct));
        m.MapPost("/{module}/upgrade-plan", (string module, UpgradeRequest body, ClaimsPrincipal u, UpgradeService upgrades, CancellationToken ct) => upgrades.PlanAsync(Ctx(u), module, body.ToVersion, ct));
        m.MapPost("/{module}/upgrades", (string module, UpgradeRequest body, ClaimsPrincipal u, UpgradeService upgrades, CancellationToken ct) => upgrades.StartAsync(Ctx(u), module, body.ToVersion, ct));
        app.MapGet("/api/upgrades/{id:guid}", (Guid id, ClaimsPrincipal u, UpgradeService upgrades, CancellationToken ct) => upgrades.GetAsync(Ctx(u), id, ct)).WithTags("Modules").RequireAuthorization();
        app.MapPost("/api/upgrades/{id:guid}/resume", (Guid id, ClaimsPrincipal u, UpgradeService upgrades, CancellationToken ct) => upgrades.ResumeAsync(Ctx(u), id, ct)).WithTags("Modules").RequireAuthorization();
    }

    public static OverlayOp ParseOp(JsonElement op)
    {
        try
        {
            return JsonSerializer.Deserialize<OverlayOp>(op.GetRawText(), SpecJson.Options) ?? throw new JsonException("empty");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new KernelException("invalid_operation", $"The operation could not be read: {ex.Message}", 400, suggestion: "Send an object with \"op\" (e.g. \"field.add\") and its properties.");
        }
    }

    private static RequestContext Ctx(ClaimsPrincipal user) => RequestContextFactory.From(user, "ui");
}
