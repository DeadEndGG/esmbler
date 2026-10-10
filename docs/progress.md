---
title: Progress and backlog
status: living
owner: platform-lead
last_reviewed: 2026-10-10
requirements: [all]
---

# Progress and backlog

Updated after every milestone ([execution brief section 1](spec/execution-brief.md)). A new session reads `AGENTS.md` and this file and resumes from **Next**.

## Status

| Field | Value |
| --- | --- |
| Current gate | Gate 1: Architecture proof |
| Current milestone | G1-M2 identity, dev auth, OpenFGA facts; G1-M4 record engine |
| Done | Gate 0 (tag `gate-0-complete`). Gate 1: G1-M1 kernel schema and RLS; record service, identity service, provisioning and demo seed with integration tests (parts of G1-M2, G1-M4, G1-M5) |
| Next | JWT auth and REST API endpoints (G1-M2/G1-M4), outbox dispatcher in the worker, then drafts and publish (G1-M6, G1-M7) |
| Blockers | None |

## How to read the backlog

- **IDs:** `G<gate>-M<n>`. Each milestone is one vertical slice (data model, API, rules, UI, MCP tools, tests, docs) with its requirement IDs from the [traceability matrix](spec/traceability.md).
- **Done:** a checkbox is ticked only when the [definition of done](../CONTRIBUTING.md#definition-of-done) holds and its tests pass.
- **Order:** within a gate, milestones are listed in build order.

## Gate 0: decisions and foundation

- [x] **G0-M1 Repo scaffold and CI.** Solution, projects, Angular workspace (`apps/web`), Dockerfiles (`deploy/docker/`), `deploy/docker-compose.yml`, CI (`.github/workflows/ci.yml`: build, format, unit, architecture, integration, frontend, docs lint and links, gitleaks, compose + Playwright acceptance). REQ-OPS-006, REQ-MOD-002
- [x] **G0-M2 Expression language, spec model, config compiler.** `Esmbler.Expressions` (CEL subset plus conformance cases); SDK specs and overlay ops; `Composer`, `ConfigValidator`, `DependencyGraph`, `ConfigCompiler`; Tickets releases; unit tests. REQ-RULE-003, REQ-CFG-001, REQ-CFG-002, REQ-CFG-004, REQ-CFG-005, REQ-CFG-006, REQ-AI-001
- [x] **G0-M3 Decision docs.** ADRs 0001–0013, `docs/decisions/assumptions.md`, `docs/security/threat-model.md`, `docs/human-actions.md`, `docs/known-issues.md`. REQ-AUTHZ-001, REQ-EXT-001, REQ-AISAFE-001
- [x] **G0-M4 Architecture, registry, design principles.** `docs/architecture/overview.md` (C4 levels 1–3), `docs/architecture/modules/tickets.md`, `docs/reference/element-registry-schema.md`, Gate 1 element specs (60 JSON files), `docs/explanation/design-principles.md`, `layered-configuration.md`, `authorization-model.md`. Docs written; generated catalog in `docs/reference/element-catalog.md`; registry token gaps tracked in design principles. REQ-AI-002, REQ-EL-001, REQ-DS-001, REQ-DS-002
- [x] **G0-M5 Spikes.** Authorization (OpenFGA vs SpiceDB vs Cedar) done; extension runtime (Wasmtime sidecar, Rust guest) done. REQ-AUTHZ-001, REQ-EXT-001, REQ-EXT-003
- [x] **G0-M6 Traceability, acceptance plan, backlog.** `docs/spec/traceability.md` seeded; `docs/testing/acceptance-plan.md` and this backlog written. REQ-ACC-01 to REQ-ACC-14 (planning), REQ-AISAFE-005
- [x] **G0-M7 Agent instruction files and docs lint.** Root `AGENTS.md` and `CLAUDE.md`, nested `AGENTS.md`/`CLAUDE.md` per module and package, `.claude/rules/*`, `.claude/agents/*` (boundary, security, spec, docs reviewers), markdownlint, link check, front-matter check in CI. REQ-MOD-002
- [x] **Gate 0 close.** All Gate 0 evidence in the [acceptance plan](testing/acceptance-plan.md#gate-0-decisions) passes; tag `gate-0-complete`; `docs/testing/gate-0-test-guide.md`; CHANGELOG updated

## Gate 1: architecture proof

- [x] **G1-M1 Kernel tenancy, RLS, migrations.** `kernel` schema, SQL migration runner, roles `esmbler_migrator` and `esmbler_app`, `FORCE ROW LEVEL SECURITY`, `set_config('app.tenant_id', …, true)` per transaction, fail-closed policies, RLS tests for every table. REQ-TEN-001, REQ-TEN-002, REQ-REC-001
- [ ] **G1-M2 Identity, dev auth, OpenFGA facts.** Principals and memberships, password login, JWT (`esmbler-api`), explicit tenant/environment switch, agent tokens (`esmbler-mcp`), OpenFGA store per tenant, membership outbox writer, seed tenants and users. REQ-IAM-001, REQ-IAM-002, REQ-TEN-003, REQ-AISAFE-004
- [ ] **G1-M3 PDP.** Four-level decisions from compiled permission sets plus OpenFGA, agent intersection, fail closed, explain access (API). REQ-AUTHZ-002, REQ-AUTHZ-003, REQ-AUTHZ-006, REQ-AUTHZ-007, REQ-AUTHZ-009, REQ-AUTHZ-010
- [ ] **G1-M4 Record engine, audit, outbox.** `kernel.records` CRUD and soft delete, revisions and idempotency keys, precise types, referential integrity, audit in the same transaction, outbox, worker dispatcher, inbox dedup, per-record ordering. REQ-REC-004, REQ-REC-005, REQ-REC-006, REQ-REC-007, REQ-EVT-001, REQ-EVT-002, REQ-EVT-003, REQ-EVT-004, REQ-ACC-06
- [ ] **G1-M5 Query compiler and list API.** Typed query contract, CEL filter to parameterized SQL over JSONB, permission predicate before paging and count, bounds (page 100, 64 nodes, 5 s), GIN and tsvector indexes, pilot-scale benchmark. REQ-REC-002, REQ-REC-003, REQ-AUTHZ-005
- [ ] **G1-M6 Config store, drafts, commands.** `kernel.config_artifacts`, `kernel.active_config`, one open draft per tenant environment, `kernel.draft_commands` with `expectedDraftVersion`, undo and redo, diff, `409 draft_conflict` with diff, `kernel.publish_requests`. REQ-CFG-006, REQ-LAY-004, REQ-LIVE-004, REQ-AI-005, REQ-ACC-02
- [ ] **G1-M7 Publish, activation, rollback.** Approval bound to hash, tenant, environment and approver permissions; `permissions.admin` for permission ops; optimistic pointer switch; health check; previous artifact kept; compatibility-checked rollback; `X-Config-Revision` pinning and `409 config_conflict`; customizations view and reset to default. REQ-CFG-007, REQ-CFG-008, REQ-CFG-010, REQ-CFG-011, REQ-AUTHZ-008
- [ ] **G1-M8 Rules and workflow services.** Data and form rules on every write path, rule ordering, cycle detection, `simulate_rule`, workflow transitions with guards, roles, required fields and set values, assignment rules, notifications to the fake outbox, preview side-effect suppression. REQ-RULE-001, REQ-RULE-002, REQ-RULE-004, REQ-RULE-005, REQ-RULE-006, REQ-BIZ-001, REQ-AISAFE-002, REQ-ACC-09
- [ ] **G1-M9 MCP server tools.** `apps/Esmbler.Mcp` with discovery, build, review, lifecycle (`request_publish`, `discard_draft`) and explain tools; resources; audience validation; per-call audit; rate and tool-call limits; structured errors. REQ-AI-003, REQ-AI-004, REQ-AI-006, REQ-AISAFE-003, REQ-AISAFE-004, REQ-AUTHZ-009
- [ ] **G1-M10 SignalR live channel.** `/hubs/live`, draft groups, `pg_notify('esmbler_draft')` plus LISTEN, `draftChanged` payload, latency instrumentation. REQ-LIVE-002, REQ-LIVE-004
- [ ] **G1-M11 Angular shell and design system.** Tokens (light and dark), base components, app shell, command palette, keyboard shortcut registry, design-lint rules (stylelint), axe in tests. REQ-DS-001, REQ-DS-002, REQ-DS-003
- [ ] **G1-M12 Record page renderer.** Registry-driven renderer (one component per element), 12-column grid, inline editing, activity feed (public and internal), audit history, related list, transition buttons, field permissions honored. REQ-LAY-001, REQ-LAY-002, REQ-AUTHZ-004, REQ-BIZ-002
- [ ] **G1-M13 List page.** Data table (dense, keyboard `j`/`k`, optimistic inline edit), filter bar, saved views, quick create. REQ-REC-002, REQ-DS-002
- [ ] **G1-M14 Live editing.** Draft mode band, chat panel docked beside the page, point-and-prompt selection, slash menu, drag handles with keyboard alternative, highlight and fade, step-to-node links, stop, undo step, accept or discard. REQ-LIVE-001, REQ-LIVE-002, REQ-LIVE-003, REQ-LAY-003, REQ-LAY-004
- [ ] **G1-M15 Assistant loop and scripted planner.** Anthropic adapter (`claude-opus-5-5`, configurable) as MCP client with the user's agent token; scripted planner without an API key; clarify at most twice; plan big changes; plain-English explanations; untrusted-content wrapping and redaction; budgets and cancellation; AI evaluation harness. REQ-AI-007, REQ-AISAFE-001, REQ-AISAFE-003, REQ-AISAFE-005, REQ-ACC-10
- [ ] **G1-M16 Tickets 1.1.0 upgrade with migration failure and recovery.** Upgrade report (applied, redundant, conflict), migration jobs (`backfill_impact`, `convert_effort`) with checkpoints, fault injection, resume, invalid values preserved, unsafe rollback blocked. REQ-CFG-009, REQ-MIG-001, REQ-MIG-002, REQ-ACC-03, REQ-ACC-04, REQ-MOD-001
- [ ] **G1-M17 Revocation across surfaces.** `authz_revision` cache keys, job re-authorization, agent session cut-off. REQ-AUTHZ-006, REQ-ACC-07
- [ ] **G1-M18 Acceptance suite and Gate 1 test guide.** `Gate1PathTests`, Playwright scenarios REQ-ACC-01, 02, 03, 04, 06, 07, 09, 10, experience-target measurements, traceability update, `docs/testing/gate-1-test-guide.md`, tag `gate-1-complete`. REQ-ACC-01, REQ-TEN-002, REQ-LIVE-002

## Gate 2: private pilot

- [ ] **G2-M1 Durable jobs and timers.** Leases, backoff, dead letters, operator replay, tenant fairness and quotas, cycle detection and budgets. REQ-EVT-005, REQ-EVT-006, REQ-TEN-006, REQ-ACC-11
- [ ] **G2-M2 Full request workflow.** Requests and catalog items, typed variables, fulfillment tasks and dependencies, workflow versioning with pinned instances. REQ-BIZ-004, REQ-MIG-003, REQ-ACC-05
- [ ] **G2-M3 Approvals.** Approval policy and panel, sequential and parallel stages, delegation, expiry, separation of duties. REQ-BIZ-004, REQ-AUTHZ-011
- [ ] **G2-M4 SLA and business hours.** SLA policy and timer, calendars, pause and resume, time zones, DST, holidays, versioning. REQ-BIZ-005, REQ-MIG-004, REQ-ACC-13
- [ ] **G2-M5 Email intake.** Email adapter (fake plus one real provider), threading, sender policy, dedup, loop prevention, no internal-note disclosure. REQ-BIZ-003, REQ-EVT-007, REQ-ACC-13
- [ ] **G2-M6 Portal.** Portal page, request form, request status tracker, requester access. REQ-BIZ-004
- [ ] **G2-M7 Attachments.** Attachment field and panel, object storage adapter (fake plus real), object-level authorization, scanning and quarantine, limits. REQ-BIZ-006
- [ ] **G2-M8 One integration and webhooks.** Connector framework basics, one validated integration, outbound webhooks with idempotency and reconciliation. REQ-OPS-005, REQ-EVT-007
- [ ] **G2-M9 Basic reports.** Report spec, KPI tile, bar chart, permission-aware aggregates. REQ-OPS-004
- [ ] **G2-M10 Environments and promotion.** Sandbox to prod diff, approve, deploy and roll back; synthetic sandbox data; no copied credentials or schedules. REQ-TEN-004, REQ-TEN-005
- [ ] **G2-M11 Enterprise identity.** Per-tenant OIDC and SAML, SCIM, MFA and passkeys (IdP registration is a human action). REQ-IAM-003
- [ ] **G2-M12 Admin Center and secrets.** Settings schema to auto-generated panels, standard sections, secrets as vault references. REQ-ADM-001, REQ-ADM-002
- [ ] **G2-M13 Observability and governors.** Scoped logs, metrics, traces; platform limits with clear errors. REQ-OPS-001, REQ-OPS-003
- [ ] **G2-M14 Backup and isolated tenant restore.** Backup jobs, restore-and-extract procedure, reconciliation, drill. REQ-OPS-002, REQ-ACC-12
- [ ] **G2-M15 Governance and theming.** Access reviews, JIT elevation, break-glass; bounded tenant theming with contrast checks. REQ-AUTHZ-011, REQ-DS-004
- [ ] **G2-M16 Pilot acceptance, workload report, runbooks, Gate 2 test guide.** REQ-ACC-05, REQ-ACC-06, REQ-ACC-11, REQ-ACC-12, REQ-ACC-13

## Gate 3: partner beta

- [ ] **G3-M1 Package model.** Package types (module, mod, config pack), manifest schema, private installation into a draft environment. REQ-PKG-001
- [ ] **G3-M2 Extension points and conflicts.** Declared cardinality, ordered pipelines, exclusive strategy selection, conflict reports. REQ-PKG-002, REQ-ACC-08
- [ ] **G3-M3 Lockfile, compatibility, uninstall.** Exact lockfile, compatibility matrix checks, disable and uninstall plans that never strand work. REQ-PKG-003, REQ-ACC-08
- [ ] **G3-M4 Extension runtime.** Rust Wasmtime sidecar per ADR 0006, WIT contract, capabilities, fuel, epoch and memory limits, per-tenant concurrency, compatibility matrix. REQ-EXT-001, REQ-EXT-002, REQ-EXT-003
- [ ] **G3-M5 UI extensions.** Sandboxed iframes on isolated origins, validated postMessage bridge. REQ-EXT-004
- [ ] **G3-M6 SDK and CLI.** Public SDK packaging, CLI, local emulator, developer tenant. REQ-PKG-001
- [ ] **G3-M7 Escape hatch and chat to package.** Generated code through the package pipeline; export without records or secrets. REQ-AI-008, REQ-AI-009, REQ-ACC-14
- [ ] **G3-M8 Incidents module.** REQ-MOD-003
- [ ] **G3-M9 Knowledge module.** Lifecycle and permission-aware retrieval. REQ-MOD-003, REQ-BIZ-007
- [ ] **G3-M10 Partner beta acceptance and Gate 3 test guide.** Independent partner package build. REQ-ACC-08, REQ-ACC-14

## Gate 4: product expansion

Order follows the module dependency graph.

- [ ] **G4-M1 Problems module.** REQ-MOD-004
- [ ] **G4-M2 Changes module** (risk, approvals, CAB calendar). REQ-MOD-004
- [ ] **G4-M3 CRM, support context only** (accounts, contacts). REQ-MOD-004
- [ ] **G4-M4 Projects module.** REQ-MOD-004
- [ ] **G4-M5 CMDB.** Identification, reconciliation, provenance. REQ-MOD-004
- [ ] **G4-M6 Advanced reports and analytics store.** REQ-OPS-004
- [ ] **G4-M7 Real-time multi-builder collaboration.** Presence, merge model. REQ-LIVE-004
- [ ] **G4-M8 Dedicated database and region tier.** REQ-TEN-007
- [ ] **G4-M9 More connectors and extension languages.** REQ-OPS-005, REQ-EXT-003
- [ ] **G4-M10 Expansion acceptance, scale and recovery tests, Gate 4 test guide.** REQ-MOD-004

## Gate 5: public marketplace

- [ ] **G5-M1 Publisher onboarding** (identity verification, payouts account in provider sandbox). REQ-MKT-001
- [ ] **G5-M2 Review pipeline** (signing, scanning, contract tests, design and accessibility lint, human review queue). REQ-MKT-001
- [ ] **G5-M3 Listings and AI discovery** (store pages, MCP marketplace search). REQ-MKT-001
- [ ] **G5-M4 Licenses and entitlements** (grace, read-only and export policy). REQ-MKT-002
- [ ] **G5-M5 Billing, metering and payouts** (sandbox; live credentials are a human action). REQ-MKT-002
- [ ] **G5-M6 Revocation, kill switch, staged rollout.** REQ-MKT-002
- [ ] **G5-M7 Marketplace acceptance, Gate 5 test guide, `docs/final-report.md`.** REQ-MKT-001, REQ-MKT-002
