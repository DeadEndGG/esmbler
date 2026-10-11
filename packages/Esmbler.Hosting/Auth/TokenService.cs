using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Esmbler.Hosting.Auth;

public static class Audiences
{
    public const string Api = "esmbler-api";
    public const string Mcp = "esmbler-mcp";
}

public static class ClaimNames
{
    public const string Tenant = "tid";
    public const string Environment = "eid";
    public const string Agent = "act";
    public const string Scope = "scp";

    /// <summary>The external MCP client's ID (its metadata document URL) and the connection (ADR 0025).</summary>
    public const string Client = "client_id";
    public const string Connection = "cid";
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken);

/// <summary>
/// Issues short-lived access tokens (ADR 0011). API tokens use audience esmbler-api; agent tokens for the MCP server
/// use esmbler-mcp and carry the agent principal (act) and delegated scopes (scp). Tokens are never passed through.
/// </summary>
public sealed class TokenService(PlatformOptions options, Database database)
{
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromHours(8);

    /// <summary>A session ends this long after sign-in however often it is refreshed (ASM-032).</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    /// <summary>Prefix of the development signing key shipped in docker-compose.yml.</summary>
    public const string DevelopmentKeyPrefix = "dev-only";

    public SymmetricSecurityKey SigningKey { get; } = CreateKey(options.Auth.SigningKey, options.Auth.AllowDevelopmentKey);

    public static SymmetricSecurityKey CreateKey(string key, bool allowDevelopmentKey = true)
    {
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Esmbler:Auth:SigningKey must be at least 32 bytes.");
        }

        // The key in docker-compose.yml is public. Anyone holding it can forge tokens for every tenant (T-40).
        if (!allowDevelopmentKey && key.StartsWith(DevelopmentKeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Esmbler:Auth:SigningKey is the public development key. Set ESMBLER_SIGNING_KEY to a secret of at least 32 bytes, or set Esmbler:Auth:AllowDevelopmentKey=true for local development only.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public IssuedTokens IssueApi(Guid principalId, string name, Guid? tenantId, Guid? environmentId, string? refreshToken = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, principalId.ToString()), new(JwtRegisteredClaimNames.Name, name) };
        if (tenantId is { } t && environmentId is { } e)
        {
            claims.Add(new Claim(ClaimNames.Tenant, t.ToString()));
            claims.Add(new Claim(ClaimNames.Environment, e.ToString()));
        }

        var expires = DateTimeOffset.UtcNow.AddMinutes(options.Auth.AccessTokenMinutes);
        return new IssuedTokens(Write(claims, Audiences.Api, expires), expires, refreshToken);
    }

    /// <summary>The audience of MCP tokens: the canonical resource URI when the MCP server is public, else esmbler-mcp (ADR 0025).</summary>
    public string McpAudience => string.IsNullOrWhiteSpace(options.Mcp.PublicUrl) ? Audiences.Mcp : options.Mcp.PublicUrl!;

    public IssuedTokens IssueAgent(Guid principalId, string name, Guid tenantId, Guid environmentId, Guid agentId, IEnumerable<string> scopes) =>
        IssueAgent(principalId, name, tenantId, environmentId, agentId, scopes, null, null, TimeSpan.FromMinutes(options.Auth.AgentTokenMinutes));

    /// <summary>An MCP token; for an external client's connection it also names the client and connection (ADR 0025).</summary>
    public IssuedTokens IssueAgent(Guid principalId, string name, Guid tenantId, Guid environmentId, Guid agentId, IEnumerable<string> scopes, string? clientId, Guid? connectionId, TimeSpan lifetime)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, principalId.ToString()),
            new(JwtRegisteredClaimNames.Name, name),
            new(ClaimNames.Tenant, tenantId.ToString()),
            new(ClaimNames.Environment, environmentId.ToString()),
            new(ClaimNames.Agent, agentId.ToString()),
            new(ClaimNames.Scope, string.Join(' ', scopes)),
        };
        if (clientId is not null && connectionId is { } connection)
        {
            claims.Add(new Claim(ClaimNames.Client, clientId));
            claims.Add(new Claim(ClaimNames.Connection, connection.ToString()));
        }

        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        return new IssuedTokens(Write(claims, McpAudience, expires), expires, null);
    }

    /// <summary>A short-lived signed value only this platform can produce (OAuth pending requests, ADR 0025).</summary>
    public string Sign(IEnumerable<Claim> claims, string audience, TimeSpan lifetime) => Write(claims, audience, DateTimeOffset.UtcNow.Add(lifetime));

    /// <summary>Validates a value from <see cref="Sign"/>; null when it is forged, for another purpose or expired.</summary>
    public ClaimsPrincipal? Verify(string value, string audience)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            return handler.ValidateToken(value, Validation(audience), out _);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    public TokenValidationParameters Validation(string audience) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Auth.Issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        IssuerSigningKey = SigningKey,
        ValidateIssuerSigningKey = true,
        NameClaimType = JwtRegisteredClaimNames.Name,
    };

    /// <summary>Creates a refresh token bound to the principal (and the tenant/environment it was issued for).</summary>
    public Task<string> CreateRefreshAsync(Guid principalId, Guid? tenantId, Guid? environmentId, CancellationToken ct) =>
        database.AsPrincipalAsync(principalId, db => AddRefreshAsync(db, principalId, tenantId, environmentId, DateTimeOffset.UtcNow, ct), ct);

    private static async Task<string> AddRefreshAsync(KernelDbContext db, Guid principalId, Guid? tenantId, Guid? environmentId, DateTimeOffset sessionStartedAt, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = now + RefreshLifetime < sessionStartedAt + SessionLifetime ? now + RefreshLifetime : sessionStartedAt + SessionLifetime;
        db.RefreshTokens.Add(new RefreshTokenRow { Id = Guid.NewGuid(), PrincipalId = principalId, TokenHash = Hash(raw), TenantId = tenantId, EnvironmentId = environmentId, ExpiresAt = expires, SessionStartedAt = sessionStartedAt, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>Rotates a refresh token. Reuse of a rotated token revokes the whole family (theft signal).</summary>
    public async Task<(Guid PrincipalId, Guid? TenantId, Guid? EnvironmentId, string NewToken)> RotateRefreshAsync(string raw, CancellationToken ct)
    {
        var hash = Hash(raw);
        var found = await database.AnonymousAsync(db => db.Database.SqlQuery<RefreshLookup>($"SELECT id AS \"Id\", principal_id AS \"PrincipalId\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", expires_at AS \"ExpiresAt\", revoked_at AS \"RevokedAt\" FROM kernel.refresh_lookup({hash})").ToListAsync(ct), ct);
        var token = found.FirstOrDefault() ?? throw new KernelException("invalid_refresh", "Your session expired. Sign in again.", 401);
        if (token.RevokedAt is not null)
        {
            await database.AsPrincipalAsync(token.PrincipalId, db => db.RefreshTokens.Where(r => r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct), ct);
            throw new KernelException("invalid_refresh", "Your session was ended for safety. Sign in again.", 401);
        }

        if (token.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new KernelException("invalid_refresh", "Your session expired. Sign in again.", 401);
        }

        // Claim the token atomically: of two concurrent redemptions only one revokes it and gets a successor; the
        // other is treated as reuse and ends the whole family.
        var next = await database.AsPrincipalAsync(token.PrincipalId, async db =>
        {
            var claimed = await db.RefreshTokens.Where(r => r.Id == token.Id && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct);
            if (claimed != 1)
            {
                return null;
            }

            var started = await db.RefreshTokens.Where(r => r.Id == token.Id).Select(r => r.SessionStartedAt).FirstAsync(ct);
            return started + SessionLifetime <= DateTimeOffset.UtcNow ? string.Empty : await AddRefreshAsync(db, token.PrincipalId, token.TenantId, token.EnvironmentId, started, ct);
        }, ct);

        if (next is null)
        {
            await database.AsPrincipalAsync(token.PrincipalId, db => db.RefreshTokens.Where(r => r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct), ct);
            throw new KernelException("invalid_refresh", "Your session was ended for safety. Sign in again.", 401);
        }

        if (next.Length == 0)
        {
            throw new KernelException("invalid_refresh", "Your session reached its maximum length. Sign in again.", 401);
        }

        return (token.PrincipalId, token.TenantId, token.EnvironmentId, next);
    }

    public async Task RevokeRefreshAsync(string raw, CancellationToken ct)
    {
        var hash = Hash(raw);
        var found = await database.AnonymousAsync(db => db.Database.SqlQuery<RefreshLookup>($"SELECT id AS \"Id\", principal_id AS \"PrincipalId\", tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\", expires_at AS \"ExpiresAt\", revoked_at AS \"RevokedAt\" FROM kernel.refresh_lookup({hash})").ToListAsync(ct), ct);
        if (found.FirstOrDefault() is { } token)
        {
            await database.AsPrincipalAsync(token.PrincipalId, db => db.RefreshTokens.Where(r => r.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct), ct);
        }
    }

    private string Write(IEnumerable<Claim> claims, string audience, DateTimeOffset expires)
    {
        var token = new JwtSecurityToken(
            issuer: options.Auth.Issuer,
            audience: audience,
            claims: claims.Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())),
            notBefore: DateTime.UtcNow.AddSeconds(-5),
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private sealed record RefreshLookup(Guid Id, Guid PrincipalId, Guid? TenantId, Guid? EnvironmentId, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
}
