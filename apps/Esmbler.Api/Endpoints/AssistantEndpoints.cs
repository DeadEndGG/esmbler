using System.Security.Claims;
using Esmbler.Api.Assistant;

namespace Esmbler.Api.Endpoints;

public static class AssistantEndpoints
{
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assistant").WithTags("Assistant").RequireAuthorization();
        g.MapGet("/status", (AssistantService assistant) => assistant.Status());
        g.MapPost("/messages", async (AssistantMessageRequest body, ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
            Results.Accepted(value: await assistant.SendAsync(user, body, ct)));
        g.MapPost("/conversations/{id}/stop", (string id, ClaimsPrincipal user, AssistantService assistant) =>
        {
            assistant.Stop(user, id);
            return Results.NoContent();
        });
    }
}
