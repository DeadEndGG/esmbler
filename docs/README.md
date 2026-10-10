---
title: Documentation index
status: living
owner: platform-lead
last_reviewed: 2026-10-10
requirements: [all]
---

# Documentation index

Esmbler's documentation is docs-as-code. It is organized with the Diátaxis framework (tutorials, how-to guides, reference, explanation), plus project records. Update this index whenever a doc is added or moved. Entries marked *planned* do not exist yet; the gate says when they arrive.

## Start here

- [Progress and backlog](progress.md): current gate, milestone, next step and the full backlog.
- [Root README](../README.md): what Esmbler is and how to run it.
- [Contributing](../CONTRIBUTING.md): commits, pull requests, definition of done, docs rules.

## Tutorials

- *Planned (Gate 1):* `tutorials/customize-tickets-with-ai.md`: customize Tickets by chatting, from draft to publish.
- *Planned (Gate 3):* `tutorials/build-your-first-module.md`: build a module on the public SDK.

## How-to guides

- *Planned (Gate 1):* `how-to/add-an-element.md`: add a registry element and its renderer component.
- *Planned (Gate 1):* `how-to/add-a-field-type.md`: add a field type end to end.
- *Planned (Gate 1):* `how-to/write-a-migration.md`: write a SQL schema migration or a module data migration.
- *Planned (Gate 1):* `how-to/add-an-mcp-tool.md`: add an MCP tool with authorization and audit.
- *Planned (Gate 3):* `how-to/publish-a-package.md`: package, sign and install privately.

## Reference

- [Element registry schema](reference/element-registry-schema.md): element definition fields and the Gate 1 catalog. To be replaced by a generated reference.
- *Planned (Gate 1, generated):* spec JSON Schemas, OpenAPI 3.1, MCP tool catalog, AsyncAPI events, permissions.

## Explanation

- [Design principles](explanation/design-principles.md): Linear/Notion-inspired rules, tokens, component contract, design lint, review checklist.
- [Layered configuration](explanation/layered-configuration.md): base, packages and tenant overlay; provenance, upgrades and reset, with a worked example.
- [Authorization model](explanation/authorization-model.md): four levels, PDP and OpenFGA, AI intersection, list filtering, explain access.
- *Planned (Gate 1):* `explanation/event-delivery.md`: outbox, inbox, ordering and replay.
- *Planned (Gate 3):* `explanation/extension-runtime.md`: the sandbox model for extension authors.

## Specification

- [Master spec](spec/master-spec.md): product vision and requirements (not edited by agents).
- [Execution brief](spec/execution-brief.md): how the platform is built; wins over the master spec (not edited by agents).
- [Traceability matrix](spec/traceability.md): requirement IDs mapped to gate, code, tests and evidence.

## Architecture

- [Architecture overview](architecture/overview.md): one-page summary, C4 levels 1–3, module graph, transaction boundaries, service extraction.
- [Tickets module](architecture/modules/tickets.md): entities, workflow, rules, roles, pages, extension points, the 1.1.0 upgrade.

## Architecture decision records

- [0001 Record architecture decisions](adr/0001-record-architecture-decisions.md): MADR process and statuses.
- [0002 Storage and query model](adr/0002-storage-and-query-model.md): pooled PostgreSQL, shared JSONB records, typed query contract.
- [0003 Configuration artifact and overlay format](adr/0003-configuration-artifact-and-overlay-format.md): typed ops, three-way upgrades, artifacts, drafts, publish.
- [0004 Authorization engine](adr/0004-authorization-engine.md): OpenFGA with a kernel PDP (spike evidence).
- [0005 Job and workflow execution](adr/0005-job-and-workflow-execution.md): Postgres outbox, inbox and jobs; declarative workflows.
- [0006 Extension runtime](adr/0006-extension-runtime.md): Rust Wasmtime sidecar and the first guest toolchain (spike evidence).
- [0007 MCP server design](adr/0007-mcp-server-design.md): separate service, audience-bound tokens, tool families.
- [0008 Real-time channel for live editing](adr/0008-realtime-channel-for-live-editing.md): SignalR with `pg_notify`.
- [0009 Rule expression language](adr/0009-rule-expression-language.md): CEL subset in C# and TypeScript with conformance cases.
- [0010 Database migrations and roles](adr/0010-database-migrations-and-roles.md): plain SQL migrations, roles, RLS.
- [0011 Identity and development authentication](adr/0011-identity-and-development-authentication.md): principals, local login, JWT, agent tokens.
- [0012 Repository layout and module boundaries](adr/0012-repository-layout-and-module-boundaries.md): projects and NetArchTest rules.
- [0013 AI assistant model adapter](adr/0013-ai-assistant-model-adapter.md): Claude as an MCP client; scripted planner fallback.

## Decisions

- [Assumptions](decisions/assumptions.md): assumptions made instead of asking, with impact and how to revisit.

## Security

- [Threat model](security/threat-model.md): tenant isolation, AI tool misuse and prompt injection, extensions, secrets.
- [Security policy](../SECURITY.md): reporting, supported versions, security invariants.

## Runbooks

- *Planned (Gate 2):* `runbooks/`: failed jobs, controlled replay, migration recovery, package isolation, access revocation, tenant restore and offboarding. Each has trigger, steps, verification and rollback.

## Testing

- [Acceptance test plan](testing/acceptance-plan.md): every gate, REQ-ACC-01 to 14, the AI evaluation suite and experience targets.
- *Planned (at each gate close):* `testing/gate-N-test-guide.md`: setup, seed data and manual scenarios for each tagged gate.

## Tracking

- [Progress and backlog](progress.md): status and milestones for Gates 0–5.
- [Human actions](human-actions.md): what only a human can provide (keys, accounts, domains).
- [Known issues](known-issues.md): failures with reproduction steps and isolation notes.
- [Changelog](../CHANGELOG.md): Keep a Changelog, per gate.

## Spikes (throwaway code with results)

- [Authorization: OpenFGA](../spikes/authz-openfga/README.md) and [SpiceDB comparison](../spikes/authz-spicedb/README.md).
- [Extension runtime](../spikes/extension-runtime/README.md).
