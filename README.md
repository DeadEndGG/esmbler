# Esmbler

Esmbler is an AI-native, composable Enterprise Service Management platform. Every capability is a module that tenants install and combine. Tenants customize fields, rules, pages and workflows by chatting with an AI assistant, Claude, which operates the platform through Esmbler's own MCP server. Changes appear live on the real page as a draft, and nothing goes live until a human publishes.

The first module is **Tickets and Service Requests**. Later modules: Incidents, Knowledge, Problems, Changes, CRM (support context), Projects and CMDB. A partner SDK and a public marketplace follow.

> **Status:** Gate 0 (decisions and foundation). See [docs/progress.md](docs/progress.md) for the current milestone and the full backlog.

## What makes it different

- **Customize, never fork.** Default modules stay intact under a tenant overlay of typed operations. Upgrades merge three ways and never resolve a conflict silently.
- **Live editing while chatting.** Each AI change renders on the page within about a second and can be undone in one step.
- **Isolation built in.** Pooled PostgreSQL with forced row-level security. Every request runs in an explicit tenant and environment. One tenant's change never affects another.
- **One permission model everywhere.** Entity, record, field and action levels, enforced on the server. AI agents act with the intersection of the user's permissions and their delegated scopes.
- **Calm, fast UI.** Keyboard-first, a command palette, a slash menu, drag handles and inline editing.

## Stack

C# on .NET 10 (ASP.NET Core), Angular 22, PostgreSQL 18, OpenFGA v1.22, Docker Compose (Kubernetes-ready manifests). Claude via the Anthropic API.

## Quick start

Requirements: Docker with Compose v2.

```bash
docker compose up
```

This starts the web app, API, MCP server, worker, PostgreSQL and OpenFGA with seed data: tenants `acme` and `globex`, each with `prod` and `sandbox` environments, and sample tickets.

> The Compose file and seed are being added in milestone G0-M1. Ports, URLs and seed logins will be listed here when they land.

The AI assistant works without an API key, using a deterministic scripted planner. To use Claude, set `ANTHROPIC_API_KEY` in a local `.env` file (never commit it).

## Development

Requirements: the .NET 10 SDK (see `global.json`), Node.js with npm, and Docker for integration tests.

<!-- The lead fills in the final commands; AGENTS.md is the authoritative list. -->

```bash
# Backend
dotnet build Esmbler.slnx
dotnet test Esmbler.slnx                               # unit, architecture, integration (needs Docker)
dotnet test tests/Esmbler.UnitTests                    # unit only
dotnet test tests/Esmbler.ArchitectureTests            # module boundary checks

# Frontend
cd apps/web
npm ci
npx ng serve                                           # http://localhost:4200
```

Commands for lint, typecheck, migrate, seed, acceptance tests and docs lint will be added with the CI work (G0-M1, G0-M7).

## Repository layout

| Path | Contents |
| --- | --- |
| `packages/Esmbler.Sdk` | Public SDK: spec model, overlay operations, element registry, module and event contracts |
| `packages/Esmbler.Expressions` | CEL-subset rule expression language |
| `packages/Esmbler.Kernel` | Kernel: configuration compiler, and (in progress) tenancy, records, authorization, drafts and publish, audit, outbox |
| `packages/Esmbler.Kernel.Infrastructure`, `Esmbler.Platform`, `Esmbler.Hosting` | Adapters, shared platform services, composition root |
| `modules/tickets` | Default Tickets module (declarative releases) |
| `apps/Esmbler.Api`, `apps/Esmbler.Mcp`, `apps/Esmbler.Worker`, `apps/web` | Deployable hosts and the Angular app |
| `tests/` | Unit, architecture and integration tests; Playwright end-to-end tests (planned) |
| `spikes/` | Throwaway Gate 0 spike code with results |

## Documentation

Start at the [documentation index](docs/README.md). Key documents:

- [Architecture overview](docs/architecture/overview.md)
- [Layered configuration](docs/explanation/layered-configuration.md)
- [Authorization model](docs/explanation/authorization-model.md)
- [Design principles](docs/explanation/design-principles.md)
- [Acceptance test plan](docs/testing/acceptance-plan.md)

## Contributing and security

- [CONTRIBUTING.md](CONTRIBUTING.md): Conventional Commits with requirement IDs, PR flow, definition of done.
- [SECURITY.md](SECURITY.md): how to report a vulnerability, and the security invariants.
- [CHANGELOG.md](CHANGELOG.md): changes per gate.
