---
title: "ADR 0005: Job and workflow execution"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-EVT-001, REQ-EVT-002, REQ-EVT-003, REQ-EVT-004, REQ-EVT-005, REQ-EVT-006, REQ-TEN-002, REQ-TEN-006, REQ-MIG-002, REQ-MIG-003, REQ-ACC-06, REQ-ACC-11]
---

# ADR 0005: Job and workflow execution

## Context and problem statement

Record changes must produce events reliably (notifications, assignment, projections, OpenFGA tuple sync), configuration publishes run data migrations, and Gate 2 adds SLA timers and approvals. The master spec ("Transactions, events, and durable execution") requires a record change, its audit entry and its outbox event to commit together, at-least-once delivery with durable deduplication, per-record ordering, leases, backoff, dead letters, and tenant fairness. The execution brief asks us to compare Hangfire with PostgreSQL storage, a messaging library with a Postgres outbox, and a dedicated workflow engine.

## Decision drivers

- One transaction for record + audit + outbox, on the same connection and RLS context.
- Every job runs inside an explicit tenant/environment context; a job can never run "cross-tenant".
- One overloaded tenant cannot starve others (REQ-ACC-11).
- No extra infrastructure at pilot scale (10 tenants).

## Considered options

1. **Own Postgres outbox, inbox and jobs tables with a small dispatcher** in `apps/Esmbler.Worker`.
2. Hangfire with PostgreSQL storage.
3. MassTransit or Wolverine with their EF Core / Postgres outbox.
4. Dedicated workflow engine (Temporal, Elsa).

## Decision outcome

**Chosen option 1.** Workflow state machines are declarative (`WorkflowSpec`) and are executed in the command transaction by the platform workflow service (`packages/Esmbler.Platform`); only side effects and timers go through the outbox and jobs.

### Tables

Abridged from `packages/Esmbler.Kernel.Infrastructure/Migrations/0001_kernel.sql` (the authoritative definition):

```sql
CREATE TABLE kernel.outbox (
  id uuid PRIMARY KEY,                       -- = EventEnvelope.EventId
  tenant_id uuid NOT NULL, environment_id uuid NOT NULL,
  type text NOT NULL,                        -- record.updated, config.published, ...
  record_id uuid, record_revision integer,   -- ordering key
  event jsonb NOT NULL,                      -- Esmbler.Sdk.Events.EventEnvelope
  status text NOT NULL DEFAULT 'pending',    -- pending | dispatched | dead
  attempts integer NOT NULL DEFAULT 0,
  available_at timestamptz NOT NULL DEFAULT now(),
  locked_until timestamptz, locked_by text, last_error text,
  created_at timestamptz NOT NULL DEFAULT now(), dispatched_at timestamptz
);
CREATE INDEX outbox_pending ON kernel.outbox (available_at) WHERE status = 'pending';

CREATE TABLE kernel.inbox (
  consumer text NOT NULL, event_id uuid NOT NULL,
  tenant_id uuid NOT NULL, environment_id uuid NOT NULL,
  processed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (consumer, event_id)
);

-- Projections remember the last applied record revision and ignore older events.
CREATE TABLE kernel.record_projections (
  tenant_id uuid NOT NULL, environment_id uuid NOT NULL, consumer text NOT NULL,
  record_id uuid NOT NULL, record_revision integer NOT NULL, state jsonb NOT NULL, ...
);

CREATE TABLE kernel.jobs (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, environment_id uuid NOT NULL,
  kind text NOT NULL,                        -- config.migration, workflow.timer, authz.sync, ...
  payload jsonb NOT NULL, checkpoint jsonb,  -- resumable progress
  status text NOT NULL DEFAULT 'pending',    -- pending | running | succeeded | failed | dead | cancelled
  attempts integer NOT NULL DEFAULT 0, max_attempts integer NOT NULL DEFAULT 5,
  available_at timestamptz NOT NULL DEFAULT now(), locked_until timestamptz, locked_by text,
  last_error text, created_by uuid,          -- whose authority the job runs with
  created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
);
```

All of these tables have `FORCE ROW LEVEL SECURITY` with the tenant-and-environment policy ([ADR 0010](0010-database-migrations-and-roles.md)).

### Dispatch

The worker cannot see rows across tenants under RLS. It claims work through two narrow `SECURITY DEFINER` functions owned by the `NOLOGIN BYPASSRLS` role `esmbler_definer`, each with `SET search_path = kernel, pg_temp`. Both take `(p_worker text, p_batch integer, p_lease_seconds integer)`, lock rows with `FOR UPDATE SKIP LOCKED`, set `locked_until`/`locked_by`, increment `attempts`, and return identifiers only:

- `kernel.claim_outbox` returns `(id, tenant_id, environment_id, attempts)` for pending events whose lease is free, oldest first.
- `kernel.claim_jobs` returns `(id, tenant_id, environment_id, kind, attempts)` and is fair across tenants: at most one job per tenant per claim round, and none for a tenant that already has a running job with a live lease. Claimed jobs move to `running`.

```sql
-- inside kernel.claim_jobs (abridged)
WITH candidates AS (
  SELECT DISTINCT ON (j.tenant_id) j.id FROM kernel.jobs j
  WHERE j.status = 'pending' AND j.available_at <= now()
    AND NOT EXISTS (SELECT 1 FROM kernel.jobs r
                    WHERE r.tenant_id = j.tenant_id AND r.status = 'running' AND r.locked_until > now())
  ORDER BY j.tenant_id, j.available_at
), next AS (
  SELECT j.id FROM kernel.jobs j JOIN candidates c ON c.id = j.id
  ORDER BY j.available_at LIMIT p_batch FOR UPDATE OF j SKIP LOCKED
)
UPDATE kernel.jobs j SET status = 'running', locked_until = now() + make_interval(secs => p_lease_seconds), ...
```

For each claimed item the worker opens a transaction through `Database.InTenantAsync` (which sets `app.tenant_id`/`app.environment_id` with `set_config(..., true)`), reads the item under RLS, and runs the handler. The worker dispatcher and handlers are planned for G1-M4; the tables and claim functions exist.

- **Leases:** `p_lease_seconds` (60 s planned), renewed by long jobs. An outbox event whose lease expired is claimable again (it stays `pending` until dispatched). `claim_jobs` claims only `pending` jobs, so a sweeper that returns `running` jobs with expired leases to `pending` is planned with the dispatcher.
- **Backoff:** `min(5 s × 2^attempts, 15 min)` with ±20% jitter.
- **Dead letter:** after `max_attempts` (default 5) the status becomes `dead` with `last_error`; operators inspect and replay (Gate 2 UI; replay is audited and re-authorizes the principal).
- **Inbox:** a handler inserts `(consumer, event_id)` in the same transaction as its effect; a duplicate key means "already done" and the item is acknowledged.
- **Per-record ordering and stale revisions:** `claim_outbox` does not order per record, so consumers must tolerate reordering: projections (`kernel.record_projections`) store the last applied `record_revision` and ignore lower ones; business-transition consumers detect gaps and reload the record.
- **Cycles and fan-out (planned):** a causation depth above 8 (carried in the event envelope) stops automation with an audited `automation.depth_exceeded`; fan-out per event is capped at 100 jobs (Gate 2 governor).
- **Tenant isolation of jobs:** a job always carries tenant, environment and principal (`created_by`); the handler re-checks the principal's permissions before acting (REQ-ACC-07).

```mermaid
sequenceDiagram
    participant API as API command
    participant DB as PostgreSQL
    participant W as Worker
    participant H as Handler
    API->>DB: BEGIN; SET LOCAL tenant; UPDATE record; INSERT audit; INSERT outbox; COMMIT
    W->>DB: kernel.claim_outbox() / kernel.claim_jobs() (SKIP LOCKED)
    W->>DB: BEGIN; set_config(tenant, env); SELECT envelope
    W->>H: handle(envelope)
    H->>DB: INSERT inbox(consumer, event_id); effect
    W->>DB: UPDATE outbox SET status='dispatched'; COMMIT
```

### Consequences

- Good: no extra broker; transactional by construction; RLS context per job is explicit and tested.
- Good: job fairness across tenants is visible in one SQL function and its tests.
- Bad: we own dispatcher code (leases, backoff, metrics) and its tests.
- Bad: Postgres polling (1 s idle interval plus `LISTEN esmbler_outbox` wake-up) limits throughput to a few thousand events/s, far above pilot need.

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| Own outbox/jobs | Same transaction and RLS context; fairness under our control | Code to maintain |
| Hangfire + PostgreSQL | Mature dashboard, retries | No per-job RLS context control; FIFO queues, weak tenant fairness; its own schema outside our migrations and policies |
| MassTransit / Wolverine + outbox | Proven outbox and sagas | Broker or extra abstraction unnecessary at pilot scale; licensing churn (MassTransit v9 commercial) |
| Temporal / Elsa | Durable timers, long-running workflows | Extra cluster or runtime; tenant isolation is our problem again; not needed until approvals and SLA timers |

## More information

- Revisit triggers: approvals and SLA timers in Gate 2 need long-running compensation that the state machine cannot express (re-evaluate Temporal/Elsa); oldest pending event age p95 > 30 s; more than 50 tenants.
- Verification: `Integration.Outbox.Record_audit_and_outbox_commit_or_roll_back_together`, `Integration.Outbox.Duplicate_delivery_is_deduplicated_by_inbox`, `Integration.Outbox.Out_of_order_revision_is_ignored_by_projection`, `Integration.Jobs.Overloaded_tenant_does_not_starve_others` (planned, Gate 1–2).
- Related: [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (migration jobs), [ADR 0004](0004-authorization-engine.md) (tuple sync via outbox).
