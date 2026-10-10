# Execution Brief: AI-Native ESM Platform

Oct 10, 2026 · Daniel Maceiras

This brief tells the coding agent how to build the platform. The full product vision and requirements are in `docs/spec/master-spec.md`. Read both before starting. Where they differ, this brief wins.

---

## 1. Role, goal, and autonomy

You are the principal architect and lead engineer for an AI-native, composable Enterprise Service Management platform. The full product vision and requirements are in `docs/spec/master-spec.md`. Read it before starting. Build the whole platform it describes, gate by gate (Gate 0 through Gate 5), in the order its delivery gates define. Where this brief and the master spec differ, this brief wins.

**Goal:** a complete, working platform. The first proof inside it: a tenant gets a default Tickets module, customizes it by chatting with an AI through MCP (fields, rules, a page card), publishes, operates real records, and receives an upgrade without losing changes, while a second tenant is provably unaffected. Every later gate builds on that proof.

**Autonomous execution.** Work continuously until every gate is complete. Do not stop to ask for approval or permission to continue, and never end a turn with a question like "should I continue?". The user will test the finished product phase by phase afterward. Where you would otherwise stop:

- **Ambiguous requirement or missing input:** choose the most reasonable option, record it in `docs/decisions/assumptions.md`, continue.
- **Irreversible or expensive decision:** write an ADR with alternatives and rationale, choose, continue. Prefer reversible choices hidden behind interfaces.
- **Something only a human can provide** (cloud accounts, API keys, payment provider, email domain, model API key): build against a local adapter or fake, add an entry to `docs/human-actions.md`, continue.
- **A failure you cannot fix after a reasonable effort:** record it in `docs/known-issues.md` with reproduction steps, isolate it, continue with other work, and return to it before closing the gate.
- **Self-verification replaces approval:** a gate is complete only when its exit evidence passes automatically. Never start work that depends on a gate whose security, isolation, or data-integrity tests fail.

**Progress and resumption.** Keep `docs/progress.md` current after every milestone: current gate, milestone, done, next, blockers. If a session ends because of context or usage limits, the next session reads `AGENTS.md` and `docs/progress.md` and resumes exactly where work stopped, without asking.

**Phase checkpoints for user testing.** When a gate completes: commit, tag `gate-N-complete`, and write `docs/testing/gate-N-test-guide.md` with setup steps, seed data, and step-by-step manual test scenarios with expected results. The user checks out each tag to test that phase.

## 2. Product decisions

These defaults replace the [FILL IN] inputs in the master spec. Treat them as approved unless the user has edited this table. Record each in `docs/decisions/assumptions.md` and revisit through an ADR only if evidence shows it is wrong.

| Input | Default |
| --- | --- |
| First buyer | Internal IT service desks at companies with 200–5,000 employees; one organization per tenant. MSP mode is a later capability |
| First workflow | Intake → triage → assignment → optional approval and fulfillment tasks → resolution → requester confirmation |
| Pilot scale | 10 tenants, up to 2,000 users and 500,000 tickets per tenant, up to 200 custom fields per entity |
| Hosting | One cloud provider, one region, pooled multi-tenancy; every deployable containerized (Docker images, Docker Compose for local development, Kubernetes-ready manifests) so dedicated deployments are possible later |
| Language and frameworks | C# on the current .NET LTS release (ASP.NET Core) for the backend, Angular (current version, standalone components) for the frontend, one repository |
| Database | PostgreSQL (current major version) with row-level security; full-text search in Postgres until a dedicated search engine is justified |
| Background jobs | Postgres-backed durable job queue and workflow timers first; evaluate .NET options (e.g., Hangfire with PostgreSQL storage versus a messaging library with a Postgres outbox) and a dedicated workflow engine in an ADR |
| Authorization engine | Decide between OpenFGA, SpiceDB, and Cedar in an ADR during Gate 0, based on a spike with the Tickets permission model and the quality of each engine's .NET client |
| Extension runtime | WebAssembly components with one guest toolchain first (choose via a Gate 0 spike). Verify Component Model support in the .NET host; if insufficient, run the runtime as an isolated sidecar container. Added in Gate 3 |
| AI | Claude via the Anthropic API; the platform exposes its own MCP server for the builder |
| Data classification | No regulated data (health, payment card) in the pilot |
| External services | Email, payments, and object storage behind adapters, with local fakes for development and tests |

**Stack conventions**

- **Module boundaries:** each module is its own set of .NET projects; only its public contracts project is referenceable by others. Enforce with architecture tests (e.g., NetArchTest or ArchUnitNET) in CI.
- **Data access:** EF Core with Npgsql. Tenant and environment context is set per transaction (`SET LOCAL`) so row-level security holds with connection pooling. Runtime database roles never own tables or bypass RLS.
- **APIs:** ASP.NET Core with OpenAPI generated from the code; the MCP server is a separate ASP.NET Core service using the official C# MCP SDK if it meets requirements.
- **Frontend:** an Angular renderer maps each registry element to an Angular component; Angular CDK provides accessibility primitives; untrusted UI extensions run in sandboxed iframes.
- **Testing:** xUnit, Testcontainers for real PostgreSQL in integration tests, Playwright for end-to-end and acceptance scenarios.
- **Containers:** separate images for API, MCP server, background workers, frontend, and (from Gate 3) the extension runtime; one `docker compose up` starts the full system with seed data.

## 3. Builder experience

The AI builder is why customers choose this product. Safeguards in the master spec protect it; they must not make it feel slow or bureaucratic. Build it to behave like this:

1. **Understand:** the user describes what they want in plain language, optionally after selecting elements on the canvas (point-and-prompt).
2. **Clarify only when needed:** if the request is ambiguous, ask at most two short questions with suggested options. Otherwise proceed.
3. **Plan big changes:** for changes that touch schema, permissions, workflows, or more than five elements, show a short plan in plain English before applying.
4. **Apply to a draft:** changes land in the draft instantly and render as highlighted ghosts on the canvas.
5. **Explain:** summarize what changed in plain English, including the scope of any rule ("required on all forms and the API").
6. **Publish on approval:** one click, with the readiness checklist shown only when something needs attention.

**Live editing while chatting (core experience, Gate 1)**

The user customizes a page from the page itself, not from a separate builder screen:

- **Chat docked beside the real page:** on any page they are allowed to customize (for example the Tickets record page), the user opens the AI chat panel. The page switches into draft mode, clearly labeled, showing sandbox or sample data.
- **Changes appear as the agent works:** each change the AI makes (a field added to a card, a column added, a rule applied) renders on the page within about one second of the tool call, one step at a time, not all at once at the end. New or changed elements are highlighted, and removed ones fade out.
- **Chat and page stay linked:** each step in the chat links to its highlighted element on the page and the reverse. Clicking an element attaches it to the chat (point-and-prompt). Rules can be tried immediately in the draft ("set category to 8" shows Justification becoming required).
- **Control while it happens:** the user can stop the agent mid-change, undo any single step, or accept or discard the whole draft. Direct drag-and-drop edits mix freely with AI edits on the same draft.
- **Mechanism:** every MCP build tool writes a Command to the draft; the server pushes a draft-change event over a real-time channel (ASP.NET Core SignalR); the Angular renderer applies the updated spec incrementally without a page reload. Version checks prevent the user's edits and the AI's edits from overwriting each other.
- **Other users are unaffected:** everyone else keeps seeing the published page until the draft is published.

**Experience targets** (proposed; measure from Gate 1 on):

- Simple change (add a field to a card, make a field required): under 10 seconds from prompt to previewed draft at p50.
- Each AI change visible on the live page within 1 second of its tool call completing (p95), measured in the Playwright acceptance suite.
- Every AI change undoable in one step.
- Errors explained in the user's language with a suggested fix, never raw schema errors.
- At least 80% of the saved customization tasks in the AI evaluation suite completed without custom code and without unintended changes.

**Design direction: inspired by Linear and Notion**

Base the product's UX and visual design on the qualities that make Linear and Notion feel great. Learn from their interaction patterns and design principles; do not copy their branding, logos, icons, illustrations, or exact visual identity. The platform has its own name, palette, and personality.

- **From Linear:** speed as a feature (optimistic updates, instant navigation, no spinners for common actions); keyboard-first with shortcuts for everything and a command palette (Cmd/Ctrl+K); dense but calm lists and boards; minimal chrome and quiet colors so content leads; subtle, fast motion; excellent dark mode; inline editing of properties.
- **From Notion:** block-based composition, which maps directly onto the layout tree (every node is a block with a drag handle); a slash menu ("/") to insert elements, alongside the AI chat; inline editing everywhere instead of separate edit forms; clean typography with generous whitespace; pages that feel like flexible documents rather than rigid forms.
- **For the builder:** editing a page should feel like editing a Notion page, and working tickets should feel as fast as Linear. The slash menu, drag handles, command palette, and AI chat all write the same Commands to the same draft.
- **Capture it in the design system:** document these principles in `docs/explanation/design-principles.md`, encode them in tokens and components, and check new screens against them in the design review.

## 4. Scope and gate order

Follow the master spec's delivery gates in order, with these adjustments:

| Gate | Build | Adjustments to the master spec |
| --- | --- | --- |
| 0: Decisions and foundation | ADRs, threat model, repo scaffold, agent and documentation files, CI, element registry schema, authorization and extension-runtime spikes | Spikes produce working throwaway code plus an ADR each |
| 1: Architecture proof | Kernel, default Tickets module on the public SDK, two tenants, AI editing of fields, rules, and page cards via MCP, live editing while chatting, point-and-prompt selection, compile/preview/publish, one first-party Tickets upgrade | Package-conflict tests move to Gate 3; no third-party packages exist yet. No email intake or extension runtime yet |
| 2: Private pilot | Full request workflow, email and portal intake, SLA, approvals and tasks, attachments, one integration, basic reports, backup and tenant restore | Email, storage, and integration run against local fakes plus one real adapter |
| 3: Partner beta | Incidents and Knowledge, extension runtime, private modules, mods and config packs, SDK and CLI, package compatibility and conflict handling | Package-conflict acceptance scenario lands here |
| 4: Product expansion | Problems, Changes, CRM (support context only), Projects, CMDB, advanced reports, real-time collaboration | Order within the gate follows the dependency graph |
| 5: Public marketplace | Publisher onboarding, review pipeline, listings, licenses, billing and payouts, revocation | Payments run against a provider sandbox; live credentials are a human action |

Within each gate, build in vertical slices: data model, API, rules, UI, MCP tools, tests, and docs for one capability before the next. Do not build later-gate features early, even when convenient.

## 5. Gate 1 element subset

Implement only these elements in Gate 1, fully described in the registry (props schema, AI description, allowed parents and children, rule hooks, accessibility, examples). Later gates add elements from the master catalog as their modules need them, and the registry records each element's gate.

| Category | Gate 1 elements |
| --- | --- |
| Spec objects | Entity, Field definition, Relationship (lookup and 1:many), Choice list, Validation rule, Conditional rule, Workflow, Assignment rule, Notification template, Record view, List view, Form, Role, Permission set |
| Layout | Page, Section, Row, Column, Card, Tab group + Tab |
| Fields | Short text, Long text, Rich text (sanitized), Integer, Decimal, Date, Date-time, Single select, Multi-select, Toggle, Status, Priority, Email, Lookup, User, Group/Team, Auto-number ID |
| Display | Heading, Label/value pair, Badge, Callout, Empty state |
| Collections | Data table, Filter bar, Saved views, Related list |
| Record activity | Activity feed with public replies and internal work notes, Audit history |
| Actions | Button, Action menu, Workflow transition buttons, Quick create, Command palette |
| Feedback | Toast, Confirmation dialog, Inline error, Loading state |

Gate 2 adds at minimum: SLA policy and SLA timer, Approval policy and Approvals panel, Attachment field and Attachments panel, Portal page, Request form, Request status tracker, Catalog item, KPI tile, Bar chart, Report.

## 6. Agent instruction files

Create these in Gate 0 and keep them accurate in every gate. They are how any coding agent, including you in a new session, works correctly in this repository. `AGENTS.md` is the cross-tool open standard and the single source of truth; `CLAUDE.md` adds only what is specific to Claude Code. The repository ships with bootstrap versions of both; replace them in Gate 0.

**Root `AGENTS.md`** (target under 150 lines)

- One-paragraph product summary and pointers to `docs/spec/master-spec.md` and `docs/spec/execution-brief.md`.
- Non-negotiable invariants: tenant/environment isolation, no cross-module imports or data access, configuration as validated specs, first-party modules use only the public SDK, server-side enforcement of rules and permissions, no secrets in code or logs.
- Exact commands: setup, run locally, build, test (unit, integration, acceptance), lint, typecheck, boundary check, migrate, seed, docs lint.
- Conventions that differ from tool defaults (naming, error format, ID formats, test layout). Skip anything an agent can derive from the code, such as full directory trees.
- Definition of done (section 9) and autonomy rules (section 1), summarized.
- Where documentation lives: link `docs/README.md` and the most important docs by plain path so agents read them on demand.

**Root `CLAUDE.md`**

- First line: `@AGENTS.md` (imports it).
- Then Claude Code specifics only: use subagents for independent research and reviews, which hooks enforce what, when to run the reviewer subagents, how to resume from `docs/progress.md`.
- Keep the combined loaded size under about 200 lines. Use `@` imports only for small files every session needs, because imported files load into context at startup. Reference large docs by plain path instead.

**Nested files per module and package**

- Each module or package directory (for example `modules/tickets/`, `packages/kernel/`) gets its own short `AGENTS.md`: owned entities, published events and extension points, local invariants, local test commands, links to its docs.
- Next to each nested `AGENTS.md`, add a `CLAUDE.md` containing `@AGENTS.md`, so Claude Code loads it when working in that directory even though the root has a `CLAUDE.md`.

**Path-scoped rules: `.claude/rules/`**

- One topic per file with `paths` frontmatter so it loads only when matching files are touched. At minimum: database migrations, authorization and policy code, MCP tools, spec schemas, renderer and UI components, extension runtime, tests.

**Reviewer subagents: `.claude/agents/`**

- `boundary-reviewer`: checks module boundaries, dependency direction, public SDK use.
- `security-reviewer`: checks tenant isolation, authorization on every path, secrets, injection, sandbox escapes.
- `spec-reviewer`: checks changes against `docs/spec/master-spec.md` and the traceability matrix.
- `docs-steward`: checks that docs, ADRs, agent files, and generated references match the code.
- Run the relevant reviewers before closing each milestone and fix what they find.

**Enforcement belongs in tooling, not prose.** Anything that must always happen (lint, typecheck, boundary checks, tests, docs lint, secret scanning) runs in CI, in pre-commit hooks, and as Claude Code hooks where useful. Instruction files explain; tools enforce.

**Maintenance rules:** write concrete, verifiable instructions ("run `dotnet test --filter Category=Acceptance` before closing a milestone", not "test thoroughly"). When an agent repeats a mistake, add a rule. At every gate, remove stale or contradictory instructions.

## 7. Documentation system and standards

Documentation is docs-as-code: Markdown in the repository, reviewed with the code, linted in CI. Organize it with the Diátaxis framework (tutorials, how-to guides, reference, explanation), and keep `docs/README.md` as the index that `AGENTS.md` points to.

| Path | Contents | Standard |
| --- | --- | --- |
| `docs/README.md` | Index of all docs, one line each | Updated whenever a doc is added or moved |
| `docs/spec/` | `master-spec.md` and `execution-brief.md` (requirements sources, not edited by agents) and `traceability.md` mapping requirement → code → test → evidence | Requirement IDs referenced in code comments, tests, and commits |
| `docs/architecture/` | System overview and per-module architecture | C4 model levels 1–3 as Mermaid diagrams |
| `docs/adr/` | One file per decision: `NNNN-short-title.md` | MADR template; status proposed, accepted, superseded |
| `docs/decisions/assumptions.md` | Assumptions made instead of asking | One row per assumption, with impact and how to revisit |
| `docs/reference/` | Spec schemas, public API, events, MCP tools, element catalog, permissions | Generated from source: JSON Schema 2020-12, OpenAPI 3.1, AsyncAPI 3.0; never hand-edited |
| `docs/how-to/` | Task guides: add a field type, add an element, write a migration, add an MCP tool, publish a package | One task per file, numbered steps |
| `docs/tutorials/` | Learning paths: build your first module, customize Tickets with AI | End-to-end and runnable |
| `docs/explanation/` | Concepts: layered configuration, authorization model, event delivery, extension runtime, design principles | Why, not how |
| `docs/runbooks/` | Operator procedures from the master spec | Trigger, steps, verification, rollback |
| `docs/testing/` | Acceptance scenarios, AI evaluation suite, per-gate test guides | Each scenario: fixture, steps, expected result, evidence |
| `docs/progress.md`, `docs/human-actions.md`, `docs/known-issues.md` | Autonomous-run tracking (section 1) | Updated after every milestone |
| Root files | `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md` | Keep a Changelog, Semantic Versioning, Conventional Commits |

**Rules for every doc**

- Front matter: title, status, owner, last reviewed, related requirement IDs.
- One H1, short sections, relative links, diagrams in Mermaid so they diff in git.
- Reference docs are generated from code and schemas in CI; prose docs link to them instead of copying.
- CI runs Markdown lint, link checking, and a check that generated references are current.
- Agent files (`AGENTS.md`, `CLAUDE.md`, rules) link to docs; they never duplicate them.

## 8. Work plan

Run these steps in order without pausing between them.

**Step 1: Gate 0 outputs**

1. Repository scaffold: solution and project structure, Angular workspace, Dockerfiles, Docker Compose, CI pipeline (build, lint, typecheck, architecture tests, unit and integration tests, docs lint, secret scanning).
2. Agent instruction files (section 6), replacing the bootstrap versions, and the documentation skeleton (section 7).
3. `docs/decisions/assumptions.md` seeded from section 2.
4. ADRs: storage and query model, configuration artifact and overlay format, authorization engine (with spike), job and workflow execution, extension runtime (with spike), MCP server design, real-time channel for live editing.
5. Threat model covering tenant isolation, AI tool misuse and prompt injection, extensions, and secrets.
6. Architecture overview (C4 levels 1–3), element registry schema, Gate 1 element specs (section 5), and `docs/explanation/design-principles.md`.
7. Traceability matrix seeded with master spec requirements and their gates.
8. Acceptance test plan for every gate, mapped to the master spec's acceptance scenarios.
9. Full backlog in `docs/progress.md`, broken into milestones per gate.

**Step 2: Each gate, 1 through 5**

1. Update `docs/progress.md` with the gate's milestones.
2. Build each milestone as a vertical slice with tests and docs.
3. After each milestone: run the full test suite and the relevant reviewer subagents, fix findings, update progress, commit.
4. At gate end: run the gate's exit evidence and acceptance scenarios, update traceability, write `docs/testing/gate-N-test-guide.md`, update `CHANGELOG.md`, tag `gate-N-complete`.
5. Continue immediately to the next gate.

**Step 3: Completion**

When Gate 5 is tagged, produce `docs/final-report.md`: what was built per gate, test and acceptance results, measured targets versus goals, open known issues, assumptions to confirm, and the human actions required before production. Only then end the run.

## 9. Working rules and definition of done

**Working rules**

- Read `AGENTS.md`, `docs/progress.md`, and the relevant docs before each work session.
- `docs/spec/master-spec.md` and `docs/spec/execution-brief.md` are the requirements sources. Never edit them; record deviations as ADRs or assumptions.
- Never invent customer facts, metrics, or credentials. Use defaults from section 2, record assumptions, and use fakes for external services.
- Prefer the simplest design that satisfies the current gate while keeping the public contracts the master spec requires.
- Security, tenant isolation, and data integrity are never deferred, stubbed, or skipped to keep moving.
- Use subagents for independent research, spikes, and reviews so the main context stays focused.
- Commit in small steps with Conventional Commit messages that reference requirement IDs.

**Definition of done (every milestone)**

- Code builds in containers; `docker compose up` runs the system.
- Unit, integration, and relevant acceptance tests pass; architecture tests pass.
- Every write path enforces authorization and tenant/environment scope, with tests proving it.
- Generated reference docs are current; affected how-to, explanation, and architecture docs updated; ADRs written for decisions made.
- `AGENTS.md`, nested agent files, and rules still accurate.
- Relevant reviewer subagents have run and their findings are resolved or logged in `docs/known-issues.md`.
- `docs/progress.md` and the traceability matrix updated.
