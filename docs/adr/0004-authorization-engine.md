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

**Chosen option: OpenFGA**, accessed only through the kernel ports `IAuthorizationEngine` and `IRelationshipGraph`.

It is the only option that meets three of the drivers at once:

- record attributes without dual-write (contextual tuples)
- an isolation primitive built into the engine (a store per tenant-environment, with models pinned per store)
- an official, idiomatic .NET SDK

The spike also confirmed the rest: CEL conditions work, a PostgreSQL datastore is supported, and the list-predicate plan holds up. SpiceDB was a close second. It is stronger on consistency tokens and on gRPC latency, but it needs dual-writes and has no tenancy primitive. Cedar does not traverse a relationship graph, and it has no official in-process .NET option.

### Consequences

Good:

- Record rows remain the single source of truth for record attributes. The engine stores only memberships, role assignments, delegation and compiled policy grants. These change far less often than records.
- Tenant isolation is enforced twice. Each tenant-environment has its own store, and model ids are scoped to a store: using one tenant's model id against another tenant's store failed with `authorization_model_not_found`.
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

### Policy decision point

The kernel PDP is the only caller of the engine. For each request it combines:

1. an engine Check, with contextual tuples derived from the record row and attributes passed as `context`
2. in-process tenant policy rules
3. field rules
4. the agent intersection, which requires the user's permission, the agent's delegated scope and the `acts_for` delegation all to be true (one BatchCheck)
5. package capability and licence entitlement checks

It returns a `Decision` with an explain trace.

### Fixed platform model and tenant policy

- **The engine model is fixed and versioned by the platform.** Tenants never edit it. It has generic types: `user`, `agent`, `group`, `role`, `entity_type`, record types, `field` and `tenant`.
  - It uses a fixed relation vocabulary: `requester`, `assignee`, `watcher`, `owner_group`, `entity`, and `read_all` / `edit_all` / `create`.
  - CEL conditions in the model are limited to platform-defined rules, for example the priority guard pattern and JIT or break-glass expiry.
- **Tenant customization lives in versioned permission-set specs,** which go through the same overlay, draft and rollback process as all other config. Activating a spec revision compiles it into two parts:
  - policy grant tuples, for example `role:X#assignee → entity_type:ticket#read_all`, `field:...#reader`, and `tenant:...#config_publish`
  - in-process ABAC predicates, which the PDP evaluates and the SQL compiler translates

### Record attributes and lists

- **Record attributes are always sent as contextual tuples.** They are never stored.
  - For direct user relations (requester, assignee, watcher), the PDP sends only the tuples whose subject is the current principal. This keeps every Check far below the 100-tuple cap.
- **Lists are compiled into a SQL predicate.** The PDP calls `ListObjects(user, member, group)` and checks `read_all` / `edit_all`. It also checks the guarded relation once for each enumerable attribute value.
  - These results become a SQL predicate that is applied before pagination and aggregation.
  - Search indexes and derived surfaces reuse the same compiled principal facts (group ids, role flags).
- **A conformance test runs in CI.** It uses OpenFGA in Testcontainers to compare engine Check against the SQL filter over generated rows and memberships, and to compare the in-memory fake against OpenFGA.

### Tenancy

- **Each tenant-environment has its own store.** A kernel table, protected by RLS, maps the tenant-environment to its store id and pinned model id.
- Provisioning a store is part of creating an environment. A sandbox never copies production tuples implicitly.

### Writes, caching and revocation

- **Relationship writes use a transactional outbox.** Membership and role changes commit in PostgreSQL, and an idempotent writer applies them to the store in chunks of 100 or fewer. It also bumps a per-store `authz_revision`.
- **A nightly reconciliation job** diffs PostgreSQL against `Read()`.
- **Staleness is bounded.** The OpenFGA check cache has a TTL of 10 s or less. Compiled list filters are cached by (store, principal, `authz_revision`), with a 30 s safety TTL.
- **Revocations are dispatched synchronously,** with the outbox as fallback.
- **Sensitive actions use `HIGHER_CONSISTENCY`.** These are `permissions.admin`, `config.publish`, export, break-glass, and package installs.

### Failure handling and testing

- **The PDP fails closed.** Each call has a deadline of about 250 ms through a `CancellationToken`, and a circuit breaker stops calls to an unhealthy engine. Unavailability or engine errors become deny, an audited `authorization_unavailable` response, and a metric.
  - Callers never fall back to a cached allow for sensitive actions.
  - When the engine is unavailable, list endpoints serve no results.
- **The ports are `IAuthorizationEngine`** (Check, BatchCheck, ListObjects, Expand for explain) **and `IRelationshipGraph`** (provision store and model, write and delete tuples, read for reconciliation).
  - Unit tests use the in-memory fake `InMemoryAuthorizationEngine`, which implements the fixed model.
  - The OpenFGA adapter is the only code that references `OpenFga.Sdk`. An architecture test enforces this.
- **SDK usage is pinned to `OpenFga.Sdk` 0.10.4:**
  - Store and model are passed in the per-request options.
  - The `HttpClient` has an explicit timeout.
  - Writes use `TransactionOptions { Disable = true, MaxPerChunk = 100 }`.
  - The PDP always sends every attribute a condition references, because a missing parameter returns HTTP 400 rather than deny.

## Revisit triggers

- With the cache enabled on production-class hardware, Check p95 exceeds 10 ms, or list-filter compilation p95 exceeds 25 ms.
- `OpenFga.Sdk` goes 6 months without a release, falls behind server API features we need, or OpenFGA project health declines.
- We need more than 100 contextual tuples, per-write consistency tokens, or tenant-specific engine models.
- The number of stores, or the operational cost of the datastore, becomes a scaling problem at the tenant-environment counts in the operating targets.
- Cedar gains an official .NET binding or partial evaluation suitable for SQL compilation. It would then become the in-process evaluator for tenant permission-set rules.
- SpiceDB adds contextual relationships and a tenancy primitive.
