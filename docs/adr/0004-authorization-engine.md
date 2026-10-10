---
title: "ADR 0004: Authorization engine"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-AUTHZ-001, REQ-AUTHZ-002, REQ-AUTHZ-003, REQ-TEN-001]
---

# ADR 0004: Authorization engine

## Context and problem statement

Esmbler needs hybrid RBAC + ABAC + ReBAC authorization at four levels: entity, record, field and action. The master spec, section "Authorization model", sets these requirements:

- Agents' effective access is the intersection of the user's permissions and the agent's delegated scopes.
- Policies are versioned specs that tenants can customize.
- Lists are pre-filtered at scale.
- Revocation has bounded staleness.
- Sensitive actions fail closed.
- Tenant/environment isolation is never deferred.

The execution brief asks us to choose between OpenFGA, SpiceDB and Cedar. The choice rests on a spike with the Tickets permission model and on the quality of each engine's .NET client.

Which engine should the kernel's policy decision point (PDP) use, and how should it be integrated?

## Decision drivers

- **ReBAC graph:** nested groups, roles assigned to users or groups, and delegation.
- **No dual-write of record attributes.** Requester, assignee, owner_group and watchers live on the record row. Mirroring them as stored tuples on every write would add a consistency problem.
- **List filtering for about 500k records per tenant** compiles into a SQL predicate. A conformance test must prove that the predicate equals the engine's Check.
- **Tenant/environment isolation** should be built into the engine, not just a naming convention.
- **ABAC conditions** over record attributes.
- **A maintained, official .NET client** with ergonomic APIs.
- **Self-hostable, with a PostgreSQL datastore,** to match the platform's database (PostgreSQL 18).
- **Explicit consistency control** for revocation, and predictable failure modes for failing closed.

## Considered options

1. **OpenFGA:** a Zanzibar-style service with CEL conditions, contextual tuples, and a store per tenant.
2. **SpiceDB:** a Zanzibar-style service with caveats and ZedTokens.
3. **Cedar:** an in-process policy language. Options are community .NET bindings, WASM, or AWS Verified Permissions.
4. **In-house:** relationship tables and evaluation in PostgreSQL. Listed for completeness.

| Criterion | OpenFGA | SpiceDB | Cedar |
|---|---|---|---|
| Official .NET client | `OpenFga.Sdk` 0.10.4 (2026-07-20, 3.9M downloads), a hand-written client, no third-party dependencies | `Authzed.Net` 1.6.0 (2026-05-26, 673k downloads), generated gRPC stubs only, pulls in legacy `Grpc.Core` 2.46.6 | None. Community: `MonoCloud.Cedar` 0.1.0 (native FFI, 25k downloads), `CedarDotNet` 0.0.8 (12k). `AWSSDK.VerifiedPermissions` is official but targets the managed AWS service |
| Record attributes without dual-write | **Contextual tuples on Check** (100 per request) | No. Only caveat context; relationships must be stored | Yes. Entities are passed per request |
| ReBAC graph traversal | Yes | Yes | No. The caller must load and pass the hierarchy |
| ABAC conditions | CEL conditions on tuples | CEL caveats | Native, with SMT-based policy analysis |
| Multi-tenancy primitive | **Stores**, each with its own models | None. Uses ID or definition prefixes, or separate deployments | N/A (in-process) |
| List support | ListObjects, ListUsers, StreamedListObjects | LookupResources (streaming) | None. Partial evaluation is not available in .NET |
| Consistency control | `MINIMIZE_LATENCY` / `HIGHER_CONSISTENCY` | ZedTokens (`at_least_as_fresh`, `fully_consistent`) | N/A |
| Datastore | PostgreSQL, MySQL, SQLite, memory | PostgreSQL, CockroachDB, Spanner, MySQL | None |

## Decision outcome

**Chosen option: OpenFGA**, accessed only through the kernel port `IRelationshipGraph` (adapter `OpenFgaRelationshipGraph`); decisions are made by the kernel PDP (`EffectivePermissions`).

It is the only option that meets three of the drivers at once:

- record attributes without dual-write (contextual tuples)
- an isolation primitive built into the engine (a store per tenant, with the model pinned per store; see [Refinement (2026-10-10)](#refinement-2026-10-10))
- an official, idiomatic .NET SDK

The spike also confirmed the rest: CEL conditions work, a PostgreSQL datastore is supported, and the list-predicate plan holds up. SpiceDB was a close second. It is stronger on consistency tokens and on gRPC latency, but it needs dual-writes and has no tenancy primitive. Cedar does not traverse a relationship graph, and it has no official in-process .NET option.

### Consequences

Good:

- Record rows remain the single source of truth for record attributes. The engine stores only memberships, role assignments, delegation and compiled policy grants. These change far less often than records.
- Tenant isolation is enforced twice. Each tenant has its own store, and model ids are scoped to a store: using one tenant's model id against another tenant's store failed with `authorization_model_not_found`.
- One HTTP client serves every store, because store and model are chosen per request.
- The model can be tested in CI with `fga model test`, without .NET.
- The port interfaces keep the engine swappable.

Bad:

- **We operate another stateful service.** It uses its own database in the PostgreSQL cluster, outside RLS, with isolation by store id.
- **Membership tuples are a derived copy** that needs an outbox and reconciliation.
- **The .NET SDK uses HTTP/JSON only.** Uncached, it was markedly slower than SpiceDB's gRPC path on the spike host, so the check cache and right-sized CPU are mandatory.
- **Request limits** cap a Write at 100 tuples per transaction and a Check at 100 contextual tuples.
- **There is no per-write consistency token.** Revocation relies on `HIGHER_CONSISTENCY` plus a bounded cache TTL.

## Spike evidence

Full method, raw numbers and SDK notes are in the [OpenFGA spike README](../../spikes/authz-openfga/README.md). The SpiceDB comparison is in [spikes/authz-spicedb](../../spikes/authz-spicedb/README.md).

Environment: OpenFGA v1.22.0, OpenFga.Sdk 0.10.4, .NET 10, PostgreSQL 18. The host was a contended 4-vCPU sandbox, so the latency figures are upper bounds.

- **Functional, 49/49 PASS:**
  - record-level view and edit (nested groups, roles assigned to groups)
  - the CEL priority guard
  - field-level and action-level checks (`config.publish` is separate from `permissions.admin`)
  - AI agent intersection
  - tenant isolation
  - revocation
  - fail closed
- **Contextual tuples vs stored tuples:** no measurable difference. With the memory store, the nested-group Check took 6.7 ms with contextual tuples and 5.9 ms with stored tuples (mean). With the PostgreSQL store, it took 6.5 ms and 7.4 ms. **Dual-writing is not needed.**
- **Check with the PostgreSQL datastore and check cache (TTL 10s):** 1.7 ms mean and 2.5 ms p95, with 3,127 checks/s at concurrency 32.
- **Uncached Check is CPU-bound on this host,** at about 6.5 ms and 81–153 checks/s. OpenFGA spent about 11 ms of CPU per nested Check on the HTTP gateway path.
- **ListObjects(user, member, group):** 1.3–3.0 ms with 2 groups, and 1.8–4.3 ms with 40 groups (20 of them nested).
  - Compiling the full list filter (ListObjects plus a BatchCheck of 6) takes 3.7–9.5 ms.
- **SQL conformance:** 0 mismatches across 10,656 (user, relation, row) cases between engine Check and the compiled SQL predicate, over 500k tickets per tenant.
  - The first page of a list query took 1–19 ms, and count(*) took 1–50 ms.
- **Store provisioning:** CreateStore plus WriteAuthorizationModel took 6.8–9.0 ms per tenant-environment (100 stores).
- **Revocation:** after deleting a nested-group membership, a `HIGHER_CONSISTENCY` Check returned deny immediately; the delete and check together took 10–14 ms.
- **Fail closed:**
  - A stopped engine raised `HttpRequestException` after about 1 s, even with `MaxRetry=0`.
  - A hung engine raised `TaskCanceledException` at the HttpClient timeout.
  - Validation failures raised `FgaApiValidationError`.
  - The adapter mapped all of these to deny.
- **SpiceDB:** 11/11 PASS on a subset of the model, with CheckPermission at 1.9 ms mean over gRPC. Ticket relationships had to be written as stored relationships.

## Integration design

### Refinement (2026-10-10)

The decision above said "a store per tenant-environment". The implementation uses **one OpenFGA store per tenant**, because identity (groups, group members, role assignments, agent delegation) is tenant-wide: the same people and groups apply in production and sandbox. Environment-specific policy lives in the permission-set specs of each environment's configuration, not in the store. The store and model IDs are kept on the tenant row (`kernel.tenants.authz_store_id`, `authz_model_id`).

### Policy decision point

The kernel PDP (`EffectivePermissions` in `packages/Esmbler.Kernel/Authorization/PolicyDecisionPoint.cs`) is the only consumer of authorization facts. For each request it combines:

1. relationship facts from the tenant's store (`AuthorizationFacts`: roles, direct or via nested groups, and transitive group memberships)
2. the versioned permission-set specs of the pinned configuration (entity, record, field and action levels)
3. record relations (`requester`, `assignee`, `owner_group`), checked in-process from the record's access fields, or through OpenFGA with contextual tuples (`IsRelatedAsync`)
4. for agents, the intersection with the agent's delegated scopes; agents never get `permissions.admin`, `config.publish`, `modules.manage` or `records.export`

It also produces an explanation ("why can X see this?", REQ-AUTHZ-009). Package capability and licence checks are planned with the extension runtime (Gate 3).

### Fixed platform model and tenant policy

- **The engine model is fixed and versioned by the platform** (`packages/Esmbler.Kernel.Infrastructure/Authorization/platform-model.fga`, JSON form `platform-model.fga.json`). Tenants never edit it. Types: `user`, `agent` (`acts_for`), `group` (`member`, nested through `group#member`), `role` (`assignee`) and `record` (`requester`, `assignee`, `owner_group`, `related`). Record relations are only ever sent as contextual tuples.
- **Tenant customization lives in versioned permission-set specs,** which go through the same overlay, draft and rollback process as all other config. The PDP evaluates them in-process; they are not compiled into tuples.

### Record attributes and lists

- **Record attributes are never stored as tuples.** The record row is the source of truth.
- **Lists are compiled into a SQL predicate** (`QueryCompiler.PermissionPredicate`): read scope `all` becomes `true`; scope `related` becomes one clause per entity access field (`data->>'requester' = <principal>`, `data->>'assignment_group' = ANY(<groups>)`). The predicate is part of `WHERE`, so it applies before ordering, paging and counting.
- **A conformance test** comparing the SQL predicate with `EffectivePermissions` and the in-memory fake (`InMemoryRelationshipGraph`) with OpenFGA is planned (G1-M3).

### Tenancy and system of record

- **One store per tenant**, created on demand by `EnsureTenantAsync` (`CreateStore` plus `WriteAuthorizationModel`) and recorded on the tenant row through `IAuthzStoreRegistry`.
- **The kernel tables `kernel.group_members` and `kernel.role_assignments` are the system of record.** The OpenFGA store is a derived index that can be rebuilt from them.

### Writes, consistency and revocation

- **Reads use `HIGHER_CONSISTENCY`** (`ListObjects` for facts, `Check` for record relations), so a revocation applies to the next request. There is no check cache in Gate 1.
- **Writes** use `TransactionOptions { Disable = true, MaxPerChunk = 100 }` and ignore duplicate writes and missing deletes, so replays are idempotent.
- Planned (G1-M2, G1-M17): the membership writer (revocations written to the graph before the database change commits; grants after commit, with retry through the outbox), a per-tenant `authz_revision` for cache keys, and a nightly reconciliation job that diffs the kernel tables against the store.

### Failure handling and testing

- **The adapter fails closed.** Each call has a deadline (`OpenFgaOptions.TimeoutMs`, default 2,000 ms) and at most one retry. Any error becomes `AuthorizationUnavailableException` (`authorization_unavailable`, HTTP 503), and callers deny. A tenant without a store is denied the same way.
- **The port is `IRelationshipGraph`** (`GetFactsAsync`, `IsRelatedAsync`, `WriteAsync`, `EnsureTenantAsync`). Unit tests use `InMemoryRelationshipGraph`; integration tests run OpenFGA in Testcontainers (planned with G1-M2). The OpenFGA adapter is the only code that references `OpenFga.Sdk`, enforced by an architecture test.
- **SDK usage is pinned to `OpenFga.Sdk` 0.10.4:** store and model are passed in the per-request options, and the `HttpClient` has an explicit timeout.

## Revisit triggers

- With the cache enabled on production-class hardware, Check p95 exceeds 10 ms, or list-filter compilation p95 exceeds 25 ms.
- `OpenFga.Sdk` goes 6 months without a release, falls behind server API features we need, or OpenFGA project health declines.
- We need more than 100 contextual tuples, per-write consistency tokens, or tenant-specific engine models.
- The number of stores, or the operational cost of the datastore, becomes a scaling problem at the tenant counts in the operating targets.
- Cedar gains an official .NET binding or partial evaluation suitable for SQL compilation. It would then become the in-process evaluator for tenant permission-set rules.
- SpiceDB adds contextual relationships and a tenancy primitive.
