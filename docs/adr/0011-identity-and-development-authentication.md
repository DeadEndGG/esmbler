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

**Chosen option 1.** Status: tokens, login, tenant switching and agent tokens are planned for G1-M2. The identity tables below exist in `0001_kernel.sql`; refresh-token storage and the agent's allowed scopes need a later migration.

### Data model

- `kernel.principals (id, kind: user | agent | service | module, email, display_name, password_hash, acts_for, agent_tenant, disabled)`: global, not tenant-scoped (one person, many tenants). `password_hash` is PBKDF2 via ASP.NET Core `PasswordHasher<T>` (dev and pilot only). An agent is a principal with `kind = 'agent'`, `acts_for` its user and `agent_tenant` its tenant: one agent per user per tenant. Login reads the hash through the `SECURITY DEFINER` function `kernel.login_lookup` ([ADR 0010](0010-database-migrations-and-roles.md)).
- `kernel.memberships (tenant_id, principal_id, title)`: RLS by tenant; a principal also sees its own memberships (tenant switcher).
- `kernel.groups`, `kernel.group_members` (nested groups through `member_group`) and `kernel.role_assignments` (to a principal or a group): the system of record for identity relations; the tenant's OpenFGA store is derived from them ([ADR 0004](0004-authorization-engine.md)).
- Later: `kernel.identity_links (principal_id, issuer, subject)` for OIDC/SAML (Gate 2).

Groups and role assignments are tenant-wide; configuration and records are per environment ([assumptions](../decisions/assumptions.md)).

### Tokens

JWT access tokens, 15 minutes, signed with a key from configuration (`Esmbler:Auth:SigningKey`, environment variable `Esmbler__Auth__SigningKey`; Docker Compose supplies a development-only default, the production key is a [human action](../human-actions.md)). Refresh tokens are opaque, stored hashed, rotated on use, 8-hour lifetime.

API token after selecting tenant `acme`, environment `sandbox`:

```json
{
  "iss": "esmbler", "aud": "esmbler-api",
  "sub": "user:6a0f…", "name": "Alex",
  "tid": "tenant:6f1c…", "eid": "env:a7d2…", "ten": "acme", "env": "sandbox",
  "authz_rev": 42,
  "jti": "0192f2…", "iat": 1791640000, "exp": 1791640900
}
```

Agent token for MCP (minted by the API, see [ADR 0007](0007-mcp-server-design.md)):

```json
{
  "iss": "esmbler", "aud": "esmbler-mcp",
  "sub": "user:6a0f…", "tid": "tenant:6f1c…", "eid": "env:a7d2…",
  "act": { "sub": "agent:assistant-alex" },
  "scp": "config:read config:edit config:publish:request records:read",
  "jti": "0192f3…", "iat": 1791640000, "exp": 1791641800
}
```

Permissions are not in tokens; the PDP evaluates them per request, so a role change applies at the next request. `authz_rev` is informational (cache key hint).

### Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` | email + password → token with no tenant (`tid` absent) and the list of memberships |
| `POST /api/auth/switch` | `{tenant, environment}` → new token after membership and environment checks; audited |
| `POST /api/auth/refresh` | rotate refresh token; re-checks membership and disabled flags |
| `POST /api/auth/agent-token` | `{scopes, ttlMinutes ≤ 30}` → `esmbler-mcp` token; scopes must be a subset of the agent's `allowed_scopes` |
| `POST /api/agents/{id}/disable` | kill switch: disables the agent principal; MCP rejects its tokens at the next call |

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
    A->>DB: INSERT audit (tenant.switch)
    A-->>U: token tid=acme, eid=sandbox (15 min)
    Note over U,A: Every request uses tid/eid from the token only. A record ID from another tenant is simply not found (RLS).
```

A token without `tid` can only call login, switch, refresh and profile endpoints. The MCP server requires `tid` and `eid`.

### Consequences

- Good: no external IdP to run in Gate 1; the principal model already separates identity from membership for Gate 2 federation.
- Good: the token states exactly one tenant/environment; switching produces an audit row.
- Bad: we issue tokens ourselves; key rotation (`kid` header, two active keys) must be built before production.
- Bad: local passwords are not acceptable for production tenants; SSO is required before the private pilot goes live (Gate 2).

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| Own issuer, local passwords | Small; full control over `tid`/`eid`/`act` claims | We own token security; SSO later |
| Keycloak from Gate 1 | Standards-complete | Heavy container; custom claims for tenant switch and agents need extensions; slows tests |
| Identity cookies only | Simple for the web app | No bearer tokens for MCP; CSRF surface; no audience separation |

## More information

- Verification (planned): `Integration.Auth.Switch_to_non_member_tenant_is_forbidden`, `Integration.Auth.Token_without_tid_cannot_read_records`, `Integration.Auth.Disabled_agent_token_rejected_by_mcp`, `Integration.Auth.Agent_scopes_cannot_exceed_allowed_scopes`.
- Revisit triggers: Gate 2 SSO (per-tenant OIDC/SAML/SCIM) — the issuer may become a federation broker or be replaced by an IdP product, via a new ADR.
