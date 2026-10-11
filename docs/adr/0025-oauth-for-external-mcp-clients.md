---
title: "ADR 0025: OAuth for external MCP clients"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-11
requirements: [REQ-AI-003, REQ-AI-006, REQ-AI-007, REQ-AISAFE-001, REQ-AISAFE-002, REQ-AISAFE-003, REQ-AISAFE-004, REQ-AUTHZ-007, REQ-IAM-001, REQ-TEN-001, REQ-TEN-003]
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

1. **Esmbler as a minimal OAuth 2.1 authorization server in `apps/Esmbler.Api`, built on `TokenService`; CIMD with an operator host allowlist; DCR off; one agent principal per connection.**
2. OpenIddict (or another server library) inside the API.
3. An external authorization server or IdP product (Keycloak, a cloud IdP) federated to Esmbler.
4. Dynamic Client Registration (RFC 7591) open to any client.
5. No OAuth: people paste a long-lived personal agent token into the host.

## Decision outcome

**Chosen option 1.**

### Roles and discovery

- **Resource server**: `apps/Esmbler.Mcp`. The new setting `Esmbler:Mcp:PublicUrl` (for example `https://mcp.example.com/mcp`) is the **canonical resource URI**. It becomes the audience of **all** MCP tokens, the in-app assistant's included, so the server validates one audience. API tokens (`esmbler-api`) are still rejected and nothing is forwarded (T-22). When `PublicUrl` is not set (local development, CI), the audience stays `esmbler-mcp` and the OAuth endpoints are off.
- **Protected resource metadata (RFC 9728)** at `/.well-known/oauth-protected-resource` on the MCP host: `resource` (equal to `PublicUrl`, path included, as Claude requires), `authorization_servers` (one entry, the API issuer) and `scopes_supported`. A request without a valid token gets `401` with `WWW-Authenticate: Bearer resource_metadata="…", scope="config:read"`; a missing scope gets `403 insufficient_scope`, and tool error results also carry `_meta["mcp/www_authenticate"]` for ChatGPT. The SDK's `McpAuthenticationHandler` serves both.
- **Authorization server metadata (RFC 8414)** at `/.well-known/oauth-authorization-server` on the issuer (`Esmbler:Auth:Issuer`, the public web origin, whose reverse proxy forwards `/.well-known/oauth-authorization-server` and `/oauth/*` to the API): `issuer`, `authorization_endpoint`, `token_endpoint`, `revocation_endpoint`, `response_types_supported: ["code"]`, `grant_types_supported: ["authorization_code", "refresh_token"]`, `code_challenge_methods_supported: ["S256"]`, `client_id_metadata_document_supported: true`, `token_endpoint_auth_methods_supported: ["none"]`, `authorization_response_iss_parameter_supported: true`, `scopes_supported`. No `registration_endpoint`.

### Authorization server

A minimal implementation in `apps/Esmbler.Api` (endpoints) on top of `TokenService` (`packages/Esmbler.Hosting/Auth`), with no OpenIddict dependency (ASM-141):

- **Grant**: authorization code with PKCE S256 only. `plain`, implicit, password, client credentials and device grants are not offered. Codes are random, stored hashed, single use and valid for 60 seconds; a second redemption revokes everything issued from the first.
- **Token endpoint**: `application/x-www-form-urlencoded`; public clients (`none`) only. The **`resource` parameter (RFC 8707) is required** on the authorization and token requests and must equal `PublicUrl`; anything else is `invalid_target`. Dead refresh tokens get `invalid_grant`.
- **Issuer identification (RFC 9207)**: `iss` in every authorization response, success or error.
- **Errors** follow RFC 6749 and never echo client-supplied text into HTML.
- Discovery and token responses stay well inside the hosts' timeouts (10 s; 30 s for refresh).

### Client registration: CIMD with an allowlist

- The `client_id` is an HTTPS URL to a Client ID Metadata Document. The server accepts it only if its host is on the operator allowlist (`Esmbler:Mcp:OAuth:AllowedClientHosts`, default `chatgpt.com` and `claude.ai`, ASM-143), narrowed further by tenant policy at consent.
- The document is fetched over HTTPS only, through the egress path with private, loopback and link-local ranges blocked (also after redirects, which are not followed), with a 5 s timeout and a 64 KB limit, and cached. Its `client_id` must equal the URL, and the request's `redirect_uri` must match one of its `redirect_uris` exactly. Loopback redirects (Claude Code) are accepted only when the document itself declares them.
- **DCR is off.** It may be enabled later only for named partner clients, with rate limits and expiry (Gate 3).

### Consent

`GET /oauth/authorize` validates the client, redirect, PKCE challenge, `resource` and scopes, stores a pending request (random ID, 10 minutes) and redirects the browser to the web app's consent page. The page requires an **interactive session** (password today, tenant SSO after G2-M11); agent tokens are refused. It shows:

- the client name **and its host** and the redirect host, with "Only continue if you started this from ChatGPT or Claude just now";
- a **workspace and environment** the person chooses from their memberships: a sandbox by default, production only if tenant policy allows it;
- scopes in plain language: `config:read` always, `config:edit`, `config:publish:request` and `access:explain` ticked, `records:read` **unticked and only offered if tenant policy allows it**, `records:write` never;
- a data notice: "Configuration you can see will be sent to OpenAI (or Anthropic) under your own account's terms."

The page is served with `frame-ancestors 'none'`, the approve call carries the API bearer token (CSRF-safe), and the request ID is single use. Approving needs config-edit rights in the chosen environment; the API returns the redirect with `code`, `state` and `iss`.

```mermaid
sequenceDiagram
    autonumber
    participant P as Person
    participant H as Host (chatgpt.com or claude.ai)
    participant M as Esmbler.Mcp (resource server)
    participant A as Esmbler.Api (authorization server)
    participant W as Esmbler web (consent)
    H->>M: POST /mcp (no token)
    M-->>H: 401 WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource"
    H->>M: GET /.well-known/oauth-protected-resource
    H->>A: GET /.well-known/oauth-authorization-server
    H->>P: open /oauth/authorize?client_id=CIMD URL&code_challenge=..&resource=PublicUrl&scope=..
    A->>A: client host allowlisted? fetch CIMD (HTTPS, no private ranges), exact redirect_uri, S256, resource
    A->>W: redirect to consent (pending request, 10 min)
    W->>P: sign in; "ChatGPT (chatgpt.com) wants to edit the draft of Acme / Sandbox"
    P->>W: choose workspace, environment, scopes; approve
    W->>A: approve (API token, interactive session only)
    A->>A: tenant policy allows? create or reuse the connection's agent principal
    A-->>H: redirect_uri?code=..&state=..&iss=issuer
    H->>A: POST /oauth/token (code, code_verifier, resource)
    A-->>H: access token (aud = PublicUrl, sub, tid, eid, act, scp, client_id, 15 min) + refresh token
    H->>M: tools/call add_field (Bearer)
    M->>M: signature, iss, aud, exp; agent and connection enabled; PDP user ∩ scp
    M-->>H: draft step; draftChanged reaches the Esmbler page live
```

### Connections as agent principals

Each consent creates (or, for the same person, client, tenant and environment, reuses) a **connection**: a row in a new tenant-scoped table (FORCE RLS) with its own **agent principal** (`act`, `kind = 'agent'`, `acts_for` the person), the client ID and host, environment, granted scopes, created, last used and revoked times. This widens ADR 0011's "one agent per user per tenant" for external clients; the in-app assistant keeps its agent. Because each connection is an ordinary agent, the PDP (user ∩ `scp` ∩ `acts_for`), `mcp.*` audit rows with the agent, the per-agent disable switch and every existing agent restriction (no publish, no approvals, no permission ops, no alerts or audit administration) apply unchanged. Consent, scope changes and revocation are audited (`mcp.connection_grant`, `mcp.connection_revoke`).

Tokens carry `iss`, `aud` = `PublicUrl`, `sub`, `tid`, `eid`, `act`, `scp`, `client_id`, `jti`, `iat`, `exp`. The MCP server validates them locally and never forwards them.

### Tokens and revocation

| Item | Decision |
| --- | --- |
| Access token | 15 minutes (ASM-142) |
| Refresh token | 256-bit random, stored hashed, bound to the connection; rotated on every use; reuse of a rotated token revokes the connection's whole token family (the T-51 claim machinery, scoped to the connection rather than all of the person's sessions); absolute lifetime 30 days from consent, then the person consents again |
| Refresh refused when | The person is disabled, loses membership of the tenant, loses config-edit rights in the environment, the agent is disabled, the connection is revoked, or tenant policy no longer allows the client or environment |
| Revocation | RFC 7009 `revocation_endpoint` (unknown tokens return 200). Revoking a connection disables its agent and revokes its token family; the next MCP call fails |
| Signing | Unchanged validation path; production moves to asymmetric keys with a `kid` from KMS (an existing [human action](../human-actions.md)) before a public deployment. Hosts treat tokens as opaque, so no JWKS is published |

**Connected assistants page** (web): a person sees their own connections (client and host, workspace, environment, scopes, last used) and revokes them; tenant administrators (`permissions.admin`) see and revoke all of the tenant's. Removing a member revokes their connections.

### Tenant policy and limits

- **External assistants** policy per tenant: `off` (default), `non_production`, `all`; plus the allowed client hosts (a subset of the operator allowlist) and whether `records:read` may be granted (default no, ASM-139, ASM-140). It is stored with the tenant AI settings of ADR 0024 (`kernel.ai_settings`), changed only by permission administrators with fresh facts, audited, and never by agents. Narrowing the policy revokes connections it no longer allows.
- **Scopes**: OAuth scopes are the existing `AgentScopes`. `records:write`, and anything that would map to `config.publish`, approvals or `permissions.admin`, is never grantable to an external client. A tool that needs a scope the token lacks returns a step-up challenge.
- **Governors**: the 200-calls-per-token budget does not fit connections that refresh every 15 minutes. External connections get per-connection limits per minute and per day instead (ASM-144); the ops-per-draft cap (300) and result size limit stay. The in-app assistant keeps its per-turn bounds. Model spend is the person's, so no Esmbler spend budget applies.
- **Tool annotations**: every tool declares `readOnlyHint` and `destructiveHint` accurately. Discovery and review tools are read-only; build tools are not; `discard_draft`, `remove_node`, `remove_rule`, `remove_metric` and `reset_to_default` are destructive. Hosts then ask for confirmation on writes. `preview_url` returns an absolute URL for external clients.

### Threats and mitigations

New rows for the [threat model](../security/threat-model.md), added in G2-M20e:

| Threat | Mitigation |
| --- | --- |
| Prompt injection through record text or configuration labels, processed by a model we do not control | Server-side boundary unchanged: user ∩ `scp` ∩ `acts_for`, no publish, approval or permission tools, drafts only with human review of the diff in Esmbler, `untrusted_data` wrapping; `records:read` off by default, so most record text never reaches the host |
| Cross-connector exfiltration (the host also has mail or drive connectors and copies Esmbler data out) | Outside our control: configuration-only default scopes, the data notice at consent, a tenant policy that turns external assistants off, every tool call audited with the agent |
| Host auto-approval ("Always allow", ChatGPT auto-approve, Claude Research mode) | Writes land only in the draft and are undoable step by step; publishing needs a person in Esmbler; accurate `readOnlyHint` and `destructiveHint` keep confirmations meaningful |
| Consent phishing (an attacker sends an authorization link for their own client, or starts a flow and sends the link to a victim) | CIMD host allowlist; client name, host and redirect host on the consent page with a warning; exact `redirect_uri` match; no DCR; pending requests expire in 10 minutes; non-production default; connections listed and revocable. Residual: a victim who approves an attacker-started flow on an allowlisted host connects the attacker's host account |
| Confused deputy | Esmbler issues its own tokens and proxies no third-party authorization server today. Once tenant SSO (G2-M11) sits upstream of consent, Esmbler keeps per-client consent before redirecting to the IdP, as the MCP security best practices require |
| Token theft or replay | 15-minute access tokens bound to the canonical audience; rotated refresh tokens with reuse detection; revocation and agent disable; no tokens in query strings; optional verification of OpenAI's mTLS client certificate later |
| Authorization code interception and mix-up | PKCE S256 required; `iss` in every response; single-use codes valid 60 seconds |
| SSRF when fetching CIMD documents | Host allowlist first, HTTPS only, private ranges blocked, no redirects, size and time limits, caching |
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
- Assumptions: ASM-139 to ASM-144 in [assumptions](../decisions/assumptions.md). Human actions: public DNS and TLS for the MCP host and issuer, asymmetric signing keys, test accounts, WAF allowlists, directory submissions, legal review ([human actions](../human-actions.md)).
- Phases: G2-M20e (authorization server, consent, connections, policy, annotations), G2-M20f (live connection from ChatGPT and Claude on a public host).
- Before G2-M20e: a spike against MCP Inspector on the C# SDK's coverage of tool `_meta`, `securitySchemes` and the `2026-07-28` stateless revision (research open question 8).
- Planned verification: integration tests with a fake CIMD client for the full flow; reuse detection; a revoked connection cut off at its next call; wrong audience, missing `resource` and `plain` PKCE rejected; a non-allowlisted or private-address client refused; production blocked by policy; `records:write` never grantable; refresh refused after membership loss; tool annotations checked for every tool. Manual acceptance from ChatGPT and Claude in G2-M20f.
- Revisit triggers: tenant SSO (G2-M11) puts an IdP upstream of consent; a partner needs DCR or Enterprise Managed Auth (Gate 3); the hosts change their OAuth requirements; the custom server needs grants beyond code and refresh.
