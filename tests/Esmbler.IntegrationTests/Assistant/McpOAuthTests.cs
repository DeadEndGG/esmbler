using System.Security.Cryptography;
using System.Text;
using Esmbler.Hosting;
using Esmbler.Hosting.Auth;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Assistant;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Assistant;

/// <summary>
/// REQ-AUTHZ-007, REQ-AISAFE-004, REQ-AI-006 (ADR 0025): OAuth for external MCP clients. Only allowlisted clients with an
/// exact redirect, PKCE S256 and this server as resource get anywhere; a signed-in person consents for an environment
/// the workspace policy allows, with scopes it allows; codes are single use; refresh tokens rotate and reuse ends the
/// family; connections are listed, revoked and isolated per workspace; tokens are bound to the MCP server's audience.
/// </summary>
[Collection(PlatformCollection.Name)]
public class McpOAuthTests(PlatformFixture f)
{
    private const string PublicUrl = "https://mcp.esmbler.test/mcp";
    private const string ClientId = "https://chatgpt.com/oauth/esmbler-client.json";
    private const string Callback = "https://chatgpt.com/connector/oauth/callback";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk-and-some-more";

    private sealed class FakeClients : IClientMetadataSource
    {
        public Task<ClientMetadata?> GetAsync(string clientId, CancellationToken ct) => Task.FromResult(
            clientId == ClientId ? new ClientMetadata(ClientId, "chatgpt.com", "ChatGPT", [Callback]) : null);
    }

    private (OAuthService OAuth, TokenService Tokens) Services()
    {
        var platform = f.Get<PlatformOptions>();
        var auth = new PlatformOptions.AuthSettings { SigningKey = platform.Auth.SigningKey, AllowDevelopmentKey = platform.Auth.AllowDevelopmentKey, Issuer = "https://app.esmbler.test" };
        var options = new PlatformOptions { Auth = auth, Mcp = new PlatformOptions.McpSettings { PublicUrl = PublicUrl } };
        var tokens = new TokenService(options, f.Get<Database>());
        return (new OAuthService(options, f.Get<Database>(), tokens, new FakeClients(), f.Get<ConfigStore>(), f.Get<AccessService>(), f.Get<AiSettingsService>(), f.Get<IClock>()), tokens);
    }

    private static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string?> Authorize(string scope = "config:read config:edit records:read", string? challenge = null, string resource = PublicUrl, string redirect = Callback, string method = "S256") => new()
    {
        ["response_type"] = "code",
        ["client_id"] = ClientId,
        ["redirect_uri"] = redirect,
        ["state"] = "s-123",
        ["code_challenge"] = challenge ?? Challenge(Verifier),
        ["code_challenge_method"] = method,
        ["scope"] = scope,
        ["resource"] = resource,
    };

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private async Task<(ProvisionedTenant Tenant, string Code)> ConnectAsync(OAuthService oauth, string prefix, string policy = "non_production", bool allowRecords = false)
    {
        var t = await f.NewTenantAsync(prefix, "1.9.0");
        await f.Get<AiSettingsService>().SaveAsync(f.As(t, "admin"), new AiSettingsRequest(null, null, 20, allowRecords, policy, null), default);
        var start = await oauth.AuthorizeAsync(Authorize(), "https://app.esmbler.test/connect", default);
        var request = Query(start.Redirect!)["request"];
        var approved = await oauth.ApproveAsync(f.As(t, "admin"), request, t.Environments["sandbox"], ["config:read", "config:edit", "records:read"], default);
        return (t, Query(approved)["code"]);
    }

    private static Dictionary<string, string?> CodeForm(string code, string verifier = Verifier) => new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["code_verifier"] = verifier,
        ["redirect_uri"] = Callback,
        ["client_id"] = ClientId,
        ["resource"] = PublicUrl,
    };

    private static Dictionary<string, string?> RefreshForm(string refresh) => new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = ClientId, ["resource"] = PublicUrl };

    private static string Field(TokenOutcome outcome, string name) => ((Dictionary<string, object>)outcome.Body)[name].ToString()!;

    [Fact]
    public async Task A_person_connects_an_assistant_to_a_sandbox_and_it_gets_tokens_for_this_server_only()
    {
        var (oauth, tokens) = Services();
        var t = await f.NewTenantAsync("oa", "1.9.0");
        var admin = f.As(t, "admin");

        // Off by default: the consent page says so and approving is refused.
        var start = await oauth.AuthorizeAsync(Authorize(), "https://app.esmbler.test/connect", default);
        Assert.StartsWith("https://app.esmbler.test/connect?request=", start.Redirect, StringComparison.Ordinal);
        var request = Query(start.Redirect!)["request"];
        Assert.Equal("off", (await oauth.ConsentAsync(admin, request, default)).Policy);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => oauth.ApproveAsync(admin, request, t.Environments["sandbox"], ["config:edit"], default))).Status);

        // Non-production only, and record reading is not offered unless the workspace allows it.
        await f.Get<AiSettingsService>().SaveAsync(admin, new AiSettingsRequest(null, null, 20, false, "non_production", null), default);
        var view = await oauth.ConsentAsync(admin, request, default);
        Assert.Equal(("ChatGPT", "chatgpt.com", "chatgpt.com"), (view.ClientName, view.ClientHost, view.RedirectHost));
        Assert.DoesNotContain("records:read", view.OfferedScopes);
        Assert.False(view.Environments.Single(e => e.Kind == "production").Allowed);
        Assert.True(view.Environments.Single(e => e.Kind == "sandbox").Allowed);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => oauth.ApproveAsync(admin, request, t.Environments["prod"], ["config:edit"], default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => oauth.ApproveAsync(f.As(t, "admin", agent: Guid.NewGuid()), request, t.Environments["sandbox"], ["config:edit"], default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => oauth.ApproveAsync(f.As(t, "dana"), request, t.Environments["sandbox"], ["config:edit"], default))).Status);

        var approved = Query(await oauth.ApproveAsync(admin, request, t.Environments["sandbox"], ["config:read", "config:edit", "records:read"], default));
        Assert.Equal(("s-123", oauth.Issuer), (approved["state"], approved["iss"]));
        Assert.Equal(409, (await Assert.ThrowsAsync<KernelException>(() => oauth.ApproveAsync(admin, request, t.Environments["sandbox"], ["config:edit"], default))).Status);

        // The resource must be this server; then the code and the verifier must match.
        var wrongTarget = await oauth.TokenAsync(new Dictionary<string, string?>(CodeForm(approved["code"])) { ["resource"] = "https://evil.example/mcp" }, default);
        Assert.Equal("invalid_target", Field(wrongTarget, "error"));
        var issued = await oauth.TokenAsync(CodeForm(approved["code"]), default);
        Assert.Equal(200, issued.Status);
        Assert.Equal("config:read config:edit", Field(issued, "scope"));
        var principal = tokens.Verify(Field(issued, "access_token"), PublicUrl)!;
        Assert.Equal(t.Environments["sandbox"].ToString(), principal.FindFirst(ClaimNames.Environment)!.Value);
        Assert.Equal(ClientId, principal.FindFirst(ClaimNames.Client)!.Value);
        Assert.NotEqual(t.Users["admin"].ToString(), principal.FindFirst(ClaimNames.Agent)!.Value);
        Assert.Null(tokens.Verify(Field(issued, "access_token"), Audiences.Mcp)); // bound to the canonical audience
        Assert.Null(tokens.Verify(Field(issued, "access_token"), Audiences.Api));

        // Refresh tokens rotate; using an old one again ends every token of the connection.
        var rotated = await oauth.TokenAsync(RefreshForm(Field(issued, "refresh_token")), default);
        Assert.Equal(200, rotated.Status);
        Assert.Equal("invalid_grant", Field(await oauth.TokenAsync(RefreshForm(Field(issued, "refresh_token")), default), "error"));
        Assert.Equal("invalid_grant", Field(await oauth.TokenAsync(RefreshForm(Field(rotated, "refresh_token")), default), "error"));

        // The connection is the person's, attributable to its own agent; the reuse ended it.
        var mine = Assert.Single(await oauth.ConnectionsAsync(admin, false, default));
        Assert.Equal(("ChatGPT", "chatgpt.com"), (mine.ClientName, mine.ClientHost));
        Assert.Empty(await oauth.ConnectionsAsync(f.As(t, "agent"), false, default));
        Assert.NotNull(mine.RevokedAt);
        Assert.False(await oauth.ConnectionActiveAsync(t.TenantId, mine.Id, default));
    }

    [Fact]
    public async Task Codes_are_single_use_and_revoked_or_disallowed_connections_stop_working()
    {
        var (oauth, _) = Services();

        // A live connection is active until its client revokes its refresh token (RFC 7009), which disconnects it.
        var (t0, code0) = await ConnectAsync(oauth, "oa0");
        var live = await oauth.TokenAsync(CodeForm(code0), default);
        var liveConnection = Assert.Single(await oauth.ConnectionsAsync(f.As(t0, "admin"), false, default));
        Assert.True(await oauth.ConnectionActiveAsync(t0.TenantId, liveConnection.Id, default));
        await oauth.RevokeTokenAsync(Field(live, "refresh_token"), default);
        Assert.False(await oauth.ConnectionActiveAsync(t0.TenantId, liveConnection.Id, default));
        await oauth.RevokeTokenAsync("not-a-token", default); // unknown tokens are not an error

        // A code redeemed twice was intercepted: the connection ends.
        var (t, code) = await ConnectAsync(oauth, "ob");
        Assert.Equal(200, (await oauth.TokenAsync(CodeForm(code), default)).Status);
        Assert.Equal("invalid_grant", Field(await oauth.TokenAsync(CodeForm(code), default), "error"));
        var first = Assert.Single(await oauth.ConnectionsAsync(f.As(t, "admin"), false, default));
        Assert.NotNull(first.RevokedAt);
        Assert.False(await oauth.ConnectionActiveAsync(t.TenantId, first.Id, default));

        // A wrong verifier burns the code.
        var (t2, code2) = await ConnectAsync(oauth, "oc");
        Assert.Equal("invalid_grant", Field(await oauth.TokenAsync(CodeForm(code2, Verifier + "x"), default), "error"));
        Assert.Equal("invalid_grant", Field(await oauth.TokenAsync(CodeForm(code2), default), "error"));

        // When the workspace turns external assistants off, refresh stops; revoking ends the connection and its agent.
        var (t3, code3) = await ConnectAsync(oauth, "od");
        var issued = await oauth.TokenAsync(CodeForm(code3), default);
        await f.Get<AiSettingsService>().SaveAsync(f.As(t3, "admin"), new AiSettingsRequest(null, null, 20, false, "off", 1), default);
        Assert.Equal("invalid_grant", Field(await oauth.TokenAsync(RefreshForm(Field(issued, "refresh_token")), default), "error"));
        var connection = Assert.Single(await oauth.ConnectionsAsync(f.As(t3, "admin"), true, default));
        Assert.NotNull(connection.RevokedAt); // narrowing the policy ended it
        Assert.False(await oauth.ConnectionActiveAsync(t3.TenantId, connection.Id, default));
        await Assert.ThrowsAsync<KernelException>(() => oauth.RevokeConnectionAsync(f.As(t2, "admin"), connection.Id, default)); // another workspace
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => oauth.ConnectionsAsync(f.As(t3, "builder"), true, default))).Status);
        await oauth.RevokeConnectionAsync(f.As(t3, "admin"), connection.Id, default);
        Assert.False(await oauth.ConnectionActiveAsync(t3.TenantId, connection.Id, default));
        var agentDisabled = await f.Get<Database>().InTenantWideAsync(t3.TenantId, null, db => db.McpConnections.Join(db.Principals, c => c.AgentId, p => p.Id, (c, p) => p.Disabled).FirstAsync(), default);
        Assert.True(agentDisabled);
        var audit = await f.Get<Database>().InTenantAsync(t3.TenantId, t3.Environments["sandbox"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation.StartsWith("mcp.connection_")).Select(a => a.Operation).ToListAsync(), default);
        Assert.Equal(["mcp.connection_grant", "mcp.connection_revoke"], audit.Order());
    }

    [Fact]
    public async Task Unknown_clients_wrong_redirects_and_unsafe_requests_are_refused()
    {
        var (oauth, _) = Services();
        var unknown = await oauth.AuthorizeAsync(new Dictionary<string, string?>(Authorize()) { ["client_id"] = "https://evil.example/client.json" }, "https://app/connect", default);
        Assert.Equal((null, 400), (unknown.Redirect, unknown.Status));
        var wrongRedirect = await oauth.AuthorizeAsync(Authorize(redirect: "https://chatgpt.com/other"), "https://app/connect", default);
        Assert.Equal((null, 400), (wrongRedirect.Redirect, wrongRedirect.Status)); // nothing is sent to an unregistered address

        Assert.Equal("invalid_request", Query((await oauth.AuthorizeAsync(Authorize(method: "plain"), "https://app/connect", default)).Redirect!)["error"]);
        var target = Query((await oauth.AuthorizeAsync(Authorize(resource: "https://mcp.esmbler.test/other"), "https://app/connect", default)).Redirect!);
        Assert.Equal(("invalid_target", oauth.Issuer, "s-123"), (target["error"], target["iss"], target["state"]));
        Assert.Equal("invalid_scope", Query((await oauth.AuthorizeAsync(Authorize(scope: "config:read records:write"), "https://app/connect", default)).Redirect!)["error"]);

        // A forged or expired request never reaches consent.
        var t = await f.NewTenantAsync("oe", "1.9.0");
        Assert.Equal(400, (await Assert.ThrowsAsync<KernelException>(() => oauth.ConsentAsync(f.As(t, "admin"), "eyJhbGciOiJub25lIn0.e30.", default))).Status);

        // Without a public MCP URL, OAuth is off.
        var off = new OAuthService(f.Get<PlatformOptions>(), f.Get<Database>(), new TokenService(f.Get<PlatformOptions>(), f.Get<Database>()), new FakeClients(), f.Get<ConfigStore>(), f.Get<AccessService>(), f.Get<AiSettingsService>(), f.Get<IClock>());
        Assert.Equal(404, (await off.AuthorizeAsync(Authorize(), "https://app/connect", default)).Status);
    }
}
