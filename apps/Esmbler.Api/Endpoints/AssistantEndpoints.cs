using System.Security.Claims;
using Esmbler.Api.Assistant;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Assistant;

namespace Esmbler.Api.Endpoints;

public static class AssistantEndpoints
{
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assistant").WithTags("Assistant").RequireAuthorization();
        g.MapGet("/status", (ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) => assistant.StatusAsync(user, ct));
        g.MapPost("/messages", async (AssistantMessageRequest body, ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
            Results.Accepted(value: await assistant.SendAsync(user, body, ct)));
        g.MapPost("/conversations/{id}/stop", (string id, ClaimsPrincipal user, AssistantService assistant) =>
        {
            assistant.Stop(user, id);
            return Results.NoContent();
        });

        // AI provider settings (ADR 0024): model from the operator's catalog, effort, daily budget; never keys.
        var ai = app.MapGroup("/api/admin/ai").WithTags("Assistant").RequireAuthorization();
        ai.MapGet("/", (ClaimsPrincipal user, AiSettingsService settings, CancellationToken ct) => settings.GetAsync(RequestContextFactory.From(user, "api"), ct));
        ai.MapPut("/", (AiSettingsRequest body, ClaimsPrincipal user, AiSettingsService settings, CancellationToken ct) => settings.SaveAsync(RequestContextFactory.From(user, "api"), body, ct));
    }
}
