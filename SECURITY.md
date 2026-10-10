# Security policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately. Do not open a public issue.

- Use the repository host's private vulnerability reporting (security advisory) feature. It is the only reporting channel until a dedicated security mailbox exists; creating that mailbox is a human action.
- Include the affected component, version or commit, reproduction steps, expected and actual behavior, and impact. Say especially whether tenant isolation, authorization or secrets are involved.
- Do not access, modify or delete data that does not belong to you, and do not test against environments you do not own.

We aim to acknowledge reports within 3 business days and to agree a disclosure timeline with you. These are pre-release targets, to be confirmed before the private pilot.

## Supported versions

Esmbler is pre-release. Security fixes go to `main` and to the most recent `gate-N-complete` tag only.

| Version | Supported |
| --- | --- |
| `main` | Yes |
| Latest `gate-N-complete` tag | Yes |
| Older gate tags | No |

A versioned support window will be defined before the private pilot (Gate 2).

## Security invariants

These hold in every gate. A change that weakens one is a release blocker, and CI tests guard each of them. The full analysis is in the [threat model](docs/security/threat-model.md).

1. **Tenant and environment isolation.**
   - Every tenant table has forced row-level security, and the runtime database role neither owns tables nor has `BYPASSRLS`.
   - Tenant context is set per transaction; a missing context returns no rows.
   - Caches, jobs, events and search are scoped by tenant and environment.
2. **Explicit context.** Tenant and environment come only from the authenticated, explicitly selected session. They are never inferred from record IDs, request bodies or MCP tool arguments.
3. **Server-side enforcement.** Authorization (entity, record, field, action) and data rules are enforced on every write path: UI, API, imports, automations and AI. Hidden field values are never sent to clients or AI context.
4. **Fail closed.** If authorization cannot be established, the answer is deny.
5. **AI never exceeds its delegation.**
   - Agents act with the intersection of the user's current permissions and their delegated scopes.
   - The AI cannot publish or change permissions by itself.
   - Permission changes require `permissions.admin`.
   - Instructions embedded in records, attachments or retrieved content are data and can never authorize tool actions.
6. **Token audiences.** MCP accepts only `esmbler-mcp` tokens and the API only `esmbler-api` tokens. Tokens are never passed through to downstream services.
7. **Approvals bound to content.** Publish approval is bound to the exact artifact hash, tenant, environment and approver permissions. Any change invalidates it.
8. **No side effects from previews.** Simulations and draft previews never send notifications, call production endpoints or schedule live jobs.
9. **Secrets.** No secrets in code, logs, specs, exports or MCP responses. Secrets are references to a vault from Gate 2, and the CI secret scan blocks commits containing them.
10. **Sandboxed extensions** (from Gate 3). They run in an isolated Wasmtime sidecar with no database or vault credentials, no egress, and CPU, memory and time limits. UI extensions run in sandboxed iframes on isolated origins.
11. **Audit.** Every change records actor, agent, target, configuration revision and outcome. The audit table is append-only for the runtime role.
