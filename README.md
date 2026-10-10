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

C# on .NET 10 (ASP.NET Core), Angular 22, PostgreSQL 18, OpenFGA v1.22, Docker Compose (Kubernetes manifests planned). Claude via the Anthropic API.

## Quick start

Requirements: Docker with Compose v2.

```bash
docker compose -f deploy/docker-compose.yml up --build
```

This builds and starts PostgreSQL 18, OpenFGA, the API, the MCP server, the worker and the web app. The API applies database migrations at startup.

| Service | URL |
| --- | --- |
| Web app | <http://localhost:8080> |
| API | <http://localhost:5080> (`/health/ready`, `/openapi/v1.json`) |
| MCP server | <http://localhost:5090/mcp> |
| OpenFGA | <http://localhost:8081> |
| PostgreSQL | `localhost:55432` |

Credentials in the Compose file are development-only defaults; override them with environment variables or a local `.env` file (never commit it). Seed data (tenants `acme` and `globex`, each with `prod` and `sandbox` environments, users and sample tickets) is planned for milestone G1-M2.

The AI assistant (planned for Gate 1) works without an API key, using a deterministic scripted planner. To use Claude, set `ANTHROPIC_API_KEY` in `.env`.

## Development

Requirements: the .NET 10 SDK (see `global.json`), Node.js 24 with npm, and Docker for integration tests. [AGENTS.md](AGENTS.md) is the authoritative command list.

```bash
# Backend
dotnet build Esmbler.slnx                              # analyzers are errors
dotnet test tests/Esmbler.UnitTests                    # unit
dotnet test tests/Esmbler.ArchitectureTests            # module boundary checks
dotnet test tests/Esmbler.IntegrationTests             # real PostgreSQL via Testcontainers (needs Docker)
dotnet format Esmbler.slnx --verify-no-changes         # format check
dotnet run --project apps/Esmbler.Api -- migrate       # apply migrations (needs Esmbler__Database__Migrator)

# Frontend
cd apps/web
npm ci
npx ng build && npx ng test --watch=false

# Docs and generated references
node tools/check-links.mjs
dotnet run --project tools/Esmbler.DocGen              # regenerates docs/reference/schemas and element-catalog.md
```

## Repository layout

| Path | Contents |
| --- | --- |
| `packages/Esmbler.Sdk` | Public SDK: spec model, overlay operations, element registry, module and event contracts |
| `packages/Esmbler.Expressions` | CEL-subset rule expression language |
| `packages/Esmbler.Kernel` | Kernel logic: configuration compiler, request context, record write pipeline, policy decision point |
| `packages/Esmbler.Kernel.Infrastructure` | PostgreSQL (SQL migrations, RLS unit of work), OpenFGA adapter, query compiler |
| `packages/Esmbler.Platform`, `packages/Esmbler.Hosting` | Shared platform services (workflow, assignment, rules hooks); composition root |
| `modules/tickets` | Default Tickets module (declarative releases) |
| `apps/Esmbler.Api`, `apps/Esmbler.Mcp`, `apps/Esmbler.Worker`, `apps/web` | Deployable hosts and the Angular app |
| `tests/` | Unit, architecture and integration tests; Playwright end-to-end tests (planned) |
| `deploy/` | Docker Compose, Dockerfiles, PostgreSQL role setup |
| `tools/` | Reference generator (`Esmbler.DocGen`) and docs link check |
| `spikes/` | Throwaway Gate 0 spike code with results |

## Documentation

Start at the [documentation index](docs/README.md). Key documents:

- [Architecture overview](docs/architecture/overview.md)
- [Layered configuration](docs/explanation/layered-configuration.md)
- [Authorization model](docs/explanation/authorization-model.md)
- [Design principles](docs/explanation/design-principles.md)
- [Acceptance test plan](docs/testing/acceptance-plan.md)
- [Element catalog](docs/reference/element-catalog.md) (generated)

## Contributing and security

- [CONTRIBUTING.md](CONTRIBUTING.md): Conventional Commits with requirement IDs, PR flow, definition of done.
- [SECURITY.md](SECURITY.md): how to report a vulnerability, and the security invariants.
- [CHANGELOG.md](CHANGELOG.md): changes per gate.
