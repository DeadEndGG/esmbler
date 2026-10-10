---
title: Traceability matrix
status: living
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [all]
---

# Traceability matrix

Maps each master-spec requirement to its gate, implementation, verification, and evidence. Requirement IDs are referenced in code comments, test names or doc comments, and commit messages. Status values: `planned`, `in-progress`, `done` (implemented and verified), `deferred` (moved to a later gate with a reason).

Source sections refer to `docs/spec/master-spec.md` (MS) and `docs/spec/execution-brief.md` (EB).

## Tenancy and isolation

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-TEN-001 | Tenant/environment scope on every record and derived data; Postgres RLS with FORCE, non-owner runtime role, transaction-scoped context | MS Multi-tenancy | 1 | packages/Esmbler.Kernel.Infrastructure/Migrations/0001_kernel.sql, Persistence/Database.cs | tests/Esmbler.IntegrationTests/Isolation/DatabaseIsolationTests.cs | in-progress |
| REQ-TEN-002 | A change made by one tenant never affects another tenant (config, data, caches, jobs) | MS Multi-tenancy | 1 | | | planned |
| REQ-TEN-003 | Tenant/environment switching is explicit and authorized; never inferred from record IDs or tool arguments | MS Domain boundaries | 1 | | | planned |
| REQ-TEN-004 | Per-tenant environments (sandbox, prod) with promotion and rollback of overlay changes | MS Multi-tenancy | 2 | | | planned |
| REQ-TEN-005 | Sandbox data synthetic; production credentials, destinations, schedules never copied | MS Environment safety | 2 | | | planned |
| REQ-TEN-006 | Per-tenant rate limits, quotas, and fair scheduling | MS Multi-tenancy | 2 | | | planned |
| REQ-TEN-007 | Dedicated database/region tier without code changes | MS Tenancy tiers | 4 | | | planned |

## Configuration composition and deployment

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-CFG-001 | Layered metadata: platform base → packages → tenant overlay resolved into effective config | MS Multi-tenancy | 1 | packages/Esmbler.Kernel/Configuration/Composer.cs | UnitTests/Configuration/OverlayTests.cs | in-progress |
| REQ-CFG-002 | Overlays are typed operations against stable IDs with expected prior values; no blind JSON merge | MS Config composition | 1 | packages/Esmbler.Sdk/Overlay/OverlayOps.cs, Composer.cs | OverlayTests, UpgradeTests | in-progress |
| REQ-CFG-003 | Namespacing (core.*, publisher.*, tenant__*) | MS Multi-tenancy | 1 | packages/Esmbler.Sdk/Overlay/Namespaces.cs | OverlayTests.Tenant_field_without_namespace_is_rejected_with_suggestion | in-progress |
| REQ-CFG-004 | Dependency graph over schema, rules, pages, workflows, policies | MS Config composition | 1 | packages/Esmbler.Kernel/Configuration/DependencyGraph.cs | OverlayTests.Removing_a_used_tenant_field_lists_dependents | in-progress |
| REQ-CFG-005 | Validation before activation (types, references, containment, expression cost, cycles, migrations) | MS Config composition | 1 | packages/Esmbler.Kernel/Configuration/ConfigValidator.cs | OverlayTests, TicketsReleaseTests | in-progress |
| REQ-CFG-006 | Compiled immutable artifact with content hash, lockfile, compat, migrations, permission impact, provenance | MS Config composition | 1 | packages/Esmbler.Kernel/Configuration/ConfigCompiler.cs | TicketsReleaseTests.Content_hash_is_deterministic | in-progress |
| REQ-CFG-007 | Publish with optimistic concurrency; approval bound to artifact hash, tenant/environment, permission context | MS Config composition | 1 | | | planned |
| REQ-CFG-008 | Each request pins one config revision; incompatible older revisions get an actionable conflict | MS Config composition | 1 | | | planned |
| REQ-CFG-009 | Upgrades compose candidate base under overlays; conflicts never silently resolved | MS Multi-tenancy | 1 | Composer.cs (three-way via expected values) | UpgradeTests | in-progress |
| REQ-CFG-010 | Preserve previous artifact; health check after activation; compatibility-checked rollback | MS Config composition | 1 | | | planned |
| REQ-CFG-011 | Visible customizations, compare with default, reset to default with impact preview | MS Default modules | 1 | | | planned |

## Records, queries, and schema evolution

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-REC-001 | Shared physical storage for custom fields (no per-tenant DDL); documented index policy | MS Multi-tenancy, Records | 1 | | | planned |
| REQ-REC-002 | Typed query contract (filter, sort, paging) with bounded cost | MS Records | 1 | | | planned |
| REQ-REC-003 | Authorization applied before paging and counting; no leaks via counts, lookups, errors | MS Records | 1 | | | planned |
| REQ-REC-004 | Record revisions for optimistic concurrency; idempotency keys for retried commands | MS Records | 1 | | | planned |
| REQ-REC-005 | Precise data types (decimal scale, UTC instants vs local dates, null vs absent, stable choice IDs) | MS Records | 1 | | | planned |
| REQ-REC-006 | Referential integrity, deletion restriction, soft delete and restore | MS Records | 1 | | | planned |
| REQ-REC-007 | Audit history of every change with actor, agent, and config revision | MS Kernel | 1 | | | planned |
| REQ-MIG-001 | Schema change classification and migration plans (expand/backfill/contract) | MS Migration | 1 | | | planned |
| REQ-MIG-002 | Failed/partial migrations resume safely, preserve invalid values, block unsafe rollback | MS Migration | 1 | | | planned |
| REQ-MIG-003 | Workflow definitions versioned; active instances pinned or explicitly migrated | MS Migration | 2 | | | planned |
| REQ-MIG-004 | SLA calendars/policies versioned; edits do not corrupt running timers or history | MS Migration | 2 | | | planned |

## Events and durable execution

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-EVT-001 | Record change, audit entry, and outbox event commit in one transaction | MS Transactions | 1 | | | planned |
| REQ-EVT-002 | At-least-once delivery with durable inbox deduplication and idempotent handlers | MS Transactions | 1 | | | planned |
| REQ-EVT-003 | Event envelope fields (IDs, tenant/env, revision, schema version, config ref, correlation/causation) | MS Transactions | 1 | | | planned |
| REQ-EVT-004 | Per-record ordering; projections reject stale revisions | MS Transactions | 1 | | | planned |
| REQ-EVT-005 | Durable jobs and timers with leases, backoff, max attempts, dead letters, replay | MS Transactions | 2 | | | planned |
| REQ-EVT-006 | Automation cycle detection, execution budgets, fan-out limits | MS Transactions | 2 | | | planned |
| REQ-EVT-007 | External side effects idempotent or reconciled | MS Transactions | 2 | | | planned |

## Identity and authorization

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-IAM-001 | Principals: users, groups, service accounts, package principals, AI agents (auditable) | MS Identity | 1 | | | planned |
| REQ-IAM-002 | A person may belong to multiple tenants | MS Domain boundaries | 1 | | | planned |
| REQ-IAM-003 | SSO (OIDC/SAML), SCIM, MFA, passkeys, per-tenant IdPs | MS Identity | 2 | | | planned |
| REQ-AUTHZ-001 | Hybrid RBAC + ABAC + ReBAC with a justified engine | MS Authorization | 0 | docs/adr/0004-authorization-engine.md | spikes/authz-openfga | done |
| REQ-AUTHZ-002 | Four levels: entity, record, field, action | MS Authorization | 1 | packages/Esmbler.Kernel/Authorization/PolicyDecisionPoint.cs | UnitTests/Records/WritePipelineTests.cs | in-progress |
| REQ-AUTHZ-003 | Policies are specs with layering, versioning, draft, rollback | MS Authorization | 1 | | | planned |
| REQ-AUTHZ-004 | UI follows permissions automatically; hidden fields never sent to clients | MS Authorization | 1 | | | planned |
| REQ-AUTHZ-005 | Permission-aware list filtering (pre-filtered) | MS Authorization | 1 | | | planned |
| REQ-AUTHZ-006 | Revocation propagation with bounded staleness; fail closed | MS Authorization | 1 | | | planned |
| REQ-AUTHZ-007 | AI acts with intersection of user permissions and delegated scopes; cannot self-elevate | MS AI, packages | 1 | PolicyDecisionPoint.cs (agent intersection) | WritePipelineTests.Agent_gets_intersection_of_user_permissions_and_scopes | in-progress |
| REQ-AUTHZ-008 | Permission changes need a distinct permissions.admin action | MS AI, packages | 1 | | | planned |
| REQ-AUTHZ-009 | Explain access ("why can Maria see this ticket?") via API and MCP | MS Explainability | 1 | | | planned |
| REQ-AUTHZ-010 | Linking records never implicitly grants access | MS Authorization | 1 | | | planned |
| REQ-AUTHZ-011 | Governance: access reviews, JIT elevation, delegation, break-glass, separation of duties | MS Governance | 2 | | | planned |

## AI builder and MCP

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-AI-001 | Spec-driven: everything is declarative JSON validated against JSON Schema | MS AI builder | 1 | | | planned |
| REQ-AI-002 | Self-describing element registry readable by the AI | MS AI builder | 0 | packages/Esmbler.Sdk/Registry, docs/reference/element-catalog.md (generated) | TicketsReleaseTests.Registry_loads_gate1_catalog | done |
| REQ-AI-003 | MCP resources and discovery, build, review, lifecycle tools | MS AI builder | 1 | | | planned |
| REQ-AI-004 | Precise, actionable errors so the AI can self-correct | MS AI builder | 1 | | | planned |
| REQ-AI-005 | Draft workspaces; human reviews diff and preview before publish | MS AI builder | 1 | | | planned |
| REQ-AI-006 | MCP context bound by authentication; no tenant/environment override; audit actor, agent, target, revision, outcome | MS AI builder | 1 | | | planned |
| REQ-AI-007 | Clarify at most twice; plan big changes; explain changes in plain English including rule scope | EB 3 | 1 | | | planned |
| REQ-AI-008 | Escape hatch: generated code goes through the package pipeline | MS AI builder | 3 | | | planned |
| REQ-AI-009 | Chat to package export without tenant records or secrets | MS AI builder | 3 | | | planned |
| REQ-AISAFE-001 | Untrusted content cannot expand tool authority (prompt injection) | MS AI safeguards | 1 | | | planned |
| REQ-AISAFE-002 | Simulations/previews have no external side effects | MS AI safeguards | 1 | | | planned |
| REQ-AISAFE-003 | Bounded tool calls, duration, context, spend; cancellation and per-agent disable | MS AI safeguards | 1 | | | planned |
| REQ-AISAFE-004 | MCP token audience validation; no token passthrough | MS AI safeguards | 1 | | | planned |
| REQ-AISAFE-005 | AI evaluation suite (task completion, unintended effects, injection, unsupported ops) | MS AI safeguards, EB 3 | 1 | | | planned |

## Layout, live editing, rules, design system

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-LAY-001 | Page tree Page → Section → Row → Column → Container → Element on a 12-column grid with stable IDs | MS Layout | 1 | | | planned |
| REQ-LAY-002 | Container types declare allowed children; invalid layouts rejected | MS Layout | 1 | | | planned |
| REQ-LAY-003 | Point-and-prompt: selection attached to chat as structured context | MS Visual builder | 1 | | | planned |
| REQ-LAY-004 | All edits are Commands (undo/redo, diff, audit); drag-and-drop and AI edits interchangeable | MS Visual builder | 1 | | | planned |
| REQ-LIVE-001 | Chat docked beside the real page; draft mode with sample data | EB 3 | 1 | | | planned |
| REQ-LIVE-002 | Each AI change visible on the page within 1 s of its tool call (p95) | EB 3 | 1 | | | planned |
| REQ-LIVE-003 | Chat steps link to page elements and back; stop, undo one step, accept or discard draft | EB 3 | 1 | | | planned |
| REQ-LIVE-004 | Version checks prevent human and AI edits overwriting each other; other users see published page | EB 3 | 1 | | | planned |
| REQ-RULE-001 | Field validation (required, type, min/max, length, regex, allowed values, uniqueness) | MS Rules | 1 | packages/Esmbler.Kernel/Records/FieldNormalizer.cs, RecordWriteEngine.cs | WritePipelineTests.Normalization_rejects_bad_types_with_suggestions | in-progress |
| REQ-RULE-002 | Conditional rules (require, show/hide, read-only, set value, restrict options, message) | MS Rules | 1 | packages/Esmbler.Platform/Rules | WritePipelineTests.Tenant_conditional_rule_requires_justification_for_access_requests | in-progress |
| REQ-RULE-003 | Safe non-Turing-complete expressions; server/client conformance | MS Rules | 1 | packages/Esmbler.Expressions (C#); TypeScript twin pending | UnitTests/Expressions/ConformanceTests.cs | in-progress |
| REQ-RULE-004 | Data-level rules enforced on every write path; form-level per view | MS Rules | 1 | RecordWriteEngine.cs + Platform hooks | WritePipelineTests | in-progress |
| REQ-RULE-005 | Rule ordering, conflict and cycle detection, simulate tool | MS Rules | 1 | ConfigValidator.cs (ordering, overlap, cycles) | OverlayTests.Set_value_cycles_are_detected | in-progress |
| REQ-RULE-006 | Hidden field values never sent to the client to evaluate a rule | MS Rules | 1 | | | planned |
| REQ-DS-001 | Specs reference tokens and components only; no raw styling | MS Design system | 1 | | | planned |
| REQ-DS-002 | Linear/Notion-inspired UX: command palette, keyboard-first, slash menu, drag handles, inline editing, dark mode | EB 3 | 1 | | | planned |
| REQ-DS-003 | WCAG 2.2 AA, keyboard navigation, screen readers, translatable labels | MS Design system | 1 | | | planned |
| REQ-DS-004 | Bounded tenant theming with contrast checks | MS Design system | 2 | | | planned |
| REQ-EL-001 | Gate 1 element subset fully described in the registry | EB 5 | 0 | packages/Esmbler.Sdk/Registry/Elements (60 elements) | docs/reference/element-catalog.md | done |

## Modules, packages, and extension runtime

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-MOD-001 | Default Tickets module ships complete and is built only on the public SDK | MS Default modules | 1 | modules/tickets (declarative releases) | TicketsReleaseTests, BoundaryTests | in-progress |
| REQ-MOD-002 | Module boundaries enforced by architecture tests; no cross-module data access | MS Architecture, EB 2 | 1 | tests/Esmbler.ArchitectureTests/BoundaryTests.cs | BoundaryTests (5) | done |
| REQ-MOD-003 | Incidents and Knowledge modules | MS Default modules | 3 | | | planned |
| REQ-MOD-004 | Problems, Changes, CRM (support context), Projects, CMDB | MS Default modules | 4 | | | planned |
| REQ-PKG-001 | Package types (module, mod, config pack) and manifest | MS Packages | 3 | | | planned |
| REQ-PKG-002 | Extension points with declared cardinality; exclusive strategies conflict explicitly | MS Packages | 3 | | | planned |
| REQ-PKG-003 | Lockfile, compatibility checks, uninstall/disable plans that never strand work | MS Packages | 3 | | | planned |
| REQ-EXT-001 | Wasm Component Model sandbox with WIT interfaces and pinned Wasmtime | MS Architecture | 0 (spike), 3 | docs/adr/0006-extension-runtime.md | spikes/extension-runtime (Gate 3 implementation pending) | in-progress |
| REQ-EXT-002 | Capability-based permissions and CPU/memory/time limits per tenant/environment | MS Architecture | 3 | | | planned |
| REQ-EXT-003 | Tested language/runtime compatibility matrix; one toolchain first | MS Architecture | 0 (spike), 3 | docs/adr/0006-extension-runtime.md (matrix) | spikes/extension-runtime/README.md | in-progress |
| REQ-EXT-004 | UI extensions in sandboxed iframes with validated postMessage bridge | MS Architecture | 3 | | | planned |

## Business behavior, admin, operations, marketplace

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-BIZ-001 | Ticket lifecycle: ownership, parent/child, reopen, cancel, concurrent edits, audit | MS Business behavior | 1 | Platform/Workflow/WorkflowHook.cs, Tickets lifecycle | WritePipelineTests (transitions, read-only) | in-progress |
| REQ-BIZ-002 | Conversations: public replies vs internal work notes with visibility checks | MS Business behavior | 1 | | | planned |
| REQ-BIZ-003 | Email intake (threading, sender policy, dedup, loop prevention) | MS Business behavior | 2 | | | planned |
| REQ-BIZ-004 | Requests, catalog, approvals, fulfillment tasks | MS Business behavior | 2 | | | planned |
| REQ-BIZ-005 | SLA and business hours (pause/resume, time zones, DST, holidays) | MS Business behavior | 2 | | | planned |
| REQ-BIZ-006 | Attachments with object-level authorization, scanning, limits | MS Business behavior | 2 | | | planned |
| REQ-BIZ-007 | Knowledge lifecycle and permission-aware retrieval | MS Business behavior | 3 | | | planned |
| REQ-ADM-001 | Admin Center with auto-generated package panels from settings schema | MS Admin Center | 2 | | | planned |
| REQ-ADM-002 | Secrets as references in a vault; never exposed in UI, MCP, logs, exports | MS Admin Center | 2 | | | planned |
| REQ-OPS-001 | Scoped observability (logs, metrics, traces, audit) | MS Platform essentials | 2 | | | planned |
| REQ-OPS-002 | Backups and isolated pooled-tenant restore | MS Platform essentials | 2 | | | planned |
| REQ-OPS-003 | Platform limits (governors) with clear errors | MS Platform essentials | 2 | | | planned |
| REQ-OPS-004 | Basic reports over standard and custom fields | MS First release | 2 | | | planned |
| REQ-OPS-005 | One validated integration and outbound webhooks | MS First release | 2 | | | planned |
| REQ-OPS-006 | Containerized: one `docker compose up` starts the system with seed data | EB 2 | 0 | deploy/docker-compose.yml, deploy/docker/* | docs/testing/gate-0-test-guide.md scenario 1 | done |
| REQ-MKT-001 | Publisher onboarding, review pipeline, listings | MS Marketplace | 5 | | | planned |
| REQ-MKT-002 | Licenses, billing, payouts, revocation | MS Marketplace | 5 | | | planned |

## Acceptance scenarios

| ID | Scenario (MS "Acceptance scenarios") | Gate | Tests / evidence | Status |
| --- | --- | --- | --- | --- |
| REQ-ACC-01 | Tenant A adds field, rule, page card; tenant B unchanged | 1 | | planned |
| REQ-ACC-02 | Competing builder revisions; stale writes rejected with diff | 1 | | planned |
| REQ-ACC-03 | Module upgrade preserves compatible overlays, blocks incompatible with impact report | 1 | | planned |
| REQ-ACC-04 | Field conversion fails partway; safe resume, invalid values preserved, no unsafe rollback | 1 | | planned |
| REQ-ACC-05 | Workflow/approval/SLA definition change with active instances | 2 | | planned |
| REQ-ACC-06 | Duplicate/out-of-order events cannot regress state or duplicate work | 1 (events), 2 (fulfillment) | | planned |
| REQ-ACC-07 | Revocation across request, cached search, queued job, AI session | 1 | | planned |
| REQ-ACC-08 | Conflicting mods and uninstall of depended-on package | 3 | | planned |
| REQ-ACC-09 | Preview/simulation cannot reach production endpoints or send notifications | 1 | | planned |
| REQ-ACC-10 | Malicious embedded instructions cannot authorize tool actions or expose data | 1 | | planned |
| REQ-ACC-11 | Overloaded tenant/connector/extension cannot exhaust shared workers | 2 | | planned |
| REQ-ACC-12 | Pooled tenant restored in isolation | 2 | | planned |
| REQ-ACC-13 | Email loops, public vs private notes, concurrent edits, reopen, DST SLA | 2 | | planned |
| REQ-ACC-14 | Package export excludes tenant records, secrets, bindings | 3 | | planned |
