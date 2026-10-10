---
title: Architecture overview
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-TEN-001, REQ-TEN-002, REQ-TEN-003, REQ-CFG-001, REQ-CFG-006, REQ-CFG-007, REQ-CFG-008, REQ-REC-001, REQ-EVT-001, REQ-EVT-002, REQ-AUTHZ-001, REQ-AI-003, REQ-AI-006, REQ-LIVE-002, REQ-MOD-002, REQ-EXT-001, REQ-OPS-006]
---

# Architecture overview

## One-page summary

Esmbler is an AI-native, composable Enterprise Service Management platform. It is a **modular monolith**: one repository and one set of .NET assemblies, deployed as a few containers that share a kernel library.

- **Kernel** (`packages/Esmbler.Kernel`, pure logic) and its adapters (`packages/Esmbler.Kernel.Infrastructure`: PostgreSQL, OpenFGA): tenant/environment context, identity and authorization enforcement (the PDP), the record engine, configuration compile/draft/publish, audit, the transactional outbox and the module registry. It knows no business-module internals.
- **Shared platform services** (`packages/Esmbler.Platform`): workflow (hook order 100), assignment (200) and rules (300), plugged into the kernel's `IRecordWriteHook`. In-app notifications are an event consumer in `Esmbler.Kernel.Infrastructure` (`NotificationConsumer`).
- **Business modules** (`modules/*`): declarative spec releases on the public SDK (`packages/Esmbler.Sdk`). Tickets is the first. Modules never reference the kernel; architecture tests enforce this ([ADR 0012](../adr/0012-repository-layout-and-module-boundaries.md)).
- **Configuration is a compiled artifact.** Base modules plus the tenant overlay (typed operations) compile into an immutable, content-hashed `EffectiveConfig`. Each request pins one config revision ([ADR 0003](../adr/0003-configuration-artifact-and-overlay-format.md), [layered configuration](../explanation/layered-configuration.md)).
- **Storage is pooled.** One PostgreSQL 18 database, schema `kernel`, a shared `kernel.records` table with JSONB data, and RLS with `FORCE` on every tenant table ([ADR 0002](../adr/0002-storage-and-query-model.md), [ADR 0010](../adr/0010-database-migrations-and-roles.md)).
- **Authorization** is a kernel PDP over versioned permission-set specs plus OpenFGA relationship facts ([ADR 0004](../adr/0004-authorization-engine.md), [authorization model](../explanation/authorization-model.md)).
- **The AI builder** uses Esmbler's own MCP server (`apps/Esmbler.Mcp`). The in-app chat (`packages/Esmbler.Assistant`, run by the API) drives Claude, or a deterministic scripted planner without an API key, as an MCP client with the user's delegated agent token ([ADR 0007](../adr/0007-mcp-server-design.md), [ADR 0013](../adr/0013-ai-assistant-model-adapter.md)).
- **Live editing:** every build tool writes a Command to the draft. A SignalR push lets the Angular renderer show each change in under a second (measured p95 about 390 ms) ([ADR 0008](../adr/0008-realtime-channel-for-live-editing.md)).
- **Extensions** (Gate 3) run in a Rust Wasmtime sidecar with no database credentials and no egress ([ADR 0006](../adr/0006-extension-runtime.md)).

### Build status

Gate 1 (architecture proof) is implemented: REST API, record engine, drafts and publish with activation health checks and rollback, module upgrades with resumable data migrations, the worker (outbox dispatcher, job runner, notifications), the MCP server and its tools, the SignalR live channel, the AI assistant (Claude or scripted planner) and the Angular renderer with live draft mode. Migrations are `0001_kernel.sql` to `0004_refresh_session_cap.sql`. What is not built yet is listed in [progress](../progress.md) and [known issues](../known-issues.md). The diagrams below show the Gate 1 system; Gate 2+ elements are labeled.

### Invariants

1. Tenant and environment come only from the authenticated session, never from record IDs or tool arguments.
2. Every write path goes through the record engine: authorization, rules, revision check, audit and outbox in one transaction.
3. Configuration changes are typed overlay operations. The runtime only consumes validated, immutable artifacts.
4. Modules talk only through events, versioned contracts and the relationship service.
5. Hidden field values never leave the server.

## Level 1: system context

```mermaid
flowchart LR
    agent([Service desk agent])
    requester([Requester])
    admin([Tenant admin / builder])
    claude[[Claude via Anthropic API]]
    extmcp[[External MCP client<br/>e.g. Claude Desktop]]
    esm[(Esmbler platform)]
    idp[[Tenant IdP: OIDC/SAML<br/>Gate 2]]
    email[[Email provider<br/>Gate 2]]
    storage[[Object storage<br/>Gate 2]]
    pay[[Payment provider<br/>Gate 5]]

    agent -- works tickets --> esm
    requester -- raises and follows requests --> esm
    admin -- customizes by chat, drag and drop, slash menu --> esm
    esm -- assistant loop: prompts + MCP tool results --> claude
    extmcp -- MCP over streamable HTTP, esmbler-mcp token --> esm
    esm -. SSO, SCIM .-> idp
    esm -. inbound and outbound mail .-> email
    esm -. attachments .-> storage
    esm -. billing and payouts .-> pay
```

| Actor or system | Interaction | Gate |
| --- | --- | --- |
| Agents, requesters, admins | Browser UI (Angular). Dev auth uses local passwords | 1 |
| Claude (Anthropic API) | Called by the API's assistant loop. A deterministic scripted planner replaces it when no API key is configured (`ANTHROPIC_API_KEY`, mapped to `Esmbler__Assistant__AnthropicApiKey`) | 1 |
| External MCP clients | Connect to `/mcp` with an agent token minted for a user (`POST /api/auth/agent-token`) | 1 |
| Email, object storage | Behind adapters with local fakes. Gate 1 notifications go to an in-app channel (`NotificationConsumer`) | 2 |
| Identity providers | Per-tenant OIDC/SAML/SCIM | 2 |
| Payment provider | Sandbox first; live credentials are a human action | 5 |

## Level 2: containers

```mermaid
flowchart TB
    browser([Browser])
    subgraph compose[Docker Compose / Kubernetes namespace]
        web[web<br/>Angular 22 static bundle]
        api[api<br/>ASP.NET Core: REST, SignalR /hubs/live,<br/>assistant loop]
        mcp[mcp<br/>ASP.NET Core + C# MCP SDK<br/>/mcp streamable HTTP]
        worker[worker<br/>outbox dispatcher, jobs,<br/>data migrations]
        pg[(postgres 18<br/>db esmbler: schema kernel<br/>db openfga)]
        fga[openfga v1.22<br/>store per tenant]
        ext[extension runtime sidecar<br/>Rust + Wasmtime 48 LTS<br/>Gate 3]
    end
    claude[[Anthropic API]]
    extclient[[External MCP client]]

    browser --> web
    browser -- REST + SignalR --> api
    api -- MCP client, agent token --> mcp
    api --> claude
    extclient --> mcp
    api -- esmbler_app role, RLS; migrator role for schema --> pg
    mcp -- esmbler_app role, RLS --> pg
    worker -- esmbler_app role, RLS --> pg
    api --> fga
    mcp --> fga
    worker --> fga
    fga --> pg
    api -. validate hooks, Gate 3 .-> ext
    worker -. async hooks, Gate 3 .-> ext
    mcp -- pg_notify esmbler_draft --> pg
    pg -- LISTEN --> api
```

| Container | Technology | Responsibilities | Data access |
| --- | --- | --- | --- |
| `web` | Angular 22 (standalone, signals, zoneless), Angular CDK | Shell, server-driven renderer (one component per registry element), live editing, command palette, chat panel | None directly; REST and SignalR to `api` |
| `api` | ASP.NET Core on .NET 10 | REST API (OpenAPI from code), auth endpoints, SignalR hub `/hubs/live`, assistant (`Esmbler.Assistant`: Anthropic SDK + MCP client), `DraftNotificationListener` (`LISTEN esmbler_draft`); `/health/live`, `/health/ready` | `esmbler_app` role via kernel; runs schema migrations as `esmbler_migrator` at startup (`Esmbler:MigrateOnStartup`) or with the `migrate` argument |
| `mcp` | ASP.NET Core + `ModelContextProtocol.AspNetCore` | Discovery, build, review, lifecycle and explain tools; resources `esmbler://registry/elements`, `esmbler://design-system/tokens`, `esmbler://app/graph`. Accepts only `esmbler-mcp` audience tokens | Same kernel services in-process, same authorization |
| `worker` | .NET generic host | Outbox and job dispatch through `kernel.claim_outbox` / `kernel.claim_jobs` (`FOR UPDATE SKIP LOCKED`, leases, backoff, dead letters), inbox dedup, record projection, in-app notifications, module-upgrade data migrations (`upgrade.run`); timers arrive in Gate 2 | `esmbler_app` only |
| `postgres` | PostgreSQL 18 | Database `esmbler`: `kernel` schema (records, audit log, outbox, inbox, jobs, config artifacts, drafts, identity) and `esmbler_meta.schema_migrations`; separate `openfga` database. Roles from `deploy/postgres/00-roles.sh` | RLS with `FORCE`; runtime role is not owner and has no `BYPASSRLS` |
| `openfga` | OpenFGA v1.22 | Relationship facts: nested group membership, role assignments, agent `acts_for`. A derived index of `kernel.group_members` / `kernel.role_assignments`, one store per tenant | Own database (`openfga`, role `esmbler_openfga`); isolation by store |
| extension runtime | Rust sidecar, `wasmtime =48.0.5` (Gate 3) | Runs tenant extension components with fuel, epoch and memory limits | None: no DB or vault credentials, egress denied |

## Level 3: components inside the API and kernel

```mermaid
flowchart LR
    subgraph host[Esmbler.Api / Esmbler.Mcp hosts]
        rest[REST endpoints]
        hub[SignalR hub<br/>/hubs/live]
        loop[Assistant<br/>Claude or scripted planner]
        tools[MCP tools]
        listen[pg_notify listener]
    end
    subgraph kernel[Esmbler.Kernel]
        ctx[Tenancy context<br/>tenant, env, principal, agent,<br/>pinned config revision]
        pdp[PDP<br/>entity, record, field, action]
        rec[Record engine]
        qc[Query compiler<br/>CEL subset to SQL]
        draft[Draft service<br/>Commands, undo, versions]
        cc[Config compiler<br/>Composer, Validator,<br/>DependencyGraph]
        pub[Publish service<br/>approval hash, migrations,<br/>pointer switch, rollback]
        audit[Audit writer]
        outbox[Outbox writer]
        reg[Registry<br/>modules + elements]
    end
    subgraph platform[Esmbler.Platform]
        rules[Rules service]
        wf[Workflow service]
        asg[Assignment]
    end
    subgraph infra[Esmbler.Kernel.Infrastructure]
        pgad[PostgreSQL adapter<br/>EF Core + Npgsql, set_config per transaction]
        fgaad[OpenFGA adapter]
    end

    rest --> ctx
    tools --> ctx
    loop --> tools
    rest --> rec
    rest --> qc
    tools --> draft
    tools --> pub
    rec --> pdp
    qc --> pdp
    rec --> rules
    rec --> wf
    rec --> asg
    rec --> audit
    rec --> outbox
    draft --> cc
    pub --> cc
    cc --> reg
    draft --> listen
    listen --> hub
    pdp --> fgaad
    rec --> pgad
    qc --> pgad
    draft --> pgad
    pub --> pgad
```

| Component | Responsibility | Key types or tables |
| --- | --- | --- |
| Tenancy context | Resolves tenant, environment, principal, agent and delegated scopes from the token. `Database.InTenantAsync` opens every transaction with `set_config('app.tenant_id', …, true)` | `RequestContext`, `Database`; `app.tenant_id`, `app.environment_id`, `app.principal_id` |
| Config compiler | Composes base modules (sorted by module ID), then applies overlay ops in order. Validates against the element registry and builds the dependency graph | `Composer`, `ConfigValidator`, `DependencyGraph`, `ConfigCompiler`, `ArtifactManifest` |
| Draft service | One open draft per tenant environment. Each edit is a Command with the expected draft version; a mismatch returns `409 draft_conflict` with the steps since. Undo marks a command undone (refused when later steps depend on it) | `kernel.drafts` (partial unique index `drafts_one_open`), `kernel.draft_commands`, `kernel.publish_requests` |
| Publish, activation and upgrade services | Recompile and compare the reviewed hash; switch `kernel.active_config` with optimistic concurrency and run `ActivationHealthCheck` in the same transaction (a failure rolls back); roll back to an earlier revision as a new one; module upgrades run checkpointed data migrations before the switch | `PublishService`, `ConfigActivator`, `UpgradeService`, `MigrationExecutor`; `kernel.config_artifacts`, `kernel.active_config` |
| Record engine | Create, update, transition, soft delete and restore (30 days). Checks revision and idempotency key, then runs PDP, the write pipeline (field permissions, normalization, defaults, hooks, required checks), audit and outbox in one transaction | `RecordWriteEngine`, `IRecordWriteHook`; `kernel.records`, `kernel.record_sequences`, `kernel.idempotency_keys`, `kernel.audit_log`, `kernel.outbox` |
| Query compiler | Typed query contract to parameterized SQL over JSONB. The permission predicate is ANDed before `ORDER BY`/`LIMIT`/`COUNT`; filtering on hidden fields is rejected | `QueryCompiler`: max page 100, offset ≤ 10,000, 64 filter nodes; `statement_timeout` 5 s |
| PDP | Policy decisions with an explain trace. Fails closed. Facts cached 5 s per `(tenant, principal, authz_revision)`; sensitive actions read fresh | `EffectivePermissions`, `AccessService`, `AuthzFactsCache`, `IRelationshipGraph` |
| Audit | Append-only change history with actor, agent and config revision. Runtime role has `INSERT`/`SELECT` only | `AuditWriter`, `kernel.audit_log` |
| Outbox | Writes event envelopes in the record transaction. The worker dispatches at least once | `kernel.outbox`, `kernel.inbox`, `kernel.record_projections`, `kernel.jobs` |
| Registry | Module releases and the element catalog. Read by the compiler, renderer and MCP | `IElementRegistry`, `IModuleDefinition` |
| Rules and workflow services | Evaluate data rules on every write path and declarative state machines in-transaction | `WorkflowHook`, `AssignmentHook`, `RulesHook`; `RuleSpec`, `WorkflowSpec` |
| Notifications | Event consumer that renders templates for record events into the in-app channel, showing each recipient only fields they may read | `NotificationConsumer` |
| SignalR hub | Group `draft:{tenant}:{env}` (builders only, joined with `WatchDraft`); pushes `draftChanged {d, v, s, k, targets, agent, at}` with IDs only, and the client reloads. Group `user:{principal}` carries `assistantEvent` progress | `LiveHub`, `DraftNotificationListener` |
| Assistant | Planner loop with bounded tool calls, model requests and time; acts only through MCP with a builder-scoped agent token; record text from tools arrives wrapped as `untrusted_data` | `IPlanner`, `ClaudePlanner`, `ScriptedPlanner`, `McpToolGateway`, `AssistantService` ([ADR 0013](../adr/0013-ai-assistant-model-adapter.md)) |

## Key flows

### Record write (one transaction)

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API
    participant K as Record engine
    participant P as PDP
    participant DB as PostgreSQL
    C->>A: PATCH /api/records/{entity}/{id} {revision, changes, configRevision} + Idempotency-Key
    A->>K: UpdateRecord(cmd) under pinned config revision
    K->>DB: BEGIN; set_config('app.tenant_id', ..., true)
    K->>P: can update record? fields writable?
    P-->>K: allow with trace, or deny (fail closed)
    K->>K: workflow guard, data rules, validation
    K->>DB: UPDATE kernel.records WHERE revision = expected
    K->>DB: INSERT kernel.audit_log
    K->>DB: INSERT kernel.outbox (record.updated envelope)
    K->>DB: COMMIT
    K-->>C: 200 with new revision (or 409 record_conflict, 422 rule errors)
```

### AI change visible on the live page

```mermaid
sequenceDiagram
    participant U as User (chat panel)
    participant L as Assistant loop (api)
    participant M as MCP server
    participant D as Draft service
    participant DB as PostgreSQL
    participant H as SignalR hub (api)
    participant R as Angular renderer
    U->>L: "Add a Vendor field to the Classification card"
    L->>M: tools/call add_field (place_in_view_node)
    M->>D: Command(op, expected_version)
    D->>DB: INSERT draft_commands; bump version; pg_notify('esmbler_draft', ids)
    DB-->>H: NOTIFY delivered on commit (DraftNotificationListener)
    H-->>R: draftChanged {v, s, targets, at}
    R->>R: reload draft and view, patch node tree by ID, highlight targets
    M-->>L: tool result (summary, targets)
    L-->>U: step "Added Vendor card" linked to the node
```

The p95 target from tool completion to render is under 1 s ([REQ-LIVE-002](../spec/traceability.md)); `tests/e2e/specs/experience.spec.ts` measured about 390 ms p95 over 20 samples, and about 650 ms p50 from prompt to previewed draft.

## Module dependency graph

### Build-time (assemblies)

```mermaid
flowchart BT
    expr[Esmbler.Expressions]
    sdk[Esmbler.Sdk]
    kernel[Esmbler.Kernel]
    infra[Esmbler.Kernel.Infrastructure]
    platform[Esmbler.Platform]
    assistant[Esmbler.Assistant]
    tc[Esmbler.Modules.Tickets.Contracts]
    tm[Esmbler.Modules.Tickets]
    hosting[Esmbler.Hosting]
    api[Esmbler.Api]
    mcp[Esmbler.Mcp]
    worker[Esmbler.Worker]
    sdk --> expr
    kernel --> sdk
    infra --> kernel
    infra --> platform
    platform --> kernel
    tc --> sdk
    tm --> sdk
    tm --> tc
    hosting --> infra
    hosting --> platform
    hosting --> tm
    api --> hosting
    api --> assistant
    mcp --> hosting
    worker --> hosting
```

Arrows point at the referenced project. The kernel uses `Esmbler.Expressions` through the SDK. These rules are enforced by `tests/Esmbler.ArchitectureTests/BoundaryTests.cs`:

- Modules reference only `Esmbler.Sdk`, `Esmbler.Expressions`, their own projects and other modules' `*.Contracts`.
- The SDK references nothing internal.
- The kernel references no platform, modules, infrastructure, Npgsql, EF Core or OpenFGA.
- The platform references no modules or infrastructure.
- The assistant references no kernel, platform, infrastructure, hosting, SDK or module assembly: it acts only through MCP.

### Runtime (spec modules)

```mermaid
flowchart BT
    kernelsvc[Kernel: tenancy, identity, PDP, records,<br/>config, audit, events, registry<br/>mandatory]
    platform[core.platform 1.0.0<br/>tenant admin role<br/>mandatory]
    shared[Shared services: rules, workflow,<br/>assignment, notifications<br/>mandatory while a module needs them]
    tickets[core.tickets 1.x<br/>optional module]
    incidents[core.incidents<br/>Gate 3]
    knowledge[core.knowledge<br/>Gate 3]
    later[Problems, Changes, CRM,<br/>Projects, CMDB<br/>Gate 4]
    platform --> kernelsvc
    shared --> kernelsvc
    tickets --> platform
    tickets --> shared
    incidents -. contracts + events .-> tickets
    knowledge --> shared
    later -. contracts + events .-> incidents
```

`core.tickets` declares `"dependencies": {"core.platform": "^1.0.0"}`. A shared service cannot be removed while an installed module depends on it.

## Transaction boundaries

| Command | Boundary | Consistency notes |
| --- | --- | --- |
| Record create, update, transition or delete | One PostgreSQL transaction: record row, audit entry and outbox event ([REQ-EVT-001](../spec/traceability.md)) | Revision check (`409 record_conflict`); idempotency key dedups retries |
| Event consumer | One transaction: inbox row `(consumer, event_id)` plus the handler's own writes | At-least-once delivery; per-record ordering by revision |
| Draft command (human or AI) | One transaction: `draft_commands` row, draft version bump and `pg_notify` | NOTIFY is delivered only on commit |
| Publish | One transaction: recompile and verify the reviewed hash, pointer switch with expected revision, audit, outbox `config.published`, activation health check (failure rolls back). Module upgrades first run expand/backfill migrations as a resumable job in checkpointed batches, then activate the same way | Not one atomic distributed transaction ([REQ-CFG-007](../spec/traceability.md), [REQ-MIG-002](../spec/traceability.md)) |
| Membership and role change | `kernel.group_members` / `kernel.role_assignments` (system of record) and an idempotent OpenFGA write; the `authz_revision` trigger bumps in the same transaction | Revocations written to OpenFGA before commit; grants after commit. Outbox retry for failed grant writes and scheduled reconciliation are not built yet |
| External side effects (Gate 2+) | Outside any DB transaction; provider idempotency keys or reconciliation | Never hold a transaction open on a remote call |
| Cross-module workflows (Gate 4) | Sagas with compensation over events | No cross-module transaction |

## Configuration revisions and caches

- Record writes send `configRevision`. A write under an incompatible older revision returns `409 config_conflict` with reload instructions ([REQ-CFG-008](../spec/traceability.md)).
- Active artifacts are cached by `(tenant, environment, revision)`; authorization facts by `(tenant, principal)` with `authz_revision` and a 5 s lifetime. No cache key omits the tenant ([REQ-TEN-002](../spec/traceability.md)).
- Active workflow instances pin their definition version. Authorization always uses current access rules.

## Service extraction path

Module boundaries keep extraction possible, but it is engineering work, not a switch.

| Concern | Today (monolith) | After extracting module X |
| --- | --- | --- |
| Code | X references only the SDK and Contracts | Unchanged; X moves to its own host |
| Data | X's records live in `kernel.records` under X's entity IDs | X keeps calling the record engine through a remote record API, or gets its own database plus a kernel adapter (larger change) |
| Events | Worker dispatches from the outbox in-process | An outbox relay publishes to a broker; X consumes with its own inbox |
| Contracts | In-process calls to `*.Contracts` | HTTP or gRPC with the same versioned contract |
| Authorization | In-process PDP | PDP library plus OpenFGA access, or a PDP sidecar |
| Configuration | Shared compiled artifact | X reads the artifact by revision (immutable, cacheable) |
| Transactions | Already no cross-module transactions | Unchanged; sagas already required |

Costs to plan for: a network hop on the record write path, broker operations, distributed tracing, and RLS in a second database.

## Deployment

- `docker compose -f deploy/docker-compose.yml up --build` starts postgres, openfga (after `openfga-migrate`), api, mcp, worker and web. Ports: web 8080, API 5080, MCP 5090 (`/mcp`), OpenFGA 8081, Postgres 55432. Compose sets `Esmbler__SeedDemoData=true`, so the API seeds demo tenants `acme` and `globex` (environments `prod` and `sandbox`) with sample tickets on first start ([REQ-OPS-006](../spec/traceability.md)). The browser reaches the API through the web container's nginx (`/api`, `/hubs`).
- Images: one per deployable. `deploy/docker/dotnet.Dockerfile` builds each .NET host (`APP` = `Esmbler.Api`, `Esmbler.Mcp` or `Esmbler.Worker`); `deploy/docker/web.Dockerfile` builds the Angular bundle served by nginx. Health checks run `dotnet <Host>.dll healthcheck <path>`, so runtime images need no curl. The extension runtime is added in Gate 3.
- Kubernetes manifests are planned and will not be exercised in CI at first ([assumptions](../decisions/assumptions.md)).

## Related documents

- [Threat model](../security/threat-model.md)
- [Tickets module architecture](modules/tickets.md)
- [Element registry schema](../reference/element-registry-schema.md)
- [Design principles](../explanation/design-principles.md)
- [Acceptance plan](../testing/acceptance-plan.md)
