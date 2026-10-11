using System.Net;
using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;

namespace Esmbler.Api.Endpoints;

public sealed record ConsentRequest(string Request, Guid EnvironmentId, IReadOnlyList<string>? Scopes);

/// <summary>
/// OAuth for external MCP clients (ADR 0025): RFC 8414 metadata, the authorization endpoint (which sends the browser to
/// the web consent page), the token and revocation endpoints (form-encoded, no session), the consent API for a
/// signed-in person, and connections people and administrators list and revoke.
/// </summary>
public static class OAuthEndpoints
{
    public static void MapOAuth(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var web = (configuration["Esmbler:WebUrl"] ?? "http://localhost:8080").TrimEnd('/');

        app.MapGet("/.well-known/oauth-authorization-server", (OAuthService oauth) =>
            oauth.Enabled ? Results.Json(oauth.Metadata(oauth.Issuer)) : Results.NotFound()).AllowAnonymous().WithTags("OAuth");

        app.MapGet("/oauth/authorize", async (HttpRequest request, HttpResponse response, OAuthService oauth, CancellationToken ct) =>
        {
            NoStore(response);
            var outcome = await oauth.AuthorizeAsync(request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString()), $"{web}/connect", ct);
            return outcome.Redirect is { } to
                ? Results.Redirect(to)
                : Results.Content($"<!doctype html><title>Esmbler</title><p>{WebUtility.HtmlEncode(outcome.Error)}</p>", "text/html; charset=utf-8", statusCode: outcome.Status);
        }).AllowAnonymous().RequireRateLimiting(LoginThrottle.OAuthPolicy).WithTags("OAuth");

        app.MapPost("/oauth/token", async (HttpRequest request, HttpResponse response, OAuthService oauth, CancellationToken ct) =>
        {
            NoStore(response);
            if (!request.HasFormContentType)
            {
                return Results.Json(new { error = "invalid_request", error_description = "Send application/x-www-form-urlencoded." }, statusCode: 400);
            }

            var form = await request.ReadFormAsync(ct);
            var outcome = await oauth.TokenAsync(form.ToDictionary(f => f.Key, f => (string?)f.Value.ToString()), ct);
            return Results.Json(outcome.Body, statusCode: outcome.Status);
        }).AllowAnonymous().RequireRateLimiting(LoginThrottle.OAuthPolicy).DisableAntiforgery().WithTags("OAuth");

        app.MapPost("/oauth/revoke", async (HttpRequest request, OAuthService oauth, CancellationToken ct) =>
        {
            if (request.HasFormContentType)
            {
                await oauth.RevokeTokenAsync((await request.ReadFormAsync(ct))["token"].ToString(), ct);
            }

            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting(LoginThrottle.OAuthPolicy).DisableAntiforgery().WithTags("OAuth");

        var consent = app.MapGroup("/api/oauth/consent").WithTags("OAuth").RequireAuthorization();
        consent.MapGet("/", (string request, ClaimsPrincipal user, OAuthService oauth, CancellationToken ct) =>
            oauth.ConsentAsync(Interactive(user), request, ct));
        consent.MapPost("/", async (ConsentRequest body, ClaimsPrincipal user, OAuthService oauth, CancellationToken ct) =>
            new { redirect = await oauth.ApproveAsync(Interactive(user), body.Request, body.EnvironmentId, body.Scopes ?? [], ct) });

        app.MapGet("/api/me/connections", (ClaimsPrincipal user, OAuthService oauth, CancellationToken ct) =>
            oauth.ConnectionsAsync(RequestContextFactory.From(user, "api"), false, ct)).RequireAuthorization().WithTags("OAuth");
        app.MapGet("/api/admin/connections", (ClaimsPrincipal user, OAuthService oauth, CancellationToken ct) =>
            oauth.ConnectionsAsync(RequestContextFactory.From(user, "api"), true, ct)).RequireAuthorization().WithTags("OAuth");
        app.MapPost("/api/connections/{id:guid}/revoke", async (Guid id, ClaimsPrincipal user, OAuthService oauth, CancellationToken ct) =>
        {
            await oauth.RevokeConnectionAsync(RequestContextFactory.From(user, "api"), id, ct);
            return Results.NoContent();
        }).RequireAuthorization().WithTags("OAuth");
    }

    /// <summary>Consent needs a person's own session: tokens of AI agents are refused.</summary>
    private static Kernel.Tenancy.RequestContext Interactive(ClaimsPrincipal user) =>
        user.FindFirst(ClaimNames.Agent) is null ? RequestContextFactory.From(user, "ui") : throw KernelException.Forbidden("Only a person can connect an assistant.");

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
