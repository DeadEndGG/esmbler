---
title: Gate 0 test guide
status: accepted
owner: platform-lead
last_reviewed: 2026-10-10
requirements: [REQ-OPS-006, REQ-MOD-002, REQ-RULE-003, REQ-CFG-001, REQ-CFG-002, REQ-CFG-005, REQ-CFG-006, REQ-TEN-001, REQ-AUTHZ-001, REQ-EXT-001, REQ-EL-001]
---

# Gate 0 test guide

Gate 0 delivers decisions and the foundation: ADRs, assumptions, threat model, architecture, element registry, the spec model and configuration compiler, the database schema with row-level security, two spikes, CI, and a system that starts with one command. Check out the tag `gate-0-complete` to test this phase.

## Setup

1. Install Docker, the .NET 10 SDK (see `global.json`), and Node.js 24.
2. `git checkout gate-0-complete`
3. `dotnet restore Esmbler.slnx`

There is no seed data or user interface beyond the default web shell yet; those arrive in Gate 1.

## Scenarios

### 1. The system starts with one command

1. Run `docker compose -f deploy/docker-compose.yml up --build -d --wait`.
2. Run `docker compose -f deploy/docker-compose.yml ps`.
3. Open <http://localhost:8080/health/ready>.

Expected: `postgres`, `openfga`, `api`, `mcp` are healthy and `worker` and `web` are up; the health endpoint returns `{"status":"ready"}`; <http://localhost:8080> serves the web shell; the API log shows `Applied migration 0001_kernel` on the first start.

Behind a TLS-intercepting proxy, set `EXTRA_CA_FILE=/path/to/ca.pem` before building.

### 2. Automated checks pass

1. `dotnet build Esmbler.slnx` (analyzers are errors).
2. `dotnet test tests/Esmbler.UnitTests`
3. `dotnet test tests/Esmbler.ArchitectureTests`
4. `dotnet test tests/Esmbler.IntegrationTests` (starts PostgreSQL 18 in Docker)
5. `dotnet run --project tools/Esmbler.DocGen -- --check`
6. `node tools/check-links.mjs`

Expected: everything passes. At the time of tagging: 105 unit tests (75 expression conformance cases, configuration compiler, overlay and upgrade behavior, write pipeline and policy decisions), 5 architecture tests, 4 database isolation tests.

### 3. Tenant isolation holds in the database itself

Run `dotnet test tests/Esmbler.IntegrationTests --filter DatabaseIsolationTests`.

Expected: the runtime role `esmbler_app` sees only rows of the tenant and environment set for its transaction, sees nothing without a context, cannot insert another tenant's row (row-level security violation), is neither superuser nor `BYPASSRLS` nor table owner, every kernel table has `FORCE ROW LEVEL SECURITY`, and the audit log cannot be updated.

To see it by hand: connect with `psql -h localhost -p 55432 -U esmbler_app esmbler` (password `dev-only-app`) and run `SELECT count(*) FROM kernel.records;`. Expected: `0`, because no tenant context is set.

### 4. The configuration compiler protects tenants and upgrades

Run `dotnet test tests/Esmbler.UnitTests --filter "FullyQualifiedName~Configuration"`.

Expected: the Tickets releases 1.0.0 and 1.1.0 compile; the canonical customization (Vendor field, a conditional rule that requires Justification for access requests, a Vendor card on the record page) compiles with provenance and survives the upgrade to 1.1.0; a relabel that the new release also changed is reported as a conflict; a tenant field without the `tenant__` prefix is rejected with a suggested key; invalid layouts and props are rejected with actionable messages.

### 5. Decisions are recorded

Read `docs/adr/` (13 ADRs), `docs/decisions/assumptions.md`, `docs/security/threat-model.md`, and `docs/human-actions.md`.

Expected: every input from the execution brief section 2 appears as an assumption; each decision lists alternatives and revisit triggers; the authorization and extension-runtime ADRs cite their spikes (`spikes/authz-openfga/README.md`, `spikes/extension-runtime/README.md`).

### 6. The element registry is complete for Gate 1

Open `docs/reference/element-catalog.md` (generated).

Expected: every Gate 1 element from the execution brief section 5 is listed with props, allowed parents and children, constraints, and accessibility notes.

## Known limitations at this gate

- No login, records, AI chat, or record pages yet (Gate 1).
- OpenFGA runs but holds no stores until tenants are provisioned in Gate 1.
