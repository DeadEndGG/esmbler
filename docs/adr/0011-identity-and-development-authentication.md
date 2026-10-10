---
title: "ADR 0011: Identity and development authentication"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-IAM-001, REQ-IAM-002, REQ-IAM-003, REQ-TEN-003, REQ-AUTHZ-006, REQ-AUTHZ-007, REQ-AISAFE-003, REQ-AISAFE-004]
---

# ADR 0011: Identity and development authentication

## Context and problem statement

Gate 1 needs real authentication so isolation and authorization tests mean something, but enterprise SSO (OIDC, SAML, SCIM, MFA, per-tenant IdPs) is Gate 2 and requires IdP registration by a human. A person may belong to several tenants; tenant and environment switching must be explicit and authorized, never inferred from record IDs or tool arguments. AI agents are first-class, auditable principals that act for a user with delegated scopes and need tokens for the MCP server only.

## Decision drivers

- Explicit, auditable tenant/environment selection carried in the token.
- Separate token audiences for the API and MCP; no passthrough.
- Short-lived tokens so revocation has bounded staleness.
- An identity model that will accept external IdPs in Gate 2 without data migration.

## Considered options

1. **Own token issuer in the API: local passwords now, external IdPs federated in Gate 2.**
2. Keycloak (or another IdP container) from Gate 1.
3. ASP.NET Core Identity cookies only.

## Decision outcome

**Chosen option 1.** Status: implemented (G1-M2, hardened after the Gate 1 security review). Token issuing is `packages/Esmbler.Hosting/Auth/TokenService.cs`; endpoints are `apps/Esmbler.Api/Endpoints/AuthEndpoints.cs`; identity data lives in `0001_kernel.sql`, refresh tokens in `0002_refresh_tokens.sql` and `0004_refresh_session_cap.sql`.

### Data model

- `kernel.principals (id, kind: user | agent | service | module, email, display_name, password_hash, acts_for, agent_tenant, disabled)`: global, not tenant-scoped (one person, many tenants). `password_hash` is PBKDF2 via ASP.NET Core `PasswordHasher<T>` (dev and pilot only). An agent is a principal with `kind = 'agent'`, `acts_for` its user and `agent_tenant` its tenant: one agent per user per tenant. Login reads the hash through the `SECURITY DEFINER` function `kernel.login_lookup` ([ADR 0010](0010-database-migrations-and-roles.md)).
- `kernel.memberships (tenant_id, principal_id, title)`: RLS by tenant; a principal also sees its own memberships (tenant switcher).
- `kernel.groups`, `kernel.group_members` (nested groups through `member_group`) and `kernel.role_assignments` (to a principal or a group): the system of record for identity relations; the tenant's OpenFGA store is derived from them ([ADR 0004](0004-authorization-engine.md)).
- Later: `kernel.identity_links (principal_id, issuer, subject)` for OIDC/SAML (Gate 2).

Groups and role assignments are tenant-wide; configuration and records are per environment ([assumptions](../decisions/assumptions.md)).

### Tokens

JWT access tokens (HMAC-SHA256), 15 minutes (`Esmbler:Auth:AccessTokenMinutes`), signed with `Esmbler:Auth:SigningKey` (environment variable `Esmbler__Auth__SigningKey`, at least 32 bytes). Docker Compose supplies a public development-only key starting with `dev-only`; every host refuses to start with it unless `Esmbler:Auth:AllowDevelopmentKey=true` (Compose sets it for local use). The production key is a [human action](../human-actions.md).

API token after selecting a workspace:

```json
{
  "iss": "esmbler", "aud": "esmbler-api",
  "sub": "6a0f…", "name": "Alex",
  "tid": "6f1c…", "eid": "a7d2…",
  "jti": "0192f2…", "iat": 1791640000, "exp": 1791640900
}
```

Agent token for MCP (minted by the API for the in-app assistant or through `POST /api/auth/agent-token`, see [ADR 0007](0007-mcp-server-design.md)), 30 minutes (`Esmbler:Auth:AgentTokenMinutes`):

```json
{
  "iss": "esmbler", "aud": "esmbler-mcp",
  "sub": "6a0f…", "name": "Alex", "tid": "6f1c…", "eid": "a7d2…",
  "act": "9b3e…",
  "scp": "config:read config:edit config:publish:request access:explain",
  "jti": "0192f3…", "iat": 1791640000, "exp": 1791641800
}
```

`act` is the agent principal (one per user per tenant, created on first use). Scopes must come from `AgentScopes.All` (`config:read`, `config:edit`, `config:publish:request`, `records:read`, `records:write`, `access:explain`); the default (`AgentScopes.Builder`) has no record access (threat model T-23). The agent's effective rights are the user's permissions intersected with these scopes, evaluated per call. Permissions are not in tokens; the PDP evaluates them per request, so a role change applies at the next request ([ADR 0004](0004-authorization-engine.md)).

### Refresh tokens and sign-in protection

- Refresh tokens are 256-bit random values, stored as hashes in `kernel.refresh_tokens` (RLS: owner only; looked up by hash through `kernel.refresh_lookup`). They travel only in the `esmbler_refresh` cookie: `HttpOnly`, `SameSite=Strict`, `Path=/api/auth`, `Secure` by default (`Esmbler:Auth:SecureCookies`, true unless turned off), 8-hour lifetime.
- `/api/auth/refresh` and `/api/auth/logout` require the `X-Esmbler-Csrf` header (a custom header cannot be sent cross-site without a CORS preflight, which the API never grants).
- Rotation claims the old token atomically: of two concurrent redemptions only one gets a successor; the other, like any reuse of a rotated token, revokes all of that principal's refresh tokens. A session ends 24 hours after sign-in whatever the rotations (`session_started_at`). Refresh refuses disabled accounts and workspaces the person no longer belongs to.
- Sign-in is rate-limited per client address (`Esmbler:Auth:LoginAttemptsPerMinute`, default 30; refresh allows ten times that), and an account is locked for 15 minutes after 8 failed passwords (`Esmbler:Auth:FailedLoginsPerAccount`) from any address.
- The API honors `X-Forwarded-For`/`X-Forwarded-Proto` only when `Esmbler:Auth:TrustForwardedHeaders` is true (Compose sets it, because browsers reach the API through the web container's nginx).

### Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` | email + password → token with no tenant (`tid` absent), the refresh cookie and the list of workspaces |
| `POST /api/auth/switch` | `{tenant, environment?}` → new token after membership and environment checks (agents refused) |
| `POST /api/auth/refresh` | rotate the refresh cookie; re-checks disabled flag and membership |
| `POST /api/auth/logout` | revoke the refresh token and clear the cookie |
| `POST /api/auth/agent-token` | `{scopes?}` → `esmbler-mcp` token for the caller's agent in the current workspace (agents refused) |
| `POST /api/admin/agents/{agentId}/disable` | kill switch (the agent's own user, or anyone with `permissions.admin`): disables the agent principal; MCP rejects its next call |

### Tenant switch flow

```mermaid
sequenceDiagram
    participant U as Browser
    participant A as API
    participant DB as PostgreSQL
    U->>A: POST /api/auth/login (alex, password)
    A-->>U: token (no tid) + memberships [acme: prod, sandbox; globex: sandbox]
    U->>A: POST /api/auth/switch {tenant: acme, environment: sandbox}
    A->>DB: membership(alex, acme) active? environment belongs to acme?
    A-->>U: token tid=acme, eid=sandbox (15 min) + refresh cookie bound to them
    Note over U,A: Every request uses tid/eid from the token only. A record ID from another tenant is simply not found (RLS).
```

A token without `tid` can only call login, switch, refresh and profile endpoints. The MCP server requires `tid` and `eid`.

### Consequences

- Good: no external IdP to run in Gate 1; the principal model already separates identity from membership for Gate 2 federation.
- Good: the token states exactly one tenant/environment.
- Bad: switching is not yet written to the audit log (the master spec asks for auditable switching).
- Bad: we issue tokens ourselves; key rotation (`kid` header, two active keys) must be built before production.
- Bad: local passwords are not acceptable for production tenants; SSO is required before the private pilot goes live (Gate 2).

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| Own issuer, local passwords | Small; full control over `tid`/`eid`/`act` claims | We own token security; SSO later |
| Keycloak from Gate 1 | Standards-complete | Heavy container; custom claims for tenant switch and agents need extensions; slows tests |
| Identity cookies only | Simple for the web app | No bearer tokens for MCP; CSRF surface; no audience separation |

## More information

- Verification: `Api.ApiTests` (`Switching_to_a_tenant_you_do_not_belong_to_is_forbidden`, `Requests_without_a_selected_workspace_are_refused`, `Refresh_rotates_and_detects_reuse`, `Agent_tokens_are_rejected_by_the_api_audience`); `Api.SecurityHardeningTests` (`Sign_in_is_rate_limited_per_address`, `Repeated_wrong_passwords_lock_the_account_temporarily`, `Concurrent_redemption_of_one_refresh_token_yields_at_most_one_successor`, `Builder_delegation_has_no_record_access_by_default`, `Hosts_refuse_the_public_development_signing_key_unless_allowed`); `Mcp.McpServerTests` (`Api_tokens_are_rejected_by_the_mcp_server`, `Disabling_the_agent_stops_its_next_call`, `Scopes_limit_the_agent_and_errors_are_structured`).
- Revisit triggers: Gate 2 SSO (per-tenant OIDC/SAML/SCIM) — the issuer may become a federation broker or be replaced by an IdP product, via a new ADR.
