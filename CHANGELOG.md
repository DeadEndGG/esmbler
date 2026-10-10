# Changelog

All notable changes to Esmbler are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each completed gate is tagged `gate-N-complete`.

## [Unreleased]

Gate 2: private pilot (in progress).

### Added

- Service requests (ADR 0015): Tickets 1.2.0 adds requests and fulfillment tasks, a service catalog with typed questions (New laptop, Software access, New hire onboarding), fulfillment tasks with dependencies and conditions, partial completion, rejection and cancellation; catalog page and request form; catalog and workflow overlay operations and MCP tools. REQ-BIZ-004, REQ-AI-003
- Versioned workflows: published workflow changes create definition versions; running records stay pinned, migrate through a state map, or are cancelled, as the change's policy says, and the publish review shows the effect. REQ-MIG-003, REQ-ACC-05
- Visual refresh of the web UI on Tailwind CSS v4 mapped onto the design tokens (ADR 0014): new sidebar and workspace switcher, sign-in with a product preview, segmented saved views, status badges with tone dots, card-based record page, AI builder panel with message bubbles and a step timeline, command palette, admin pages with page headers and table cards; Lucide icons; self-hosted Inter; refined light and dark tokens. axe passes in both themes. REQ-DS-001, REQ-DS-002, REQ-DS-003
- Durable jobs and timers: job handler registry with per-attempt re-authorization, checkpoints and lease renewal; idempotent, cancellable timers; jittered backoff; dead letters with audited operator replay (Operations page); tenant-interleaved event dispatch; automation depth, fan-out and queue quotas. REQ-EVT-005, REQ-EVT-006, REQ-TEN-006, REQ-ACC-11

### Fixed

- Module upgrades that skip a release now run that release's data migrations too.
- The sign-in page shows the demo account only where demo data is seeded (`GET /api/auth/sign-in-options`, KI-011).
- The production web build no longer depends on an inline `onload` script to load its stylesheet; the CSP blocked it, so deployed pages rendered without most styles. The CSP no longer allows Google Fonts hosts; Inter is served from the app's own origin.

## [0.2.0] - 2026-10-10 (`gate-1-complete`)

Gate 1: architecture proof. A tenant customizes the default Tickets module by chatting with the AI builder, watches the page change live, publishes, works tickets under the new rules, takes the 1.1.0 upgrade without losing changes, and a second tenant is unaffected.

### Added

- Record engine with pinned configuration revisions, optimistic concurrency, idempotency keys, auto-numbers, reference validation, full-text search over non-restricted fields, soft delete with 30-day restore, audit, and outbox in one transaction. REQ-REC-003, REQ-REC-004, REQ-REC-006, REQ-REC-007, REQ-EVT-001
- Identity service (login, tenant switching, AI agent principals with a kill switch, groups and roles with graph-sync ordering) and provisioning with two demo tenants. REQ-IAM-001, REQ-IAM-002, REQ-TEN-003
- REST API with problem details, OpenAPI, JWT access tokens, rotating refresh tokens (reuse detection, 24 h session cap), sign-in rate limits and account lockout. REQ-AI-004, REQ-AISAFE-004
- Query compiler with the permission predicate in SQL; benchmark at 100,000 tickets (all list shapes p95 at or below 231 ms on the CI database container). REQ-REC-002, REQ-AUTHZ-005
- Outbox dispatcher with inbox deduplication, revision-guarded projection, in-app notification consumer, and a fair, leased job runner that re-authorizes each job as its creator. REQ-EVT-002, REQ-EVT-004, REQ-AUTHZ-006
- Drafts with versioned commands, undo/redo with dependency checks, publish bound to the reviewed artifact hash, post-activation health check with automatic revert, compatibility-checked rollback, customizations list and reset to default. REQ-CFG-006, REQ-CFG-007, REQ-CFG-010, REQ-CFG-011
- Module upgrades with an upgrade report, checkpointed data migrations, fault injection and resume (Tickets 1.0.0 to 1.1.0). REQ-CFG-009, REQ-MIG-002
- Revocation across requests, permission caches (`authz_revision`), queued jobs and AI agent sessions. REQ-AUTHZ-006, REQ-ACC-07
- MCP server with discovery, build, review, lifecycle and explain tools, resources, audience validation, scopes, budgets, audit, and untrusted-content wrapping. REQ-AI-003, REQ-AI-006, REQ-AISAFE-001, REQ-AISAFE-003
- AI builder (`packages/Esmbler.Assistant`): Claude planner (`claude-opus-5-5`, configurable) and a built-in scripted planner, both acting only through MCP with the person's agent token; progress streamed live; plan announcements; 43-case evaluation suite. REQ-AI-005, REQ-AI-007, REQ-AISAFE-005
- SignalR live channel fed by Postgres `LISTEN/NOTIFY`; AI changes visible on the page p95 under 400 ms after the server applies them. REQ-LIVE-002
- Angular app: registry-driven renderer (one component per element), record, list and create pages, inline editing, saved views and filters, command palette and shortcuts, dark mode, draft mode with point-and-prompt, drag handles, slash insert, per-step undo, publish review, people and configuration administration. REQ-LAY-001, REQ-LAY-003, REQ-LIVE-001, REQ-LIVE-003, REQ-DS-002
- Acceptance evidence: `Gate1PathTests` through the API, preview-safety and revocation tests, Playwright scenarios (customization, isolation, concurrency, upgrade, preview, point-and-prompt, keyboard), axe accessibility scans and experience measurements. REQ-ACC-01, REQ-ACC-02, REQ-ACC-03, REQ-ACC-04, REQ-ACC-09, REQ-ACC-10, REQ-DS-003

### Security

- Gate 1 security review applied: builder-only reads for draft and administration data, name-only directory pickers, refusal of the public development signing key outside local use, no record access in the default AI delegation, forwarded-header handling behind the web proxy.

## [0.1.0] - 2026-10-10 (`gate-0-complete`)

Gate 0: decisions and foundation.

### Added

- Docker Compose stack (Postgres 18, OpenFGA, API, MCP, worker, web) started with one command; Dockerfiles per host; health checks. REQ-OPS-006
- CI pipeline: build with analyzers as errors, format, unit, architecture, integration (Testcontainers), frontend, docs lint, links and front matter, generated-reference check, gitleaks, acceptance job.
- Kernel database schema with FORCE row-level security on every tenant table, a non-owner runtime role, definer-owned privileged functions, and a checksummed SQL migration runner. REQ-TEN-001
- Policy decision point, record write pipeline, and platform rules, workflow, and assignment hooks. REQ-AUTHZ-002, REQ-RULE-002, REQ-RULE-004
- OpenFGA relationship-graph adapter (fail closed) and query compiler with the permission predicate inside SQL. REQ-AUTHZ-005, REQ-REC-003
- ADRs 0001–0013, assumptions register, threat model, human actions, known issues.
- Agent instruction files (`AGENTS.md`, `CLAUDE.md`, nested per package), path-scoped rules, and reviewer subagents.
- Reference generator (`tools/Esmbler.DocGen`) for spec JSON Schemas and the element catalog.

- .NET 10 solution (`Esmbler.slnx`) with central package management, analyzers as errors, and projects for the SDK, expressions, kernel, kernel infrastructure, platform, hosting, the API, MCP and worker hosts, and tests.
- Angular 22 workspace in `apps/web`.
- CEL-subset rule expression language (`Esmbler.Expressions`) with static checks, cost limits and shared conformance cases. REQ-RULE-003
- Public SDK (`Esmbler.Sdk`): spec model (entities, fields, choice lists, relationships, rules, workflows, views and layout nodes, roles, permission sets, assignment rules, notification templates, extension points, migrations), 15 typed overlay operations, the module API and the event envelope. REQ-CFG-002, REQ-AI-001, REQ-EVT-003
- Element registry with JSON Schema and 60 Gate 1 element definitions. REQ-AI-002, REQ-EL-001
- Kernel configuration compiler: composition with provenance and customized markers, three-way conflict detection, dependency graph, validation against the registry, and a content-hashed artifact manifest. REQ-CFG-001, REQ-CFG-004, REQ-CFG-005, REQ-CFG-006, REQ-CFG-009
- Default Tickets module releases 1.0.0 and 1.1.0 (declarative, public SDK only), with sample data and data migrations. REQ-MOD-001, REQ-MIG-001
- Architecture boundary tests (NetArchTest). REQ-MOD-002
- Authorization engine spike comparing OpenFGA and SpiceDB, and ADR 0004 choosing OpenFGA. REQ-AUTHZ-001
- Extension runtime spike and ADR 0006 choosing a Rust Wasmtime sidecar. REQ-EXT-001, REQ-EXT-003
- Traceability matrix seeded with master-spec requirements and gates.
- Documentation:
  - documentation index;
  - architecture overview (C4 levels 1–3) and Tickets module architecture;
  - element registry schema reference;
  - design principles;
  - layered configuration and authorization model explanations;
  - acceptance test plan;
  - full backlog in `docs/progress.md`;
  - root README, CONTRIBUTING, SECURITY and this changelog.
