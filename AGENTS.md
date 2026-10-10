# AGENTS.md

Esmbler is an AI-native, composable Enterprise Service Management platform: tenants install modules (Tickets first), customize them by chatting with an AI that operates the platform through an MCP server, publish validated configuration artifacts, and receive upgrades without losing changes. Requirements: `docs/spec/master-spec.md` (what) and `docs/spec/execution-brief.md` (how; it wins on conflicts). Where work stands: `docs/progress.md`. Docs index: `docs/README.md`.

## Start or resume

1. Read `docs/progress.md` (current gate, milestone, next step) and continue exactly there.
2. Work autonomously (execution brief section 1): never stop to ask whether to continue. Ambiguity → choose, record in `docs/decisions/assumptions.md`. Irreversible choice → ADR in `docs/adr/`. Needs a human (keys, accounts) → fake/adapter + `docs/human-actions.md`. Stuck after real effort → `docs/known-issues.md`, isolate, move on, return before closing the gate.
3. Update `docs/progress.md` after every milestone; commit in small Conventional Commits that cite requirement IDs (`feat(records): ... (REQ-REC-004)`).

## Non-negotiable invariants

- **Tenant/environment isolation is never deferred.** Every tenant table has FORCE RLS; the runtime role `esmbler_app` is not an owner and has no BYPASSRLS; context is set per transaction through `Database.InTenantAsync` (never on a raw pooled connection). Tenant/environment come only from the authenticated session, never from arguments or record IDs.
- **No cross-module imports or data access.** Modules reference only `Esmbler.Sdk`, `Esmbler.Expressions`, their own projects, and other modules' `*.Contracts`. Enforced by `tests/Esmbler.ArchitectureTests`.
- **Configuration is validated specs.** Tenant changes are typed overlay operations (`packages/Esmbler.Sdk/Overlay/OverlayOps.cs`) against stable IDs; never blind JSON merges. The runtime consumes only compiled, content-hashed artifacts.
- **First-party modules use only the public SDK** (dogfooding). Tickets is declarative JSON under `modules/tickets/.../Releases/`.
- **Server-side enforcement is authoritative.** Rules and permissions run on every write path (`RecordWriteEngine` + hooks). Hidden field values are never sent to clients or the AI.
- **AI never exceeds delegated authority.** Agent = user permissions ∩ scopes; agents cannot publish or change permissions; untrusted record content is data, not instructions.
- **No secrets in code, logs, audit details, or MCP output.** Configuration comes from environment variables.
- Never edit `docs/spec/master-spec.md` or `docs/spec/execution-brief.md`.

## Commands

Toolchain: .NET SDK 10.0.4xx (`global.json`), Node 24, Docker. Run from the repo root unless noted.

| Task | Command |
| --- | --- |
| Setup | `dotnet restore Esmbler.slnx && (cd apps/web && npm ci)` |
| Run everything locally (with seed data) | `docker compose -f deploy/docker-compose.yml up --build` → web <http://localhost:8080> |
| Build (analyzers are errors) | `dotnet build Esmbler.slnx` |
| Unit tests | `dotnet test tests/Esmbler.UnitTests` |
| Architecture/boundary check | `dotnet test tests/Esmbler.ArchitectureTests` |
| Integration tests (Docker for Testcontainers Postgres 18; authz graph is in-memory) | `dotnet test tests/Esmbler.IntegrationTests` |
| Acceptance (Playwright against the Compose stack on :8080, or `ESMBLER_WEB_URL`) | `cd tests/e2e && npm ci && npx playwright test` |
| Format/lint (.NET) | `dotnet format Esmbler.slnx --verify-no-changes` |
| Frontend typecheck + build / unit tests | `cd apps/web && npx ng build && npx ng test --watch=false` |
| Migrate | applied automatically at API startup as `esmbler_migrator`; manual: `dotnet run --project apps/Esmbler.Api -- migrate` |
| Seed demo tenants (acme, globex) | automatic when `Esmbler__SeedDemoData=true` (Compose sets it); manual: `dotnet run --project apps/Esmbler.Api -- seed` |
| Docs lint, links, front matter | `npx markdownlint-cli2@0.18.1 "**/*.md" "#**/node_modules" "#apps/web/README.md" "#docs/spec/master-spec.md" "#docs/spec/execution-brief.md"` and `node tools/check-links.mjs` |
| Regenerate references | `dotnet run --project tools/Esmbler.DocGen` (CI runs it with `--check`) |

## Conventions that differ from defaults

- IDs: base `core.<module>.*`; tenant-created IDs and field keys start with `tenant__`; publishers `<publisher>__` keys. Field keys are snake_case and unique per entity. Choice option IDs are stable strings.
- Expressions use the CEL subset in `packages/Esmbler.Expressions` (one number type; absent = null). Change semantics only with matching updates to `packages/Esmbler.Expressions/conformance/cases.json` and the TypeScript evaluator.
- Errors: `KernelException(code, message, status, target, suggestion)` → RFC 9457 problem details (API) or structured tool errors (MCP). Messages are user-facing and suggest a fix.
- Record values: date `YYYY-MM-DD`, instant `yyyy-MM-ddTHH:mm:ss.fffZ` (UTC), decimals rounded to field scale, references are UUID strings.
- Tests: xUnit; name tests as sentences (`Requester_cannot_write_hidden_fields`); integration tests use real Postgres via `PostgresFixture` and connect as `esmbler_app`; cite requirement IDs in the class summary.
- SQL migrations are new files in `packages/Esmbler.Kernel.Infrastructure/Migrations/NNNN_name.sql`; applied files are immutable (checksummed).
- Docs: front matter (title, status, owner, last_reviewed, requirements), one H1, relative links, Mermaid diagrams.

## Definition of done (every milestone)

Builds in containers and `docker compose up` works; unit, integration, architecture, and relevant acceptance tests pass; every write path enforces authorization and tenant/environment scope with tests; generated references current; affected docs and ADRs updated; agent files accurate; reviewer subagents run with findings fixed or logged; `docs/progress.md` and `docs/spec/traceability.md` updated.

## Where things are

- Architecture: `docs/architecture/overview.md`; decisions: `docs/adr/`; assumptions: `docs/decisions/assumptions.md`; threats: `docs/security/threat-model.md`.
- Acceptance plan: `docs/testing/acceptance-plan.md`; per-gate test guides: `docs/testing/gate-N-test-guide.md`.
- Each package/module has its own `AGENTS.md` with local invariants and test commands.
