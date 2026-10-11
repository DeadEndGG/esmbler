using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Assistant;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Esmbler.Hosting.Auth;

/// <summary>Where /oauth/authorize sends the browser: the consent page, or back to the client with an error.</summary>
public sealed record AuthorizeOutcome(string? Redirect, int Status = 302, string? Error = null);

public sealed record ConsentEnvironment(Guid Id, string Key, string Name, string Kind, bool Allowed);

public sealed record ConsentView(string ClientName, string ClientHost, string RedirectHost, IReadOnlyList<string> Scopes, IReadOnlyList<string> OfferedScopes, string Policy, IReadOnlyList<ConsentEnvironment> Environments);

public sealed record ConnectionView(Guid Id, string ClientName, string ClientHost, string Person, Guid PrincipalId, string Environment, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>An OAuth token endpoint answer: the JSON body, or an RFC 6749 error.</summary>
public sealed record TokenOutcome(int Status, object Body);

/// <summary>
/// The minimal OAuth 2.1 authorization server for external MCP clients (ADR 0025): authorization code with PKCE S256,
/// clients identified by allowlisted Client ID Metadata Documents, the RFC 8707 resource bound to the MCP server's
/// canonical URI, RFC 9207 iss, consent by a signed-in person who chooses the workspace environment, one agent
/// principal per connection, 15-minute access tokens and rotating refresh tokens (reuse revokes the family, 30 days
/// absolute), RFC 7009 revocation, and connections people and administrators can list and revoke. Tenant policy
/// (ADR 0024 settings) decides whether external assistants may connect at all, to which environments, and whether
/// records:read may be granted. records:write is never grantable.
/// </summary>
public sealed class OAuthService(PlatformOptions options, Database database, TokenService tokens, IClientMetadataSource clients, ConfigStore configs, AccessService access, AiSettingsService ai, IClock clock)
{
    public const string RequestAudience = "esmbler-oauth-request";
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Scopes an external client may ask for; config:read is always granted, records:read only if the workspace allows it.</summary>
    public static IReadOnlyList<string> Grantable => AgentScopes.ExternalGrantable;

    /// <summary>On when the MCP server has a public URL and the issuer is an HTTPS URL (the clients need both).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Mcp.PublicUrl)
        && Uri.TryCreate(options.Auth.Issuer, UriKind.Absolute, out var issuer) && issuer.Scheme == Uri.UriSchemeHttps;

    public string Resource => options.Mcp.PublicUrl ?? string.Empty;

    public string Issuer => options.Auth.Issuer;

    /// <summary>RFC 8414 authorization server metadata (no registration endpoint: clients use metadata documents).</summary>
    public object Metadata(string issuerBase) => new Dictionary<string, object>
    {
        ["issuer"] = Issuer,
        ["authorization_endpoint"] = $"{issuerBase.TrimEnd('/')}/oauth/authorize",
        ["token_endpoint"] = $"{issuerBase.TrimEnd('/')}/oauth/token",
        ["revocation_endpoint"] = $"{issuerBase.TrimEnd('/')}/oauth/revoke",
        ["response_types_supported"] = new[] { "code" },
        ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
        ["code_challenge_methods_supported"] = new[] { "S256" },
        ["token_endpoint_auth_methods_supported"] = new[] { "none" },
        ["client_id_metadata_document_supported"] = true,
        ["authorization_response_iss_parameter_supported"] = true,
        ["scopes_supported"] = Grantable,
    };

    // ---- Authorization request ------------------------------------------------------------------------------------

    public async Task<AuthorizeOutcome> AuthorizeAsync(IReadOnlyDictionary<string, string?> q, string consentUrl, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new AuthorizeOutcome(null, 404, "External assistants are not enabled on this platform.");
        }

        var clientId = q.GetValueOrDefault("client_id");
        var client = clientId is null ? null : await clients.GetAsync(clientId, ct);
        if (client is null)
        {
            return new AuthorizeOutcome(null, 400, "This assistant is not one Esmbler accepts.");
        }

        // An unregistered redirect never receives anything, not even an error.
        var redirect = q.GetValueOrDefault("redirect_uri");
        if (redirect is null || !client.RedirectUris.Any(r => RedirectMatches(r, redirect)))
        {
            return new AuthorizeOutcome(null, 400, "The return address does not belong to this assistant.");
        }

        var state = q.GetValueOrDefault("state");
        string Fail(string error, string description) => With(redirect, ("error", error), ("error_description", description), ("state", state), ("iss", Issuer));
        if (q.GetValueOrDefault("response_type") != "code")
        {
            return new AuthorizeOutcome(Fail("unsupported_response_type", "Only the authorization code flow is supported."));
        }

        var challenge = q.GetValueOrDefault("code_challenge");
        if (q.GetValueOrDefault("code_challenge_method") != "S256" || challenge is null || challenge.Length != 43)
        {
            return new AuthorizeOutcome(Fail("invalid_request", "PKCE with S256 is required."));
        }

        if (q.GetValueOrDefault("resource") != Resource)
        {
            return new AuthorizeOutcome(Fail("invalid_target", "The resource must be this MCP server."));
        }

        var requested = (q.GetValueOrDefault("scope") ?? AgentScopes.ConfigRead).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        if (requested.Except(Grantable).Any())
        {
            return new AuthorizeOutcome(Fail("invalid_scope", $"Assistants may ask only for {string.Join(", ", Grantable)}."));
        }

        var request = tokens.Sign(
        [
            new Claim("client_id", client.ClientId),
            new Claim("client_name", client.Name),
            new Claim("client_host", client.Host),
            new Claim("redirect_uri", redirect),
            new Claim("state", state ?? string.Empty),
            new Claim("code_challenge", challenge),
            new Claim("scope", string.Join(' ', requested.Union([AgentScopes.ConfigRead]))),
        ], RequestAudience, RequestLifetime);
        return new AuthorizeOutcome(With(consentUrl, ("request", request)));
    }

    // ---- Consent (a signed-in person) -----------------------------------------------------------------------------

    public async Task<ConsentView> ConsentAsync(RequestContext ctx, string request, CancellationToken ct)
    {
        var r = Request(ctx, request);
        var resolved = await ai.ResolveAsync(ctx.TenantId, ctx.EnvironmentId, ct);
        var environments = await database.InTenantWideAsync(ctx.TenantId, ctx.PrincipalId, db => db.Environments.AsNoTracking().OrderBy(e => e.Kind).ThenBy(e => e.Key).ToListAsync(ct), ct);
        var offered = Grantable.Where(s => s != AgentScopes.RecordsRead || resolved.AllowRecordsRead).ToList();
        return new ConsentView(
            r.ClientName, r.ClientHost, new Uri(r.RedirectUri).Host, r.Scopes, offered, resolved.ExternalAssistants,
            [.. environments.Select(e => new ConsentEnvironment(e.Id, e.Key, e.Name, e.Kind, Allows(resolved.ExternalAssistants, e.Kind)))]);
    }

    /// <summary>Approves a pending request for one of the person's environments; returns where to send the browser.</summary>
    public async Task<string> ApproveAsync(RequestContext ctx, string request, Guid environmentId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var r = Request(ctx, request);
        if (!HttpClientMetadataSource.Acceptable(r.ClientId, options.Mcp.AllowedClientHosts, out _))
        {
            throw KernelException.Forbidden("This assistant is no longer allowed on this platform.");
        }

        var envCtx = ctx with { EnvironmentId = environmentId };
        var code = NewSecret();
        try
        {
            await database.InTenantAsync(ctx.TenantId, environmentId, ctx.PrincipalId, async db =>
            {
                // The workspace policy is read under a share lock: a concurrent change either comes before (and is
                // applied here) or after (and revokes this connection).
                var settings = (await db.AiSettings.FromSqlInterpolated($"SELECT * FROM kernel.ai_settings FOR SHARE").AsNoTracking().ToListAsync(ct)).FirstOrDefault();
                var policy = settings?.ExternalAssistants ?? "off";
                var granted = scopes.Intersect(r.Scopes).Union([AgentScopes.ConfigRead]).Where(s => s != AgentScopes.RecordsRead || settings?.AllowRecordsRead == true).Distinct().ToList();
                var environment = await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == environmentId && e.TenantId == ctx.TenantId, ct) ?? throw KernelException.NotFound("Environment");
                if (!Allows(policy, environment.Kind))
                {
                    throw KernelException.Forbidden(policy == "off"
                        ? "This workspace does not allow external assistants."
                        : "This workspace allows external assistants only in non-production environments.", "environment", "Choose a sandbox, or ask an administrator to change the policy under AI assistant settings.");
                }

                // The person must be able to edit configuration there themselves: an agent never has more than its principal.
                var artifact = await configs.GetActiveAsync(db, ctx.TenantId, environmentId, ct);
                var perms = await access.ForAsync(db, envCtx, artifact.Config, ct, fresh: true);
                if (!perms.Can(PlatformActions.ConfigEdit))
                {
                    throw KernelException.Forbidden("You cannot change configuration in this environment, so an assistant cannot either.");
                }

                var now = clock.UtcNow;
                var person = await db.Principals.AsNoTracking().FirstAsync(p => p.Id == ctx.PrincipalId, ct);
                var connection = await db.McpConnections.AsTracking().FirstOrDefaultAsync(c => c.PrincipalId == ctx.PrincipalId && c.ClientId == r.ClientId && c.EnvironmentId == environmentId && c.RevokedAt == null && c.ExpiresAt > now, ct);
                if (connection is null)
                {
                    var agent = new PrincipalRow
                    {
                        Id = Guid.NewGuid(),
                        Kind = "agent",
                        DisplayName = Cut($"{r.ClientName} ({r.ClientHost}) for {person.DisplayName}", 200),
                        ActsFor = ctx.PrincipalId,
                        AgentTenant = ctx.TenantId,
                        CreatedAt = now,
                    };
                    db.Principals.Add(agent);
                    connection = new McpConnectionRow
                    {
                        TenantId = ctx.TenantId,
                        Id = Guid.NewGuid(),
                        EnvironmentId = environmentId,
                        PrincipalId = ctx.PrincipalId,
                        AgentId = agent.Id,
                        ClientId = r.ClientId,
                        ClientHost = r.ClientHost,
                        ClientName = r.ClientName,
                        CreatedAt = now,
                        ExpiresAt = now + ConnectionLifetime,
                    };
                    db.McpConnections.Add(connection);
                }

                connection.Scopes = string.Join(' ', granted);
                db.OAuthCodes.Add(new OAuthCodeRow
                {
                    TenantId = ctx.TenantId,
                    Id = Guid.NewGuid(),
                    ConnectionId = connection.Id,
                    CodeHash = Hash(code),
                    RequestId = r.Id,
                    CodeChallenge = r.Challenge,
                    RedirectUri = r.RedirectUri,
                    ClientId = r.ClientId,
                    Scopes = connection.Scopes,
                    ExpiresAt = now + CodeLifetime,
                });
                AuditWriter.Add(db, envCtx, "mcp.connection_grant", "mcp_connection", connection.Id.ToString(), artifact.Revision, new { client = r.ClientHost, scopes = granted, environment = environment.Key });
                await db.SaveChangesAsync(ct);
                return 0;
            }, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw KernelException.Conflict("request_used", "This request was already answered.", suggestion: "Start connecting again from the assistant.");
        }

        return With(r.RedirectUri, ("code", code), ("state", r.State.Length == 0 ? null : r.State), ("iss", Issuer));
    }

    // ---- Token endpoint -------------------------------------------------------------------------------------------

    public async Task<TokenOutcome> TokenAsync(IReadOnlyDictionary<string, string?> form, CancellationToken ct)
    {
        if (!Enabled)
        {
            return Error(400, "unsupported_grant_type", "External assistants are not enabled on this platform.");
        }

        if (form.GetValueOrDefault("resource") is { } resource && resource != Resource)
        {
            return Error(400, "invalid_target", "The resource must be this MCP server.");
        }

        return form.GetValueOrDefault("grant_type") switch
        {
            "authorization_code" => await RedeemCodeAsync(form, ct),
            "refresh_token" => await RefreshAsync(form, ct),
            _ => Error(400, "unsupported_grant_type", "Use authorization_code or refresh_token."),
        };
    }

    private async Task<TokenOutcome> RedeemCodeAsync(IReadOnlyDictionary<string, string?> form, CancellationToken ct)
    {
        if (form.GetValueOrDefault("resource") is null)
        {
            return Error(400, "invalid_target", "The resource parameter is required.");
        }

        var code = form.GetValueOrDefault("code");
        var verifier = form.GetValueOrDefault("code_verifier");
        if (code is null || verifier is null || verifier.Length is < 43 or > 128)
        {
            return Error(400, "invalid_request", "code and a code_verifier of 43 to 128 characters are required.");
        }

        var hash = Hash(code);
        var where = await LookupAsync($"SELECT tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\" FROM kernel.oauth_code_lookup({hash})", ct);
        if (where is null)
        {
            return Error(400, "invalid_grant", "The code is not valid.");
        }

        return await database.InTenantAsync(where.TenantId, where.EnvironmentId, null, async db =>
        {
            var row = (await db.OAuthCodes.FromSqlInterpolated($"SELECT * FROM kernel.oauth_codes WHERE code_hash = {hash} FOR UPDATE").AsTracking().ToListAsync(ct)).Single();
            var now = clock.UtcNow;
            if (row.UsedAt is not null)
            {
                // A code redeemed twice was intercepted: end the connection.
                await RevokeConnectionCoreAsync(db, where.TenantId, row.ConnectionId, null, ct);
                await db.SaveChangesAsync(ct);
                return Error(400, "invalid_grant", "The code was already used.");
            }

            row.UsedAt = now;
            await db.SaveChangesAsync(ct);
            if (row.ExpiresAt < now || row.ClientId != form.GetValueOrDefault("client_id") || row.RedirectUri != form.GetValueOrDefault("redirect_uri")
                || !FixedTimeEquals(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), row.CodeChallenge))
            {
                return Error(400, "invalid_grant", "The code, client, return address or verifier does not match.");
            }

            return await IssueAsync(db, where.TenantId, row.ConnectionId, ct) ?? Error(400, "invalid_grant", "The connection is no longer active.");
        }, ct);
    }

    private async Task<TokenOutcome> RefreshAsync(IReadOnlyDictionary<string, string?> form, CancellationToken ct)
    {
        var raw = form.GetValueOrDefault("refresh_token");
        if (raw is null)
        {
            return Error(400, "invalid_request", "refresh_token is required.");
        }

        var hash = Hash(raw);
        var where = await LookupAsync($"SELECT tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\" FROM kernel.oauth_refresh_lookup({hash})", ct);
        if (where is null)
        {
            return Error(400, "invalid_grant", "The refresh token is not valid.");
        }

        return await database.InTenantAsync(where.TenantId, where.EnvironmentId, null, async db =>
        {
            var row = (await db.OAuthRefreshTokens.FromSqlInterpolated($"SELECT * FROM kernel.oauth_refresh_tokens WHERE token_hash = {hash} FOR UPDATE").AsTracking().ToListAsync(ct)).Single();
            var connection = await db.McpConnections.AsNoTracking().FirstAsync(c => c.Id == row.ConnectionId, ct);
            if (row.RevokedAt is not null)
            {
                // A rotated token came back: it was copied. End every token of the connection (T-51).
                await RevokeConnectionCoreAsync(db, where.TenantId, row.ConnectionId, null, ct);
                AuditWriter.AddSystem(db, where.TenantId, where.EnvironmentId, "mcp.refresh_reuse", "mcp_connection", row.ConnectionId.ToString(), new { client = connection.ClientHost });
                await db.SaveChangesAsync(ct);
                return Error(400, "invalid_grant", "The refresh token was already used.");
            }

            if (row.ExpiresAt < clock.UtcNow || connection.ClientId != form.GetValueOrDefault("client_id"))
            {
                return Error(400, "invalid_grant", "The refresh token expired or belongs to another client.");
            }

            row.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return await IssueAsync(db, where.TenantId, row.ConnectionId, ct) ?? Error(400, "invalid_grant", "The connection is no longer active.");
        }, ct);
    }

    /// <summary>Access and refresh tokens for an active connection whose person may still edit configuration there; null otherwise.</summary>
    private async Task<TokenOutcome?> IssueAsync(KernelDbContext db, Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var connection = await db.McpConnections.AsNoTracking().FirstAsync(c => c.Id == connectionId, ct);
        if (connection.RevokedAt is not null || connection.ExpiresAt <= now)
        {
            return null;
        }

        var person = await db.Principals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == connection.PrincipalId && p.Kind == "user" && !p.Disabled, ct);
        var agent = await db.Principals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == connection.AgentId && !p.Disabled, ct);
        var member = await db.Memberships.AnyAsync(m => m.PrincipalId == connection.PrincipalId && m.TenantId == tenantId, ct);
        var environment = await db.Environments.AsNoTracking().FirstAsync(e => e.Id == connection.EnvironmentId, ct);
        var settings = await db.AiSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var policy = settings?.ExternalAssistants ?? "off";
        if (person is null || agent is null || !member || !Allows(policy, environment.Kind) || !HttpClientMetadataSource.Acceptable(connection.ClientId, options.Mcp.AllowedClientHosts, out _))
        {
            return null;
        }

        var ctx = new RequestContext { TenantId = tenantId, EnvironmentId = connection.EnvironmentId, PrincipalId = connection.PrincipalId, Channel = "mcp" };
        var artifact = await configs.GetActiveAsync(db, tenantId, connection.EnvironmentId, ct);
        if (!(await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true)).Can(PlatformActions.ConfigEdit))
        {
            return null;
        }

        // Record reading the workspace no longer allows is dropped, whatever was granted at consent.
        var scopes = connection.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(s => s != AgentScopes.RecordsRead || settings?.AllowRecordsRead == true).ToArray();
        var access_ = tokens.IssueAgent(person.Id, person.DisplayName, tenantId, connection.EnvironmentId, connection.AgentId, scopes, connection.ClientId, connection.Id, AccessLifetime);
        var refresh = NewSecret();
        db.OAuthRefreshTokens.Add(new OAuthRefreshTokenRow { TenantId = tenantId, Id = Guid.NewGuid(), ConnectionId = connection.Id, TokenHash = Hash(refresh), CreatedAt = now, ExpiresAt = connection.ExpiresAt });
        await db.SaveChangesAsync(ct);
        return new TokenOutcome(200, new Dictionary<string, object>
        {
            ["access_token"] = access_.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)AccessLifetime.TotalSeconds,
            ["refresh_token"] = refresh,
            ["scope"] = string.Join(' ', scopes),
        });
    }

    /// <summary>RFC 7009: revokes a refresh token's whole family; unknown tokens are not an error.</summary>
    public async Task RevokeTokenAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var hash = Hash(token);
        if (await LookupAsync($"SELECT tenant_id AS \"TenantId\", environment_id AS \"EnvironmentId\" FROM kernel.oauth_refresh_lookup({hash})", ct) is { } where)
        {
            // A client revoking its refresh token disconnects: the connection, its agent and its tokens end.
            await database.InTenantAsync(where.TenantId, where.EnvironmentId, null, async db =>
            {
                var connection = await db.OAuthRefreshTokens.Where(t => t.TokenHash == hash).Select(t => t.ConnectionId).FirstAsync(ct);
                await RevokeConnectionCoreAsync(db, where.TenantId, connection, null, ct);
                AuditWriter.AddSystem(db, where.TenantId, where.EnvironmentId, "mcp.connection_revoke", "mcp_connection", connection.ToString(), new { by = "client" }, outcome: "success");
                await db.SaveChangesAsync(ct);
                return 0;
            }, ct);
        }
    }

    // ---- Connections ----------------------------------------------------------------------------------------------

    /// <summary>The person's own connections in this workspace, or everyone's for permission administrators.</summary>
    public Task<IReadOnlyList<ConnectionView>> ConnectionsAsync(RequestContext ctx, bool all, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<ConnectionView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            RefuseAgents(ctx);
            if (all)
            {
                await RequireAdminAsync(db, ctx, ct);
            }

            var rows = await db.McpConnections.AsNoTracking().Where(c => all || c.PrincipalId == ctx.PrincipalId).OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct);
            var people = rows.Select(r => r.PrincipalId).Distinct().ToList();
            var names = await db.Principals.AsNoTracking().Where(p => people.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            var envs = await db.Environments.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, ct);
            return rows.Select(r => new ConnectionView(r.Id, r.ClientName, r.ClientHost, names.GetValueOrDefault(r.PrincipalId, "someone"), r.PrincipalId, envs.GetValueOrDefault(r.EnvironmentId, "?"),
                r.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), r.CreatedAt, r.ExpiresAt, r.LastUsedAt, r.RevokedAt)).ToList();
        }, ct);

    /// <summary>Ends a connection (its owner or a permission administrator): its agent is disabled and its tokens revoked.</summary>
    public Task RevokeConnectionAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            RefuseAgents(ctx);
            var connection = await db.McpConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw KernelException.NotFound("Connection");
            if (connection.PrincipalId != ctx.PrincipalId)
            {
                await RequireAdminAsync(db, ctx, ct);
            }

            await RevokeConnectionCoreAsync(db, ctx.TenantId, id, ctx.PrincipalId, ct);
            AuditWriter.Add(db, ctx, "mcp.connection_revoke", "mcp_connection", id.ToString(), null, new { client = connection.ClientHost }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    /// <summary>Whether an external client's connection is still active (the MCP host uses <see cref="McpConnectionStatus"/>).</summary>
    public Task<bool> ConnectionActiveAsync(Guid tenantId, Guid connectionId, CancellationToken ct) =>
        new McpConnectionStatus(database, clock).ActiveAsync(tenantId, connectionId, ct);

    private async Task RevokeConnectionCoreAsync(KernelDbContext db, Guid tenantId, Guid connectionId, Guid? by, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var agent = await db.McpConnections.Where(c => c.Id == connectionId).Select(c => c.AgentId).FirstAsync(ct);
        await db.McpConnections.Where(c => c.Id == connectionId && c.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedBy, by), ct);
        await db.OAuthRefreshTokens.Where(t => t.ConnectionId == connectionId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.Principals.Where(p => p.Id == agent && p.AgentTenant == tenantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Disabled, true), ct);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private sealed record PendingRequest(string Id, string ClientId, string ClientName, string ClientHost, string RedirectUri, string State, string Challenge, IReadOnlyList<string> Scopes);

    private PendingRequest Request(RequestContext ctx, string request)
    {
        RefuseAgents(ctx);
        var p = tokens.Verify(request, RequestAudience) ?? throw new KernelException("invalid_request", "This connection request expired or is not valid.", 400, suggestion: "Start connecting again from the assistant.");
        string Claim(string type) => p.FindFirst(type)?.Value ?? string.Empty;
        return new PendingRequest(Claim(JwtRegisteredClaimNames.Jti), Claim("client_id"), Claim("client_name"), Claim("client_host"), Claim("redirect_uri"), Claim("state"), Claim("code_challenge"),
            Claim("scope").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void RefuseAgents(RequestContext ctx)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("Only a person can connect or manage assistants.");
        }
    }

    private async Task RequireAdminAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        if (!(await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true)).Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden("Other people's connections are for permission administrators.");
        }
    }

    /// <summary>Exact match, except that a loopback redirect may use any port (RFC 8252 section 7.3, native clients).</summary>
    private static bool RedirectMatches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(registered, UriKind.Absolute, out var r) && Uri.TryCreate(requested, UriKind.Absolute, out var q)
            && r.Scheme == Uri.UriSchemeHttp && q.Scheme == Uri.UriSchemeHttp && r.IsLoopback && q.IsLoopback && r.Host == q.Host
            && r.AbsolutePath == q.AbsolutePath && r.Query == q.Query && q.Fragment.Length == 0 && q.UserInfo.Length == 0;
    }

    private static bool Allows(string policy, string environmentKind) => policy == "all" || (policy == "non_production" && environmentKind != "production");

    private sealed class Where
    {
        public Guid TenantId { get; set; }

        public Guid EnvironmentId { get; set; }
    }

    private async Task<Where?> LookupAsync(FormattableString sql, CancellationToken ct)
    {
        var rows = await database.AnonymousAsync(db => db.Database.SqlQuery<Where>(sql).ToListAsync(ct), ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    private static TokenOutcome Error(int status, string error, string description) => new(status, new Dictionary<string, object> { ["error"] = error, ["error_description"] = description });

    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static string With(string url, params (string Key, string? Value)[] parameters)
    {
        var query = string.Join('&', parameters.Where(p => p.Value is not null).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;
    }
}
