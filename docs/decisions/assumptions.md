---
title: Assumptions
status: living
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-TEN-001, REQ-TEN-006, REQ-REC-001, REQ-REC-002, REQ-RULE-003, REQ-AUTHZ-001, REQ-EXT-001, REQ-AISAFE-003, REQ-IAM-003, REQ-OPS-006]
---

# Assumptions

Defaults chosen instead of asking a human (execution brief, section 1). Each row is treated as approved until evidence or the user says otherwise. Changing an expensive one needs an ADR; see [ADR 0001](../adr/0001-record-architecture-decisions.md).

Sources: **EB §2** = execution brief section 2 defaults; **Decision** = Gate 0 architecture decision brief; **ADR NNNN** = a default set while writing that ADR.

## Product and platform defaults (execution brief section 2)

| ID | Assumption | Source | Impact | How / when to revisit |
|---|---|---|---|---|
| ASM-001 | First buyer: internal IT service desks at companies with 200–5,000 employees, one organization per tenant; MSP mode later | EB §2 | Tenant = one organization; no tenant hierarchy in Gate 1–3 | User feedback after Gate 2 pilot; MSP demand opens an ADR on tenant hierarchy |
| ASM-002 | First workflow: intake → triage → assignment → optional approval and fulfillment → resolution → requester confirmation | EB §2 | Tickets 1.x lifecycle and Gate 2 approvals follow this flow | Gate 2 pilot test guide results |
| ASM-003 | Pilot scale: 10 tenants, up to 2,000 users and 500,000 tickets per tenant, up to 200 custom fields per entity | EB §2 | Sizes query limits, indexes and job fairness ([ADR 0002](../adr/0002-storage-and-query-model.md), [ADR 0005](../adr/0005-job-and-workflow-execution.md)) | Load test at pilot scale in Gate 2; list p95 > 300 ms triggers ADR 0002 revisit |
| ASM-004 | Hosting: one cloud provider, one region, pooled multi-tenancy; everything containerized (Docker images, Compose, Kubernetes-ready manifests) | EB §2 | One database, RLS isolation; dedicated tier later (REQ-TEN-007) | Cloud account choice is a [human action](../human-actions.md); data-residency demand |
| ASM-005 | Backend C# on .NET 10 LTS (ASP.NET Core); frontend Angular (22, standalone); one repository | EB §2 | Stack for all gates ([ADR 0012](../adr/0012-repository-layout-and-module-boundaries.md)) | Next .NET LTS / Angular major at each gate start |
| ASM-006 | PostgreSQL (18) with row-level security; full-text search in Postgres until a search engine is justified | EB §2 | `tsvector` search on shared records table | Search latency or relevance complaints; Gate 4 advanced reports |
| ASM-007 | Postgres-backed durable job queue and workflow timers first; .NET options and workflow engines evaluated in an ADR | EB §2 | Own outbox/jobs ([ADR 0005](../adr/0005-job-and-workflow-execution.md)) | Gate 2 approvals/SLA timers; oldest event age p95 > 30 s |
| ASM-008 | Authorization engine chosen among OpenFGA, SpiceDB, Cedar by spike | EB §2 | OpenFGA chosen ([ADR 0004](../adr/0004-authorization-engine.md)) | ADR 0004 revisit triggers |
| ASM-009 | Extension runtime: WebAssembly components, one guest toolchain first, sidecar if .NET host support is insufficient; Gate 3 | EB §2 | Rust sidecar on Wasmtime ([ADR 0006](../adr/0006-extension-runtime.md)) | Gate 3 start; WASI 0.3 maturity |
| ASM-010 | AI: Claude via the Anthropic API; the platform exposes its own MCP server for the builder | EB §2 | [ADR 0007](../adr/0007-mcp-server-design.md), [ADR 0013](../adr/0013-ai-assistant-model-adapter.md) | Evaluation-suite results; regional processing requirements |
| ASM-011 | No regulated data (health, payment card) in the pilot | EB §2 | No HIPAA/PCI controls in Gates 1–2; rich text and attachments still sanitized and authorized | Any customer requiring regulated data blocks onboarding until an ADR |
| ASM-012 | Email, payments and object storage behind adapters with local fakes | EB §2 | Fakes in dev/CI; real providers are [human actions](../human-actions.md) | When credentials are provided per gate |

## Architecture decision brief

| ID | Assumption | Source | Impact | How / when to revisit |
|---|---|---|---|---|
| ASM-013 | Choice option IDs are strings (stable IDs like `"access"`); the master spec's "category == 8" becomes `record.category == 'access'` | Decision | None functionally; labels can change without breaking rules | Never, unless numeric codes are required by an importer (map them) |
| ASM-014 | One number type (IEEE double) in expressions | Decision | Integer/decimal distinction enforced by field validation, not by the language ([ADR 0009](../adr/0009-rule-expression-language.md)) | Currency arithmetic in rules needing exact decimals (Gate 2+) |
| ASM-015 | One open draft per tenant environment in Gate 1 ("one builder at a time with concurrency protection"); real-time multi-builder collaboration in Gate 4 | Decision | Version checks, not merges ([ADR 0003](../adr/0003-configuration-artifact-and-overlay-format.md)) | Gate 4 collaboration ADR |
| ASM-016 | Groups/teams and role assignments are tenant-wide identity data (not per environment); configuration and records are per environment | Decision | Tenant-only RLS policy on identity tables ([ADR 0010](../adr/0010-database-migrations-and-roles.md)); one OpenFGA store per tenant ([ADR 0004 refinement](../adr/0004-authorization-engine.md#refinement-2026-10-10)) | Customers needing sandbox-only groups |
| ASM-017 | Gate 1 notifications go to an in-app fake outbox; email delivery arrives in Gate 2 | Decision | Notification templates testable without email | Gate 2 |
| ASM-018 | Development and pilot authentication use local passwords; SSO in Gate 2 | Decision | [ADR 0011](../adr/0011-identity-and-development-authentication.md) | Gate 2 (IdP registration is a human action) |
| ASM-019 | Seed data: users alex (tenant admin + agent), sam (agent), dana, jordan, riley (requesters); groups `service_desk`, `network`; tenants "acme" and "globex", each with environments `prod` and `sandbox` | Decision | Fixtures for tests and gate test guides | When test guides need more personas |
| ASM-020 | Experience targets (simple change < 10 s p50; AI change visible < 1 s p95) are proposed, not approved; measured from Gate 1 | Decision | Reported as measured vs proposed, never as "met" without evidence | User confirmation (human action) |
| ASM-021 | AI model ID defaults to `claude-opus-5-5`, configurable | Decision | `Esmbler:Assistant:Model` setting | Evaluation-suite cost/quality results |
| ASM-022 | Docker Compose is the deployment target for local and pilot; Kubernetes manifests provided but not exercised in CI | Decision | Manifests may drift | Before any Kubernetes deployment; add a kind-based CI job |

## Defaults set in Gate 0 ADRs

| ID | Assumption | Source | Impact | How / when to revisit |
|---|---|---|---|---|
| ASM-023 | Query limits: page ≤ 100, ≤ 64 filter nodes, one sort key, offset ≤ 10,000, exact counts up to 10,000, 5 s statement timeout, no relationship traversal in Gate 1 filters | ADR 0002 | Lists page by offset; deeper pages need a narrower filter; counts may show "10000+" | Pilot load tests; report needs in Gate 2 |
| ASM-024 | A field can be full-text searchable only if no permission set hides it; search uses the `simple` text configuration (no stemming) | ADR 0002 | No search leaks of hidden fields; weaker relevance for English | Relevance feedback; multilingual tenants |
| ASM-025 | Dates and instants are normalized ISO-8601 strings in records and expressions; no timestamp/duration type in Gate 1 | ADR 0002, ADR 0009 | Date comparisons work; date arithmetic does not | Gate 2 SLA rules |
| ASM-026 | Record conflicts are not merged server-side; the client re-applies non-overlapping edits | ADR 0002 | Simpler, explicit conflicts | Users reporting frequent conflicts |
| ASM-027 | Retention windows: idempotency keys 24 h; publish approvals expire after 24 h; soft-deleted records restorable for 30 days | ADR 0002, ADR 0003 | Bounded table growth; stale approvals cannot be used | Gate 2 retention policies |
| ASM-028 | API errors use RFC 9457 `application/problem+json` with `code`, `target`, `suggestion` extensions; MCP tool errors use `{code, message, target, suggestion}` | ADR 0003, ADR 0007 | One error vocabulary across REST and MCP | Generated OpenAPI review |
| ASM-029 | Job governors: one running job per tenant at a time (`kernel.claim_jobs`), max 5 attempts by default, backoff 5 s × 2ⁿ capped at 15 min, causation depth 8, fan-out 100 | ADR 0005 | Tenant fairness at pilot scale | Gate 2 load tests (REQ-ACC-11) |
| ASM-030 | MCP governors: 200 tool calls per session, 60 per minute per agent token, 300 ops per draft, 64 KB result size, agent tokens ≤ 30 min | ADR 0007 | AI sessions bounded; long sessions re-mint tokens | Evaluation suite; user complaints about limits |
| ASM-031 | Assistant bounds: 30 tool calls and 15 model requests per turn, 120 s per turn, 60 s per model request, 16k output tokens, last 12 turns of history, 30 messages per minute per person; per-tenant daily spend budget planned (see [known issues](../known-issues.md)) | ADR 0013 | Predictable cost and latency | Evaluation-suite cost per successful customization |
| ASM-032 | Access tokens 15 min; refresh tokens opaque, hashed, rotated, 8 h, within an absolute 24 h session (sign in again daily) | ADR 0011 | Bounded revocation staleness for sessions | Gate 2 session policies |
| ASM-033 | Auto-number IDs use the `kernel.record_sequences` table (one row per tenant, environment and entity), not database sequences (no per-tenant DDL) | ADR 0002 | Creates of one entity in one tenant environment serialize briefly on the sequence row; no gaps, because allocation is inside the record transaction | Contention at high create rates |

## Decisions made while closing Gate 1

| ID | Assumption | Source | Impact | How / when to revisit |
|---|---|---|---|---|
| ASM-034 | The in-app builder's default delegation excludes `records:read`: the model works on configuration and synthetic samples; reading real records is an explicit opt-in scope | Security review (threat model T-23) | No record text reaches the model provider by default | Customers asking the assistant to reason over real tickets (Gate 4 assistant features) |
| ASM-035 | Sign-in limits: 30 login attempts per minute per client address, refresh at ten times that, and a 15-minute lock after 8 failed passwords for one account | Security review (T-50) | Slows guessing and credential stuffing; a lock can be triggered by someone who knows an email (mitigated by its short duration) | Gate 2 SSO and MFA |
| ASM-036 | Experience targets are measured with the built-in planner on the Docker Compose stack: 20 samples of "add a field to a card", time from sending the prompt to the field rendering, and from the server applying a step to it rendering | Gate 1 measurement | Proves the live-editing pipeline; model latency is excluded because no API key is available | Repeat with Claude once a key is provided ([human action](../human-actions.md)) |
| ASM-037 | Gate 1 query benchmark at 100,000 tickets in one tenant environment (20% of the declared pilot size), median budget 300 ms per query shape; the 500,000-ticket load test with p95 targets is Gate 2 | G1-M5, ASM-003 | Evidence that the permission predicate inside SQL scales; tail latency on shared CI runners is reported, not asserted | Gate 2 load test |
| ASM-038 | Reset to default is an overlay operation (`element.reset`) in the draft, not a destructive edit of the overlay: it undoes earlier operations on one element and shows dependents as validation errors before publishing | REQ-CFG-011 | Reset is reviewable, undoable and published like any change | User feedback on resetting whole pages |

