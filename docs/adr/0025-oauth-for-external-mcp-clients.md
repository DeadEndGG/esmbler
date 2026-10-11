---
title: "ADR 0025: OAuth for external MCP clients"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-11
requirements: [REQ-AI-003, REQ-AI-006, REQ-AISAFE-001, REQ-AISAFE-002, REQ-AISAFE-003, REQ-AISAFE-004, REQ-AUTHZ-007, REQ-IAM-001, REQ-TEN-001, REQ-TEN-003]
---

# ADR 0025: OAuth for external MCP clients

## Context and problem statement

A person who already pays for ChatGPT or Claude should be able to customize Esmbler from there: their subscription does the reasoning, and Esmbler keeps enforcing their authority. Both vendors officially support this. Claude custom connectors work on every plan, including Free. ChatGPT custom MCP servers (plugins) allow write actions on Business, Enterprise and Edu. Both connect from their own clouds to a public MCP server and authenticate with OAuth ([AI providers and subscriptions](../research/ai-providers-and-subscriptions.md), sections 2.2, 2.3 and 3.2). This is "path B"; model providers inside Esmbler are [ADR 0024](0024-multiple-model-providers.md).

Today the MCP server ([ADR 0007](0007-mcp-server-design.md)) accepts only symmetric-key JWTs with audience `esmbler-mcp`, 30 minutes, no refresh, minted by `POST /api/auth/agent-token` or by the in-app assistant ([ADR 0011](0011-identity-and-development-authentication.md)). An external host cannot obtain such a token. ADR 0007's revisit trigger ("external MCP clients need OAuth 2.1") has fired, earlier than the Gate 3 partner beta it expected.

Questions this ADR settles:

- Who is the authorization server, and how much of OAuth 2.1 do we build?
- How does a host register as a client without opening an SSRF or phishing hole?
- How do a person's consent, workspace, environment and scopes become a token the existing PDP understands?
- How long do connections live, and how are they cut off?
- What can a tenant forbid?

## Decision drivers

- The external model is untrusted: authority stays user ∩ `scp` ∩ `acts_for`, drafts only, no publish, approval or permission tools (REQ-AISAFE-001, REQ-AISAFE-004, REQ-AUTHZ-007).
- Tenant and environment come only from the token, chosen by the person at consent; no tool takes them (REQ-TEN-003, T-03).
- Interoperate with ChatGPT and Claude as they behave today: RFC 9728 discovery, PKCE S256, CIMD, `resource` and `iss`, refresh rotation.
- Every connection is attributable, visible and revocable by the person and by tenant administrators.
- Tenants decide whether their data goes to an external assistant at all; the default is conservative.
- No new internet-facing surface beyond what the flow needs: no dynamic registration, no machine-to-machine grants.
- Small enough to review in full, and a hook for tenant SSO (G2-M11).

## Considered options

1. **Esmbler as a minimal OAuth 2.1 authorization server served by `apps/Esmbler.Api`, built on `TokenService`; CIMD with an operator host allowlist; DCR off; one agent principal per connection.**
2. OpenIddict (or another server library) inside the API.
3. An external authorization server or IdP product (Keycloak, a cloud IdP) federated to Esmbler.
4. Dynamic Client Registration (RFC 7591) open to any client.
5. No OAuth: people paste a long-lived personal agent token into the host.

## Decision outcome

**Chosen option 1.**

### Roles and discovery

- **When it is on**: OAuth for external clients is on only when `Esmbler:Mcp:PublicUrl` is set **and** `Esmbler:Auth:Issuer` is an absolute `https` URL (`OAuthService.Enabled`; the hosts need both). Otherwise (local development, CI, Compose) the authorization server metadata returns `404`, `/oauth/authorize` answers `404`, the token endpoint answers `unsupported_grant_type`, and MCP tokens keep the audience `esmbler-mcp`.
- **Resource server**: `apps/Esmbler.Mcp`. `Esmbler:Mcp:PublicUrl` (for example `https://mcp.example.com/mcp`) is the **canonical resource URI**. When it is set it becomes the audience of **all** MCP tokens, the in-app assistant's and `POST /api/auth/agent-token`'s included (`TokenService.McpAudience`), so the server validates one audience. It must be set to the same value on the API and MCP hosts ([KI-062](../known-issues.md)). API tokens (`esmbler-api`) are still rejected and nothing is forwarded (T-22).
- **Protected resource metadata (RFC 9728)** on the MCP host, served only when `PublicUrl` is set, at `/.well-known/oauth-protected-resource` and `/.well-known/oauth-protected-resource/mcp`: `resource` (equal to `PublicUrl`, path included, as Claude requires), `authorization_servers` (one entry, `Esmbler:Auth:Issuer`), `scopes_supported`, `bearer_methods_supported: ["header"]` and `resource_name`.
- **The `401` challenge** is a custom `JwtBearerEvents.OnChallenge` on the MCP host (there is no SDK `McpAuthenticationHandler`): `WWW-Authenticate: Bearer resource_metadata="…", scope="config:read"`, plus `error="invalid_token"` when a token was sent and failed (RFC 6750). There is **no step-up**: no `403 insufficient_scope` and no `_meta["mcp/www_authenticate"]` on tool errors. A tool that needs a scope the token lacks returns the `scope_missing` tool error, and the person reconnects with more scopes ([KI-058](../known-issues.md)).
- **Authorization server metadata (RFC 8414)** at `/.well-known/oauth-authorization-server` on the issuer, the public web origin, whose reverse proxy forwards `/.well-known/oauth-authorization-server` and `/oauth/*` to the API (`deploy/docker/nginx.conf`): `issuer`, `authorization_endpoint`, `token_endpoint`, `revocation_endpoint`, `response_types_supported: ["code"]`, `grant_types_supported: ["authorization_code", "refresh_token"]`, `code_challenge_methods_supported: ["S256"]`, `client_id_metadata_document_supported: true`, `token_endpoint_auth_methods_supported: ["none"]`, `authorization_response_iss_parameter_supported: true`, `scopes_supported`. No `registration_endpoint`.

### Authorization server

`OAuthService` (`packages/Esmbler.Hosting/Auth`) on top of `TokenService`, with endpoints in `apps/Esmbler.Api/Endpoints/OAuthEndpoints.cs` and no OpenIddict dependency (ASM-141). Only the API host registers it (`AddEsmblerOAuthServer()`, which also registers `HttpClientMetadataSource`); the MCP host uses only the read-only `McpConnectionStatus` (is a connection still active) and `AgentScopes.ExternalGrantable` (the scopes external clients may be granted, published as `scopes_supported`).

- **Grant**: authorization code with PKCE S256 only (a 43-character challenge). `plain`, implicit, password, client credentials and device grants are not offered. Codes are 256-bit random values stored as SHA-256 hashes, single use and valid for 60 seconds. A second redemption **ends the whole connection** (connection revoked, its refresh tokens revoked, its agent disabled). A wrong verifier, client or redirect burns the code.
- **Pending requests** are not stored: `/oauth/authorize` puts the validated request in a value signed by `TokenService` (audience `esmbler-oauth-request`, 10 minutes). Each is answered once: the request ID is unique on `kernel.oauth_codes`, so a second approval is `409 request_used`.
- **Token endpoint**: `application/x-www-form-urlencoded`; public clients (`none`) only; `client_id` must match the code's or connection's client. The **`resource` parameter (RFC 8707)** is required on the authorization request and the code grant and must equal `PublicUrl` (`invalid_target`); it is **optional on the refresh grant**, which RFC 8707 allows, and checked when sent. Dead, expired or foreign refresh tokens get `invalid_grant`.
- **Issuer identification (RFC 9207)**: `iss` in every authorization response, success or error.
- **Errors** follow RFC 6749. Until the client and redirect are validated, `/oauth/authorize` answers with a plain HTML page (HTML-encoded text, `400`) and redirects nothing; afterwards errors go to the redirect with `error`, `error_description`, `state` and `iss`. Client-supplied text is never echoed into HTML. Authorization and token responses are `Cache-Control: no-store`.
- **Rate limit**: `/oauth/authorize`, `/oauth/token` and `/oauth/revoke` share their own policy, `oauth` (`LoginThrottle.OAuthPolicy`): 1,200 requests a minute per address, generous because hosts refresh for many people from few addresses (tokens are 256-bit, so guessing is not the risk). A rejection is `429` with the RFC 6749 body `{"error":"temporarily_unavailable"}`.
- **Code and refresh lookups**: the token endpoint has no session, so it finds the workspace of a code or refresh token only through two definer functions (`kernel.oauth_code_lookup`, `kernel.oauth_refresh_lookup`, migration `0020_mcp_oauth.sql`) that return just the tenant and environment for a 32-byte hash in an active workspace; everything else runs under RLS in that workspace.

### Client registration: CIMD with an allowlist

- The `client_id` is an HTTPS URL to a Client ID Metadata Document: the default port, no user info, no fragment, a path, at most 500 characters. Its host must equal or be a subdomain of a host on the operator allowlist **`Esmbler:Mcp:AllowedClientHosts`** (default `chatgpt.com` and `claude.ai`, ASM-143). The allowlist is checked again at approval and on every refresh. A **tenant-level subset is not built**: every allowlisted host may connect to a workspace whose policy is not `off` ([KI-059](../known-issues.md)).
- The document is fetched with no proxy and no redirects, connecting only when **every** resolved address is public (no loopback, private, unique local, link-local, CGNAT, multicast, NAT64 `64:ff9b::/96`, 6to4 `2002::/16` or `198.18.0.0/15`), with 5 s and 64 KB limits. Only a `200` response with content type `application/json` (or `application/client-metadata+json`) is read; its `client_id` must equal the URL; the name has control and format characters removed; it is cached for an hour.
- Redirect URIs in the document must be HTTPS, or HTTP on a loopback host (`127.0.0.1`, `[::1]`, `localhost`), at most 20. The request's `redirect_uri` must match one exactly, except that a **loopback redirect may use any port** (RFC 8252 section 7.3, native clients such as Claude Code). Nothing, not even an error, is sent to an unregistered redirect.
- **DCR is off.** It may be enabled later only for named partner clients, with rate limits and expiry (Gate 3).

### Consent

`GET /oauth/authorize` validates the client, redirect, response type, PKCE challenge, `resource` and scopes and redirects the browser to the web app's consent page, `{Esmbler:WebUrl}/connect?request=…`. The page needs a signed-in person; the consent API (`GET` and `POST /api/oauth/consent`) refuses agent tokens. If the person is not signed in, the request resumes after sign-in when they have exactly one workspace (ASM-148). The page shows:

- the client name **and its host** and the redirect host, with "Only continue if you started this from … just now";
- the **environments** of the person's current workspace, each marked allowed or not by the workspace policy; a non-production environment is preselected, and production is selectable only when the policy is `all`;
- scopes in plain language: `config:read` always; `config:edit`, `config:publish:request` and `access:explain` ticked when the client asked for them; `records:read` **unticked and only offered if the workspace allows it**; `records:write` never;
- a data notice: what the person can see will be sent to the client under their own account's terms with its provider.

Cancel returns to the Esmbler home page; no `access_denied` redirect is sent (ASM-147). Every page is served with `frame-ancestors 'none'` and `X-Frame-Options: DENY`, and the approve call carries the API bearer token (CSRF-safe). Approving reads the workspace policy under `FOR SHARE` (a concurrent settings save locks it `FOR UPDATE`, so the consent sees either the old or the new policy, and a narrowing that commits later revokes the new connection), refuses environments the policy does not allow, and needs the person's own config-edit right in the chosen environment with fresh facts. The API returns the redirect with `code`, `state` and `iss`.

```mermaid
sequenceDiagram
    autonumber
    participant P as Person
    participant H as Host (chatgpt.com or claude.ai)
    participant M as Esmbler.Mcp (resource server)
    participant A as Esmbler.Api (authorization server)
    participant W as Esmbler web (consent)
    H->>M: POST /mcp (no token)
    M-->>H: 401 WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource", scope="config:read"
    H->>M: GET /.well-known/oauth-protected-resource
    H->>A: GET /.well-known/oauth-authorization-server
    H->>P: open /oauth/authorize?client_id=CIMD URL&code_challenge=..&resource=PublicUrl&scope=..
    A->>A: host allowlisted? fetch CIMD (public addresses only), redirect_uri, S256, resource, scopes
    A->>W: redirect to /connect?request=signed pending request (10 min)
    W->>P: sign in; "ChatGPT (chatgpt.com) wants to work in Acme"
    P->>W: choose environment and scopes; Allow
    W->>A: POST /api/oauth/consent (API token, a person only)
    A->>A: policy FOR SHARE, config-edit right; create or reuse the connection and its agent
    A-->>H: redirect_uri?code=..&state=..&iss=issuer
    H->>A: POST /oauth/token (code, code_verifier, client_id, redirect_uri, resource)
    A-->>H: access token (aud = PublicUrl, sub, tid, eid, act, scp, client_id, cid, 15 min) + refresh token
    H->>M: tools/call add_field (Bearer)
    M->>M: signature, iss, aud, exp; connection active; scope; PDP user ∩ scp
    M-->>H: draft step; draftChanged reaches the Esmbler page live
```

### Connections as agent principals

Each consent creates (or, for the same person, client and environment while active, reuses) a **connection**: a row in `kernel.mcp_connections` (FORCE RLS) with its own **agent principal** (`kind = 'agent'`, `acts_for` the person, named "ChatGPT (chatgpt.com) for <person>"), the client ID, host and name, environment, granted scopes, and created, expiry, last used and revoked times. Codes (`kernel.oauth_codes`) and refresh tokens (`kernel.oauth_refresh_tokens`) are stored hashed and bound to their connection. This widens ADR 0011's "one agent per user per tenant" for external clients; the in-app assistant keeps its agent. Because each connection is an ordinary agent, the PDP (user ∩ `scp` ∩ `acts_for`), `mcp.*` audit rows with the agent, the per-agent disable switch and every existing agent restriction (no publish, no approvals, no permission ops, no alerts or audit administration) apply unchanged. Consent and revocation are audited (`mcp.connection_grant` with the client host, scopes and environment; `mcp.connection_revoke`, also when the client revokes; `mcp.refresh_reuse`).

Tokens carry `iss`, `aud` = `PublicUrl`, `sub`, `name`, `tid`, `eid`, `act`, `scp`, `client_id`, `cid` (the connection), `jti`, `nbf` and `exp`. The MCP server validates them locally and never forwards them. On every tool call `ToolRunner` checks through `McpConnectionStatus` that a token's connection is still active (not revoked or expired; `last_used_at` is updated at most once a minute) and otherwise returns the `connection_revoked` tool error.

### Tokens and revocation

| Item | Decision |
| --- | --- |
| Access token | 15 minutes (ASM-142) |
| Refresh token | 256-bit random, stored hashed, bound to the connection and client; rotated on every use; **reuse of a rotated token ends the whole connection**: the connection is revoked, all its refresh tokens are revoked and its agent is disabled (the T-51 idea, scoped to the connection); absolute lifetime 30 days from consent, then the person consents again |
| Refresh refused when | The connection is revoked or expired; the person is disabled or no longer a member of the workspace; the agent is disabled; the person no longer has config-edit rights in the environment (fresh facts); the policy is `off` or no longer allows the environment; the client host left the operator allowlist. `records:read` is **dropped** from the new token when the workspace no longer allows it |
| Revocation | RFC 7009 `revocation_endpoint`: revoking a refresh token **ends the whole connection and its agent**; unknown tokens return `200`. People revoke from the Connected assistants page; the next MCP call fails with `connection_revoked` |
| Signing | Unchanged validation path (HMAC today); production moves to asymmetric keys with a `kid` from KMS (an existing [human action](../human-actions.md)) before a public deployment. Hosts treat tokens as opaque, so no JWKS is published |

**Connected assistants page** (`/assistants` in the web app): a person sees their own connections (client and host, environment, scopes, created, last used, expiry) from `GET /api/me/connections` and revokes them; permission administrators (`permissions.admin`, fresh facts) switch to "Everyone's" (`GET /api/admin/connections`) and revoke anyone's (`POST /api/connections/{id}/revoke`). An administrator of one environment sees and revokes connections of every environment ([KI-064](../known-issues.md)). There is no member-removal path yet, so a removed person's connections end at their next refresh, and live access tokens last up to 15 minutes ([KI-063](../known-issues.md)).

### Tenant policy and limits

- **External assistants** policy per workspace: `off` (default), `non_production`, `all`; plus whether `records:read` may be granted (default no, ASM-139, ASM-140). Both are columns of `kernel.ai_settings` (ADR 0024), changed only by permission administrators with fresh facts, audited as `ai.settings_save` with previous and new values, and never by agents. **Narrowing the policy revokes the connections it no longer allows**: all of them for `off`, those in production for `non_production`, and those holding `records:read` when it is no longer allowed. There is no per-workspace list of allowed client hosts ([KI-059](../known-issues.md)).
- **Scopes**: OAuth scopes are the existing `AgentScopes`. External clients may ask only for `config:read`, `config:edit`, `config:publish:request`, `access:explain` and `records:read` (`AgentScopes.ExternalGrantable`); anything else, `records:write` included, is `invalid_scope`. `config:read` is always granted. The `records:read` workspace setting governs external assistants and the in-app assistant, not a person's own `POST /api/auth/agent-token` (ASM-146).
- **Governors**: the 200-calls-per-token budget does not fit connections that refresh every 15 minutes. External connections get per-connection limits of 60 tool calls a minute and 2,000 per UTC day instead (ASM-144), counted in memory per MCP process (ASM-149, [KI-060](../known-issues.md)); the ops-per-draft cap (300) and result size limit stay. The in-app assistant keeps its per-turn bounds. Model spend is the person's, so no Esmbler spend budget applies.
- **Tool annotations**: every tool declares `ReadOnly = true` or an explicit `Destructive` value, which the SDK publishes as `readOnlyHint` and `destructiveHint`; `ArchitectureTests.BoundaryTests.Every_mcp_tool_says_whether_it_only_reads_or_may_destroy` enforces it. Discovery and review tools are read-only; build tools are not; exactly `discard_draft`, `remove_metric`, `remove_node`, `remove_rule` and `reset_to_default` are destructive. Hosts then ask for confirmation on writes. `preview_url` is absolute when `Esmbler:WebUrl` is set on the MCP host ([KI-062](../known-issues.md)).

### Threats and mitigations

Added to the [threat model](../security/threat-model.md) as T-90 to T-100 (T-101 and T-102 cover ADR 0024's keys and customer-paid models):

| Threat | Mitigation |
| --- | --- |
| Prompt injection through record text or configuration labels, processed by a model we do not control | Server-side boundary unchanged: user ∩ `scp` ∩ `acts_for`, no publish, approval or permission tools, drafts only with human review of the diff in Esmbler, `untrusted_data` wrapping; `records:read` off by default, so most record text never reaches the host |
| Cross-connector exfiltration (the host also has mail or drive connectors and copies Esmbler data out) | Outside our control: configuration-only default scopes, the data notice at consent, a tenant policy that turns external assistants off, every tool call audited with the agent |
| Host auto-approval ("Always allow", ChatGPT auto-approve, Claude Research mode) | Writes land only in the draft and are undoable step by step; publishing needs a person in Esmbler; accurate `readOnlyHint` and `destructiveHint` keep confirmations meaningful |
| Consent phishing (an attacker sends an authorization link for their own client, or starts a flow and sends the link to a victim) | CIMD host allowlist; client name, host and redirect host on the consent page with a warning; exact `redirect_uri` match (loopback on any port); no DCR; pending requests expire in 10 minutes; non-production default; connections listed and revocable. Residual: a victim who approves an attacker-started flow on an allowlisted host connects the attacker's host account |
| Confused deputy | Esmbler issues its own tokens and proxies no third-party authorization server today. Once tenant SSO (G2-M11) sits upstream of consent, Esmbler keeps per-client consent before redirecting to the IdP, as the MCP security best practices require |
| Token theft or replay | 15-minute access tokens bound to the canonical audience; rotated refresh tokens with reuse detection that ends the connection; revocation and agent disable; a connection check on every tool call; no tokens in query strings; optional verification of OpenAI's mTLS client certificate later |
| Authorization code interception and mix-up | PKCE S256 required; `iss` in every response; single-use codes valid 60 seconds |
| SSRF when fetching CIMD documents | Host allowlist first, HTTPS only, every resolved address public (private, CGNAT, NAT64, 6to4 and `198.18.0.0/15` blocked), no proxy, no redirects, `application/json` only, size and time limits, caching |
| Wrong workspace or environment | Chosen by the person at consent and fixed in the token (`tid`, `eid`); no tool takes them; production only with explicit tenant policy |
| Data governance (data processed under the person's consumer or workspace terms with the vendor) | Tenant policy can forbid external assistants or limit them to non-production; the consent notice names the vendor; legal review is a [human action](../human-actions.md) |

### Consequences

- Good: people use the assistant they already pay for, and Esmbler's authority model applies to it exactly as to the in-app assistant.
- Good: one agent principal per connection makes every external action attributable and individually revocable.
- Good: tenants keep control: off by default, non-production before production, no record text unless they allow it.
- Good: no new dependency; the authorization server is small enough to review completely and reuses the refresh-token rotation already tested.
- Bad: we own an internet-facing OAuth implementation and its security; every RFC detail the hosts rely on is ours to keep correct.
- Bad: Esmbler needs public DNS, TLS and production signing keys before any external host can connect ([human actions](../human-actions.md)).
- Bad: the hosted claude.ai CIMD URL is not published, so the allowlist default is confirmed only by testing a real connection.
- Bad: ChatGPT and Claude change their connector rules; plan support and behavior must be rechecked before user documentation.
- Neutral: all MCP tokens move to the canonical audience when `PublicUrl` is set; local development keeps `esmbler-mcp`.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Minimal authorization server on `TokenService` | Exactly the flows the hosts use; reuses our token, refresh and agent model; no dependency; small review surface | We implement and maintain the protocol details; adding grants later is our work |
| 2. OpenIddict | Mature, standards-complete, certified building blocks | A large surface we would mostly disable; its own storage model next to `kernel.principals` and refresh tokens; CIMD and RFC 9728 support still need custom code; licence and upgrade cadence to track. Remains the fallback if the custom server grows (G2-M11 federation) |
| 3. External IdP product | Off-the-shelf OAuth and SSO | A heavy service to run (rejected for Gate 1 in ADR 0011); tenant, environment and agent claims need extensions; consent with workspace choice is ours anyway |
| 4. Open DCR | Works with any MCP client | Unbounded client registrations, a phishing and abuse surface; deprecated in the current MCP revision in favor of CIMD |
| 5. Pasted personal tokens | No OAuth to build | Neither ChatGPT nor Claude supports it for remote connectors; long-lived bearer secrets in a third party; no rotation or consent |

## More information

- Extends [ADR 0007](0007-mcp-server-design.md) (authentication, audience and governors of the MCP server; its OAuth revisit trigger) and [ADR 0011](0011-identity-and-development-authentication.md) (agent principals, tokens). Related: [ADR 0024](0024-multiple-model-providers.md), [ADR 0013](0013-ai-assistant-model-adapter.md), [research](../research/ai-providers-and-subscriptions.md).
- Assumptions: ASM-139 to ASM-144 and ASM-146 to ASM-149 in [assumptions](../decisions/assumptions.md). Known issues: [KI-058](../known-issues.md) (no step-up), KI-059 (no tenant-level host allowlist), KI-060 (per-process connection limits), KI-061 (MCP host wiring untested), KI-062 (`PublicUrl` and `WebUrl` on both hosts), KI-063 (no member-removal path), KI-064 (administrators see every environment's connections). Human actions: `Esmbler__Mcp__PublicUrl`, an HTTPS `Esmbler__Auth__Issuer`, the client host allowlist, public DNS and TLS for the MCP host and issuer, asymmetric signing keys, test accounts, WAF allowlists, directory submissions, legal review ([human actions](../human-actions.md)).
- Phases: G2-M20e shipped 2026-10-11 (authorization server, consent page `/connect`, connections and the `/assistants` page, policy, annotations; migration `0020_mcp_oauth.sql`). G2-M20f (a public host and live connections from ChatGPT and Claude, a user guide) waits on human actions.
- The spike against MCP Inspector on the C# SDK's coverage of tool `_meta`, `securitySchemes` and the `2026-07-28` stateless revision (research open question 8) was not run; step-up and `_meta` challenges are deferred with it (KI-058).
- Verification: `IntegrationTests/Assistant/McpOAuthTests` (`A_person_connects_an_assistant_to_a_sandbox_and_it_gets_tokens_for_this_server_only`: policy off, non-production only, agents and non-builders refused, single-use requests, `invalid_target`, the canonical audience, rotation and reuse; `Codes_are_single_use_and_revoked_or_disallowed_connections_stop_working`: RFC 7009, code reuse, burnt codes, narrowing the policy, revocation across workspaces, audit; `Unknown_clients_wrong_redirects_and_unsafe_requests_are_refused`: unknown clients, wrong redirects, `plain` PKCE, wrong resource, `records:write`, forged requests, OAuth off without `PublicUrl`); `UnitTests/Assistant/ClientMetadataTests` (`Only_https_metadata_documents_on_allowlisted_hosts_are_accepted`, `Metadata_documents_must_name_themselves_and_list_safe_redirects`, `Metadata_is_fetched_only_from_public_addresses`); `ArchitectureTests.BoundaryTests.Every_mcp_tool_says_whether_it_only_reads_or_may_destroy`; `Isolation.DatabaseIsolationTests.Only_allow_listed_security_definer_functions_exist`; e2e `ai.spec.ts` `@REQ-AUTHZ-007`. Not covered: the MCP host's metadata and challenge and the `connection_revoked` path through a real MCP call (KI-061). Manual acceptance from ChatGPT and Claude in G2-M20f.
- Revisit triggers: tenant SSO (G2-M11) puts an IdP upstream of consent; a partner needs DCR or Enterprise Managed Auth (Gate 3); the hosts change their OAuth requirements; the custom server needs grants beyond code and refresh.
