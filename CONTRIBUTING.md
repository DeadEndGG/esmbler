# Contributing to Esmbler

Thanks for helping build Esmbler. This guide covers commits, branches and pull requests, the definition of done, and how documentation works. Agents follow the same rules; `AGENTS.md` summarizes them along with the exact commands.

## Before you start

1. Read [docs/progress.md](docs/progress.md) to see the current gate and milestone.
2. Read the docs for the area you will touch, starting from [docs/README.md](docs/README.md).
3. Never edit `docs/spec/master-spec.md` or `docs/spec/execution-brief.md`. Record deviations as an ADR in `docs/adr/` or a row in `docs/decisions/assumptions.md`.
4. Do not build features from a later gate, even when convenient.

## Commits

We use [Conventional Commits](https://www.conventionalcommits.org/) and reference requirement IDs from [docs/spec/traceability.md](docs/spec/traceability.md).

```text
<type>(<scope>): <summary in imperative mood>

<body: what and why>

Refs: REQ-TEN-001, REQ-REC-007
```

- **Types:** `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`, `spike`.
- **Scopes:** the area changed, such as `kernel`, `sdk`, `expressions`, `tickets`, `mcp`, `api`, `worker`, `web`, `infra`, `docs`, or a gate (`gate-0`) for cross-cutting scaffolding.
- **Requirement IDs:** every `feat`, `fix` and `test` commit lists the IDs it implements or verifies in a `Refs:` footer.
- **Breaking changes** to public contracts (SDK, specs, overlay ops, MCP tools, events, REST) use `!` after the type and a `BREAKING CHANGE:` footer.
- **Size:** commit in small steps; one logical change per commit.
- **Secrets:** never commit secrets, keys or `.env` files. The secret scan in CI blocks them.

Example:

```text
feat(kernel): enforce forced RLS on kernel.records

Adds tenant/environment policies on kernel.current_tenant() and
kernel.current_environment() (fail closed) and grants only DML to esmbler_app.

Refs: REQ-TEN-001
```

## Branches and pull requests

1. Branch from `main`: `<type>/<milestone>-<short-name>`, for example `feat/g1-m1-kernel-rls`.
2. Keep each PR to one milestone slice, or a coherent part of one.
3. Open the PR with:
   - the milestone ID and requirement IDs;
   - what changed and why;
   - how it was tested: commands and new test names;
   - docs updated;
   - any new ADR, assumption, human action or known issue.
4. CI must be green before merge: build, lint, typecheck, architecture tests, unit and integration tests, relevant acceptance tests, docs lint, link check, generated-reference check and secret scan.
5. Run the relevant reviewer subagents (`boundary-reviewer`, `security-reviewer`, `spec-reviewer`, `docs-steward`) and resolve their findings, or log them in `docs/known-issues.md`.
6. Squash-merge with a Conventional Commit title.
7. At a gate's end: tag `gate-N-complete`, write `docs/testing/gate-N-test-guide.md` and update `CHANGELOG.md`.

## Definition of done

Every milestone must meet all of these ([execution brief section 9](docs/spec/execution-brief.md)):

- [ ] The code builds in containers, and `docker compose up` runs the system.
- [ ] Unit, integration and relevant acceptance tests pass; architecture tests pass.
- [ ] Every write path enforces authorization and tenant/environment scope, with tests proving it.
- [ ] Generated reference docs are current. Affected how-to, explanation and architecture docs are updated. ADRs are written for decisions made.
- [ ] `AGENTS.md`, the nested agent files and the rules are still accurate.
- [ ] Relevant reviewer subagents have run, and their findings are resolved or logged in `docs/known-issues.md`.
- [ ] `docs/progress.md` and the traceability matrix are updated.

Security, tenant isolation and data integrity are never deferred, stubbed or skipped to keep moving.

## Code conventions

- **Module boundaries:** modules reference only `Esmbler.Sdk`, `Esmbler.Expressions`, their own projects and other modules' `*.Contracts`. Architecture tests enforce this ([ADR 0012](docs/adr/0012-repository-layout-and-module-boundaries.md)).
- **Tenant context:** comes only from the authenticated session. Never accept tenant or environment from a request body, record ID or tool argument.
- **Errors:** errors returned to callers carry `code`, `message`, `target` and `suggestion`. Write them so a person or an AI can fix the problem.
- **Tests:** test names include the requirement ID where one applies, for example `[Trait("Requirement", "REQ-ACC-01")]`.
- **UI:** specs and components use design tokens only. See [design principles](docs/explanation/design-principles.md).

## Documentation (docs-as-code)

- **Location:** Markdown lives in `docs/`, organized by Diátaxis. Add every new doc to [docs/README.md](docs/README.md).
- **Front matter:** every doc starts with:

  ```yaml
  ---
  title: Short title
  status: draft | accepted | living | superseded
  owner: role or team
  last_reviewed: YYYY-MM-DD
  requirements: [REQ-…]
  ---
  ```

- **Structure:** one H1, short sections, relative links, and diagrams in Mermaid so they diff in git.
- **Generated references:** `tools/Esmbler.DocGen` generates `docs/reference/schemas/*.schema.json` and `docs/reference/element-catalog.md` from code (`dotnet run --project tools/Esmbler.DocGen`); CI fails if they are stale (`--check`). OpenAPI, MCP tool, event and permission references follow in Gate 1. Never hand-edit them; prose docs link to them instead of copying.
- **ADRs:** use the MADR template, `docs/adr/NNNN-short-title.md`, with status `proposed`, `accepted` or `superseded`.
- **Agent files:** `AGENTS.md`, `CLAUDE.md` and `.claude/rules/*` link to docs and never duplicate them.
- **CI checks:** Markdown lint (`markdownlint-cli2`), relative link checking (`node tools/check-links.mjs`), and a check that generated references are current. Front-matter validation is planned.
