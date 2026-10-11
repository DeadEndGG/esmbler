using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http.Features;

namespace Esmbler.Api.Endpoints;

public sealed record MailDecisionRequest(Guid? Requester);

/// <summary>
/// Email intake (ADR 0023): the inbound webhook (no session; the mailbox's secret token in the Authorization header,
/// the raw RFC 5322 message as the body), the review queue for people who work the intake's records, and mailbox
/// settings for people with notifications.manage. The server decides rights on every call.
/// </summary>
public static class MailEndpoints
{
    public static void MapMail(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/mail/inbound", async (HttpRequest request, MailIntakeService intake, CancellationToken ct) =>
        {
            var header = request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
            if (await intake.ResolveTokenAsync(token, ct) is not { } inbox)
            {
                throw new KernelException("invalid_token", "The mailbox token is missing or not valid.", 401, suggestion: "Copy the token from the mailbox settings, or rotate it.");
            }

            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = MailParser.MaxBytes + 1;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MailParser.MaxBytes)
                {
                    throw new KernelException("mail_too_large", $"A message must be at most {MailParser.MaxBytes / (1024 * 1024)} MB.", 413);
                }

                buffer.Write(chunk, 0, read);
            }

            var receipt = await intake.ReceiveAsync(inbox.TenantId, inbox.EnvironmentId, inbox.InboxId, buffer.ToArray(), ct);
            return Results.Accepted(value: receipt);
        }).AllowAnonymous().RequireRateLimiting(LoginThrottle.InboundMailPolicy).WithTags("Mail").DisableAntiforgery();

        var messages = app.MapGroup("/api/mail/messages").WithTags("Mail").RequireAuthorization();
        messages.MapGet("/", (string? status, ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.ListAsync(RequestContextFactory.From(user, "api"), status, ct));
        messages.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.GetAsync(RequestContextFactory.From(user, "api"), id, ct));
        messages.MapPost("/{id:guid}/accept", (Guid id, MailDecisionRequest? body, ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.AcceptAsync(RequestContextFactory.From(user, "api"), id, body?.Requester, ct));
        messages.MapPost("/{id:guid}/reject", (Guid id, ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.RejectAsync(RequestContextFactory.From(user, "api"), id, ct));

        var inboxes = app.MapGroup("/api/admin/mail/inboxes").WithTags("Mail").RequireAuthorization();
        inboxes.MapGet("/", (ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.InboxesAsync(RequestContextFactory.From(user, "api"), ct));
        inboxes.MapPut("/{key}", (string key, MailInboxRequest body, ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.SaveInboxAsync(RequestContextFactory.From(user, "api"), body with { Key = key }, ct));
        inboxes.MapPost("/{key}/rotate-token", (string key, ClaimsPrincipal user, MailIntakeService intake, CancellationToken ct) =>
            intake.RotateTokenAsync(RequestContextFactory.From(user, "api"), key, ct));
    }
}
