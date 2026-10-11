using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Hosting.Auth;

/// <summary>
/// What the MCP host needs from OAuth (ADR 0025): whether an external client's connection is still active, checked on
/// every tool call, and when it was last used. The authorization server itself (<see cref="OAuthService"/>) is
/// registered only by the API host.
/// </summary>
public sealed class McpConnectionStatus(Database database, IClock clock)
{
    public Task<bool> ActiveAsync(Guid tenantId, Guid connectionId, CancellationToken ct) =>
        database.InTenantWideAsync(tenantId, null, async db =>
        {
            var now = clock.UtcNow;
            var connection = await db.McpConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
            if (connection is null || connection.RevokedAt is not null || connection.ExpiresAt <= now)
            {
                return false;
            }

            if (connection.LastUsedAt is null || now - connection.LastUsedAt > TimeSpan.FromMinutes(1))
            {
                await db.McpConnections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now), ct);
            }

            return true;
        }, ct);
}
