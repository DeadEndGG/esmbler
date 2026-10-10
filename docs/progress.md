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
| Current gate | Gate 2: Private pilot |
| Current milestone | G2-M9 metrics, dashboards and the connected explorer |
| Done | Gate 0 (tag `gate-0-complete`). Gate 1 (tag `gate-1-complete`): all milestones G1-M1 to G1-M18; see [Gate 1 test guide](testing/gate-1-test-guide.md). Gate 2: G2-M1 durable jobs, G2-M2 full request workflow, G2-M3 approvals, G2-M17 event platform v2, G2-M4 SLA and business hours |
| Next | G2-M9 and G2-M9b ([connected workspace](spec/connected-workspace.md)), then G2-M18 notifications and G2-M19 alerts and audit policy ([ADR 0017](adr/0017-event-audit-alert-notification-services.md)), then G2-M5 email intake (reuses the email channel) |
| Interjected | Product owner request (2026-10-10, later): research Zendesk and Jira's work hierarchy (`docs/research/`), and support several AI providers, OpenAI first, plus using an existing ChatGPT or Claude subscription (G2-M20). Product owner request (2026-10-10): events, audit, alerts and notifications as a configurable shared service (ADR 0017, G2-M17 to G2-M19). Product owner review (2026-10-10): connected workspace and guided creation recorded as REQ-WS-001 to 006 and REQ-ACC-15, scheduled right after SLAs. Web UI visual refresh on Tailwind CSS v4 ([ADR 0014](adr/0014-tailwind-css-for-the-web-ui.md)), including a fix for the production stylesheet that the CSP blocked; axe clean in light and dark |
| Blockers | None. Open human actions: Anthropic API key to score the Claude planner; product-target confirmation ([human actions](human-actions.md)) |

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
- [x] **G1-M2 Identity, dev auth, OpenFGA facts.** Principals and memberships, password login, JWT (`esmbler-api`), explicit tenant/environment switch, agent tokens (`esmbler-mcp`), OpenFGA store per tenant, membership outbox writer, seed tenants and users. REQ-IAM-001, REQ-IAM-002, REQ-TEN-003, REQ-AISAFE-004 Done: login, refresh rotation with reuse detection and a 24 h session cap, explicit switch, agent tokens, one OpenFGA store per tenant written in graph-sync order (revoke before commit, grant after) instead of a membership outbox.
- [x] **G1-M3 PDP.** Four-level decisions from compiled permission sets plus OpenFGA, agent intersection, fail closed, explain access (API). REQ-AUTHZ-002, REQ-AUTHZ-003, REQ-AUTHZ-006, REQ-AUTHZ-007, REQ-AUTHZ-009, REQ-AUTHZ-010 Done.
- [x] **G1-M4 Record engine, audit, outbox.** `kernel.records` CRUD and soft delete, revisions and idempotency keys, precise types, referential integrity, audit in the same transaction, outbox, worker dispatcher, inbox dedup, per-record ordering. REQ-REC-004, REQ-REC-005, REQ-REC-006, REQ-REC-007, REQ-EVT-001, REQ-EVT-002, REQ-EVT-003, REQ-EVT-004, REQ-ACC-06 Done, including restore within 30 days.
- [x] **G1-M5 Query compiler and list API.** Typed query contract, CEL filter to parameterized SQL over JSONB, permission predicate before paging and count, bounds (page 100, 64 nodes, 5 s), GIN and tsvector indexes, pilot-scale benchmark. REQ-REC-002, REQ-REC-003, REQ-AUTHZ-005 Done; benchmark at 100,000 tickets (`Records/QueryBenchmarkTests`), full pilot-scale load test in Gate 2 (ASM-037).
- [x] **G1-M6 Config store, drafts, commands.** `kernel.config_artifacts`, `kernel.active_config`, one open draft per tenant environment, `kernel.draft_commands` with `expectedDraftVersion`, undo and redo, diff, `409 draft_conflict` with diff, `kernel.publish_requests`. REQ-CFG-006, REQ-LAY-004, REQ-LIVE-004, REQ-AI-005, REQ-ACC-02 Done.
- [x] **G1-M7 Publish, activation, rollback.** Approval bound to hash, tenant, environment and approver permissions; `permissions.admin` for permission ops; optimistic pointer switch; health check; previous artifact kept; compatibility-checked rollback; `X-Config-Revision` pinning and `409 config_conflict`; customizations view and reset to default. REQ-CFG-007, REQ-CFG-008, REQ-CFG-010, REQ-CFG-011, REQ-AUTHZ-008 Done: activation health check with automatic revert; customizations list and reset to default (`element.reset`).
- [x] **G1-M8 Rules and workflow services.** Data and form rules on every write path, rule ordering, cycle detection, `simulate_rule`, workflow transitions with guards, roles, required fields and set values, assignment rules, notifications to the fake outbox, preview side-effect suppression. REQ-RULE-001, REQ-RULE-002, REQ-RULE-004, REQ-RULE-005, REQ-RULE-006, REQ-BIZ-001, REQ-AISAFE-002, REQ-ACC-09 Done.
- [x] **G1-M9 MCP server tools.** `apps/Esmbler.Mcp` with discovery, build, review, lifecycle (`request_publish`, `discard_draft`) and explain tools; resources; audience validation; per-call audit; rate and tool-call limits; structured errors. REQ-AI-003, REQ-AI-004, REQ-AI-006, REQ-AISAFE-003, REQ-AISAFE-004, REQ-AUTHZ-009 Done, plus `reset_to_default`.
- [x] **G1-M10 SignalR live channel.** `/hubs/live`, draft groups, `pg_notify('esmbler_draft')` plus LISTEN, `draftChanged` payload, latency instrumentation. REQ-LIVE-002, REQ-LIVE-004 Done; draft events carry server timestamps for latency measurement.
- [x] **G1-M11 Angular shell and design system.** Tokens (light and dark), base components, app shell, command palette, keyboard shortcut registry, design-lint rules (stylelint), axe in tests. REQ-DS-001, REQ-DS-002, REQ-DS-003 Done; axe scans in the Playwright suite; stylelint design lint is KI-005.
- [x] **G1-M12 Record page renderer.** Registry-driven renderer (one component per element), 12-column grid, inline editing, activity feed (public and internal), audit history, related list, transition buttons, field permissions honored. REQ-LAY-001, REQ-LAY-002, REQ-AUTHZ-004, REQ-BIZ-002 Done.
- [x] **G1-M13 List page.** Data table (dense, keyboard `j`/`k`, optimistic inline edit), filter bar, saved views, quick create. REQ-REC-002, REQ-DS-002 Done.
- [x] **G1-M14 Live editing.** Draft mode band, chat panel docked beside the page, point-and-prompt selection, slash menu, drag handles with keyboard alternative, highlight and fade, step-to-node links, stop, undo step, accept or discard. REQ-LIVE-001, REQ-LIVE-002, REQ-LIVE-003, REQ-LAY-003, REQ-LAY-004 Done; reverse element-to-step highlight and fade-out are KI-004.
- [x] **G1-M15 Assistant loop and scripted planner.** Anthropic adapter (`claude-opus-5-5`, configurable) as MCP client with the user's agent token; scripted planner without an API key; clarify at most twice; plan big changes; plain-English explanations; untrusted-content wrapping and redaction; budgets and cancellation; AI evaluation harness. REQ-AI-007, REQ-AISAFE-001, REQ-AISAFE-003, REQ-AISAFE-005, REQ-ACC-10 Done: `packages/Esmbler.Assistant` (scripted and Claude planners), 43-case evaluation suite; daily spend budget is KI-002; Claude scoring needs a key (KI-007).
- [x] **G1-M16 Tickets 1.1.0 upgrade with migration failure and recovery.** Upgrade report (applied, redundant, conflict), migration jobs (`backfill_impact`, `convert_effort`) with checkpoints, fault injection, resume, invalid values preserved, unsafe rollback blocked. REQ-CFG-009, REQ-MIG-001, REQ-MIG-002, REQ-ACC-03, REQ-ACC-04, REQ-MOD-001 Done; schema-change classification is partial (KI-003).
- [x] **G1-M17 Revocation across surfaces.** `authz_revision` cache keys, job re-authorization, agent session cut-off. REQ-AUTHZ-006, REQ-ACC-07 Done.
- [x] **G1-M18 Acceptance suite and Gate 1 test guide.** `Gate1PathTests`, Playwright scenarios REQ-ACC-01, 02, 03, 04, 06, 07, 09, 10, experience-target measurements, traceability update, `docs/testing/gate-1-test-guide.md`, tag `gate-1-complete`. REQ-ACC-01, REQ-TEN-002, REQ-LIVE-002 Done: `Acceptance/Gate1PathTests`, Playwright 9 scenarios, experience measurements, test guide.

- [x] **Gate 1 close.** Gate 1 evidence in the [acceptance plan](testing/acceptance-plan.md) passes; reviewer passes (security, spec, docs) applied; tag `gate-1-complete`; `docs/testing/gate-1-test-guide.md`; CHANGELOG updated

## Gate 2: private pilot

- [x] **G2-M1 Durable jobs and timers.** Leases, backoff, dead letters, operator replay, tenant fairness and quotas, cycle detection and budgets. REQ-EVT-005, REQ-EVT-006, REQ-TEN-006, REQ-ACC-11 Done: handler registry, idempotent timers, jittered backoff, dead letters with audited replay (Operations page), tenant-interleaved outbox claims, depth/fan-out/quota governors (`Events/DurableJobsTests`); load test with queue-wait budgets in G2-M16.
- [x] **G2-M2 Full request workflow.** Requests and catalog items, typed variables, fulfillment tasks and dependencies, workflow versioning with pinned instances. Done for the fulfillment and workflow-version parts; REQ-BIZ-004, REQ-MIG-003 and REQ-ACC-05 stay in progress for approvals and SLA ([ADR 0015](adr/0015-requests-catalog-fulfillment-and-workflow-versions.md)): Tickets 1.2.0 with requests, fulfillment tasks and three catalog items; typed variables validated on every write path; idempotent fulfillment consumer (dependencies, conditions, partial outcome, cancellation cascade); catalog and workflow overlay ops and MCP tools; versioned workflow definitions with pin, migrate and cancel policies and their impact in the publish review; catalog page and request form; upgrades that skip a release run its migrations.
- [x] **G2-M3 Approvals.** Approval policy and panel, sequential and parallel stages, delegation, expiry and escalation, separation of duties, approval rejection, requester changes after approval, active-instance behavior when an approval policy changes, and preserving pending approvals across workflow migrate and cancel. REQ-BIZ-004, REQ-MIG-003 Done; REQ-AUTHZ-011 in progress (access reviews, JIT elevation and break-glass in G2-M15) ([ADR 0016](adr/0016-approvals.md)): Tickets 1.3.0 (awaiting_approval, Approver role, manager and access policies); approval policy spec, overlay ops and MCP tools; `ApprovalService` with sequential stages, all/any approvers, conditional stages, expiry with one escalation that decides alone, delegation that grants read access only while active, separation of duties (creator, requester and the person a request is for, snapshotted; one person's authority counts once per stage); rejections need a reason; answers and locked fields frozen after intake; people cannot step around approval (intake, approval state, migrations); changing who approves needs permission administration; requests in intake go for approval when their item gains a policy, earlier ones are grandfathered (ASM-046); pinned policy snapshots and the approvals in flight shown in the publish review; approvals panel, Approvals page with out-of-office delegation, admin delegation review.
- [x] **G2-M4 SLA and business hours.** SLA policy and timer, calendars, pause and resume, time zones, DST, holidays, versioning; requester-facing SLA on requests and team SLAs on fulfillment tasks; `platform.sla.warning` and `platform.sla.breached` events (moved from G2-M17) (ADR 0015). REQ-BIZ-005, REQ-MIG-004, REQ-ACC-13 Done ([ADR 0018](adr/0018-sla-and-business-hours.md)): business calendars (IANA time zones, local hours, holidays, DST-correct business time) and SLA policies as spec objects with overlay ops and MCP tools; timers updated in the record write transaction with pinned snapshots (start, pause, resume, stop, reopen resume/restart); durable `sla.check` timers publish `platform.sla.warning` and `platform.sla.breached`; breaches recorded once; running clocks shown in the draft review; SLA panel (`activity.sla`) on ticket, request and task pages; Tickets 1.5.0 (service desk calendar, ticket response/resolution, request fulfillment paused during approval, task completion). Review fixes: timers move on restore (new cycle) and workflow migrations; disabled policies never restart clocks; a deadline missed before a pause is recorded at the deadline; check job locks the record first; keyed timers are exempt from the job quota; calendar limits (4 intervals a day, 15-minute minimum, reachable goals); SLA conditions cannot read hidden fields; `config_revision` on timers. Open: historical breach reporting (G2-M9), module-upgrade test with running timers (next SLA-changing release).
- [ ] **G2-M5 Email intake.** Email adapter (fake plus one real provider), threading, sender policy, dedup, loop prevention, no internal-note disclosure. REQ-BIZ-003, REQ-EVT-007, REQ-ACC-13
- [ ] **G2-M6 Portal.** Portal page, request form, request status tracker, requester access. G2-M2 shipped a hard-coded catalog page and request form; G2-M6 turns them into registry elements and adds the status tracker with task progress for requesters. REQ-BIZ-004
- [ ] **G2-M7 Attachments.** Attachment field and panel, object storage adapter (fake plus real), object-level authorization, scanning and quarantine, limits. REQ-BIZ-006
- [ ] **G2-M8 One integration and webhooks.** Connector framework basics, one validated integration, outbound webhooks with idempotency and reconciliation. REQ-OPS-005, REQ-EVT-007
- [ ] **G2-M9 Metrics, dashboards and the connected explorer.** Reusable metric definitions (filter, measure, date range, permissions) shared by dashboards and the explorer so counts agree; KPI tile, bar chart and report spec with permission-aware aggregates; Tickets dashboard (open, unassigned, SLA breaches, workload by team, resolution trend); every figure drills into the explorer with exactly its filter; explorer keeps filters, sort, columns and position on return; configurable columns and grouping; a new field becomes available to the dashboard builder by type and permissions, and a customization request is one change across form, rules, explorer and dashboard ([connected workspace](spec/connected-workspace.md)). REQ-OPS-004, REQ-WS-002, REQ-WS-003, REQ-WS-004, REQ-WS-005
- [ ] **G2-M9b Guided workspace creation.** "Create a workspace" flow from a starter (Ticketing first): describe the goal, answer only questions without defaults, review forms, explorer, dashboard, navigation and permissions together in one draft, preview as each persona, publish together; honest empty states with clearly labeled preview data; acceptance REQ-ACC-15 (new admin creates the workspace, adds a field, drills from a dashboard figure into its tickets without code). REQ-WS-001, REQ-WS-002, REQ-WS-006, REQ-ACC-15
- [ ] **G2-M20 Multiple AI providers and bring-your-own subscription.** A provider abstraction for the server-side planner (Anthropic and OpenAI first, chosen per tenant, keys from configuration and later vault references), and "bring your own assistant": Esmbler's MCP server as a remote connector for ChatGPT and Claude with OAuth, so a person's own subscription does the reasoning while Esmbler enforces their authority. Design from the research in `docs/research/ai-providers-and-subscriptions.md` (in progress). REQ-AI-007, REQ-AISAFE-001, REQ-AISAFE-004
- [ ] **G2-M10 Environments and promotion.** Sandbox to prod diff, approve, deploy and roll back; synthetic sandbox data; no copied credentials or schedules. REQ-TEN-004, REQ-TEN-005
- [ ] **G2-M11 Enterprise identity.** Per-tenant OIDC and SAML, SCIM, MFA and passkeys (IdP registration is a human action). REQ-IAM-003
- [ ] **G2-M12 Admin Center and secrets.** Settings schema to auto-generated panels, standard sections, secrets as vault references. REQ-ADM-001, REQ-ADM-002
- [ ] **G2-M13 Observability and governors.** Scoped logs, metrics, traces; platform limits with clear errors. REQ-OPS-001, REQ-OPS-003
- [ ] **G2-M14 Backup and isolated tenant restore.** Backup jobs, restore-and-extract procedure, reconciliation, drill. REQ-OPS-002, REQ-ACC-12
- [ ] **G2-M15 Governance and theming.** Access reviews, JIT elevation, break-glass; bounded tenant theming with contrast checks. REQ-AUTHZ-011, REQ-DS-004
- [x] **G2-M17 Event platform v2.** Envelope v2 (source, subject, payload schema, trace context, severity, sensitivity) read alongside v1; event type registry in the SDK and module specs; `emits` on transitions and an `emit_event` rule action with payload type-checking; platform events for approvals and SLA; `config.published` as an envelope; per-consumer delivery state with independent retries, dead letters and replay; outbox retention ([ADR 0017](adr/0017-event-audit-alert-notification-services.md)). REQ-EVT-008, REQ-EVT-009 Done: envelope v2 (source, type version, emitter, severity, sensitivity, trace context, recorded-at; v1 shape kept, ASM-052) with v1/v2 contract tests; event type registry (`EventTypeSpec`, `event_type.add|remove`, platform types built in per ASM-051, generated AsyncAPI reference); `emits` and edge-triggered `emit_event` with type checks, namespace protection (no forged platform or foreign-module events) and hidden-field protection (ASM-053); approval platform events; `config.published` as an envelope (legacy rows still dispatch); one transaction per consumer with `kernel.consumer_failures`, per-consumer dead letters and audited replay; retention via `kernel.purge_outbox` (keeps events with dead consumers); Tickets 1.4.0 (`escalate`, ticket events); MCP `list_event_types`, `add_event_type`, `emit_event` in `add_rule`, `publish_events` in `update_transition`. Moved: SLA events to G2-M4; subscriptions and router to G2-M18.
- [ ] **G2-M18 Notification service.** Subscriptions spec object and subscription router (moved from G2-M17); channel abstraction; in-app inbox (API, bell, read state); email and SMS adapters (fake plus one provider each); channel instances in the Admin Center with vault references and capture mode; templates per channel; profiles; routes with module, tenant, team and user layers; resolution and dedup; per-recipient, per-channel delivery jobs with states, rate limits, idempotency, ambiguous outcomes, inspection and replay; preview and test send; MCP tools; existing templates and approval notifications moved onto routes. REQ-NOT-001 to REQ-NOT-007
- [ ] **G2-M19 Alerts and audit policy.** Alert rules, lifecycle, acknowledgment, escalation timers, auto-resolve, suppression, alerts page, SLA breach alerts; audit retention and legal hold, export, hash-chain verifier; the critical-escalation example as an acceptance test. REQ-ALR-001, REQ-AUD-001
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
