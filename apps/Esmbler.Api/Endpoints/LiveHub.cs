using System.Text.Json;
using Esmbler.Hosting;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Esmbler.Api.Endpoints;

/// <summary>
/// Real-time channel for live editing (ADR 0008, REQ-LIVE-002). Clients join their environment's draft group only
/// after an authorization check; the server pushes ids and versions, and clients reload what they may see.
/// </summary>
[Authorize]
public sealed class LiveHub(DraftService drafts) : Hub
{
    public static string DraftGroup(Guid tenant, Guid env) => $"draft:{tenant:N}:{env:N}";

    public static string UserGroup(Guid principal) => $"user:{principal:N}";

    public override async Task OnConnectedAsync()
    {
        var principal = RequestContextFactory.PrincipalId(Context.User!);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(principal));
        await base.OnConnectedAsync();
    }

    /// <summary>Starts receiving draft changes for the caller's tenant environment (builders only).</summary>
    public async Task WatchDraft()
    {
        var ctx = RequestContextFactory.From(Context.User!, "ui");
        await drafts.CurrentAsync(ctx, Context.ConnectionAborted); // throws unless the caller may edit configuration
        await Groups.AddToGroupAsync(Context.ConnectionId, DraftGroup(ctx.TenantId, ctx.EnvironmentId));
    }

    public async Task UnwatchDraft()
    {
        var ctx = RequestContextFactory.From(Context.User!, "ui");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, DraftGroup(ctx.TenantId, ctx.EnvironmentId));
    }
}

/// <summary>LISTENs on Postgres for draft notifications (sent by the API and the MCP server) and fans them out.</summary>
public sealed class DraftNotificationListener(PlatformOptions options, IHubContext<LiveHub> hub, ILogger<DraftNotificationListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(options.Database.App) { KeepAlive = 30 }.ConnectionString);
                await conn.OpenAsync(stoppingToken);
                conn.Notification += (_, e) => _ = ForwardAsync(e.Payload);
                await using (var cmd = new NpgsqlCommand("LISTEN esmbler_draft", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Draft listener lost its connection; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ForwardAsync(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var tenant = root.GetProperty("t").GetGuid();
            var env = root.GetProperty("e").GetGuid();
            await hub.Clients.Group(LiveHub.DraftGroup(tenant, env)).SendAsync("draftChanged", JsonSerializer.Deserialize<JsonElement>(payload));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring malformed draft notification");
        }
    }
}
