# Changelog

All notable changes to Esmbler are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each completed gate is tagged `gate-N-complete`.

## [Unreleased]

Gate 0: decisions and foundation (in progress).

### Added

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
