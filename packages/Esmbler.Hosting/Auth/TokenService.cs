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
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken);

/// <summary>
/// Issues short-lived access tokens (ADR 0011). API tokens use audience esmbler-api; agent tokens for the MCP server
/// use esmbler-mcp and carry the agent principal (act) and delegated scopes (scp). Tokens are never passed through.
/// </summary>
public sealed class TokenService(PlatformOptions options, Database database)
{
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromHours(8);

    public SymmetricSecurityKey SigningKey { get; } = CreateKey(options.Auth.SigningKey);

    public static SymmetricSecurityKey CreateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Esmbler:Auth:SigningKey must be at least 32 bytes.");
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

    public IssuedTokens IssueAgent(Guid principalId, string name, Guid tenantId, Guid environmentId, Guid agentId, IEnumerable<string> scopes)
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
        var expires = DateTimeOffset.UtcNow.AddMinutes(options.Auth.AgentTokenMinutes);
        return new IssuedTokens(Write(claims, Audiences.Mcp, expires), expires, null);
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
        database.AsPrincipalAsync(principalId, async db =>
        {
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            db.RefreshTokens.Add(new RefreshTokenRow { Id = Guid.NewGuid(), PrincipalId = principalId, TokenHash = Hash(raw), TenantId = tenantId, EnvironmentId = environmentId, ExpiresAt = DateTimeOffset.UtcNow + RefreshLifetime, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            return raw;
        }, ct);

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

        var next = await CreateRefreshAsync(token.PrincipalId, token.TenantId, token.EnvironmentId, ct);
        await database.AsPrincipalAsync(token.PrincipalId, db => db.RefreshTokens.Where(r => r.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct), ct);
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
