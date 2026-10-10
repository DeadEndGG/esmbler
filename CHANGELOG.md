# Changelog

All notable changes to Esmbler are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each completed gate is tagged `gate-N-complete`.

## [Unreleased]

Gate 1: architecture proof (in progress).

### Added

- Record engine with pinned configuration revisions, optimistic concurrency, idempotency keys, auto-numbers, reference validation, full-text search over non-restricted fields, audit, and outbox in one transaction. REQ-REC-003, REQ-REC-004, REQ-REC-007, REQ-EVT-001
- Identity service (login, tenant switching, AI agent principals, groups and roles with graph-sync ordering) and provisioning with two demo tenants. REQ-IAM-001, REQ-IAM-002, REQ-TEN-003

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
