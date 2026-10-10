using System.Security.Claims;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Notifications;

namespace Esmbler.Api.Endpoints;

public sealed record PreferenceRequest(string Route, string Channel, bool Muted);

/// <summary>
/// Notifications (ADR 0021): the signed-in person's inbox and preferences, and for people who manage notifications the
/// environment's channel instances, delivery history, replay, test sends and route previews.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").WithTags("Notifications").RequireAuthorization();
        me.MapGet("/notifications", (bool? unread, DateTimeOffset? before, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            service.InboxAsync(RequestContextFactory.From(user, "api"), unread ?? false, before, ct));
        me.MapGet("/notifications/unread-count", async (ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            new { unread = await service.UnreadAsync(RequestContextFactory.From(user, "api"), ct) });
        me.MapPost("/notifications/{id:guid}/read", async (Guid id, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
        {
            await service.MarkReadAsync(RequestContextFactory.From(user, "api"), id, ct);
            return Results.NoContent();
        });
        me.MapPost("/notifications/read-all", async (ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            new { read = await service.MarkReadAsync(RequestContextFactory.From(user, "api"), null, ct) });
        me.MapGet("/notification-preferences", (ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            service.PreferencesAsync(RequestContextFactory.From(user, "api"), ct));
        me.MapPut("/notification-preferences", async (PreferenceRequest body, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
        {
            await service.SetPreferenceAsync(RequestContextFactory.From(user, "api"), body.Route, body.Channel, body.Muted, ct);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/admin/notifications").WithTags("Notifications").RequireAuthorization();
        admin.MapGet("/channels", (ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            service.ChannelsAsync(RequestContextFactory.From(user, "api"), ct));
        admin.MapPut("/channels/{key}", (string key, SaveChannelRequest body, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            service.SaveChannelAsync(RequestContextFactory.From(user, "api"), body with { Key = key }, ct));
        admin.MapPost("/channels/{key}/test", async (string key, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            new { delivery = await service.TestSendAsync(RequestContextFactory.From(user, "api"), key, ct) }).RequireRateLimiting(LoginThrottle.WorkspacePolicy);
        admin.MapGet("/deliveries", (string? status, Guid? @event, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            service.DeliveriesAsync(RequestContextFactory.From(user, "api"), status, @event, ct));
        admin.MapPost("/deliveries/{id:guid}/replay", async (Guid id, bool? force, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            new { delivery = await service.ReplayAsync(RequestContextFactory.From(user, "api"), id, force ?? false, ct) });
        admin.MapGet("/preview", (string route, Guid? record, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            service.PreviewAsync(RequestContextFactory.From(user, "api"), route, record, ct));
    }
}
