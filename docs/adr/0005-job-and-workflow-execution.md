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

```sql
CREATE TABLE kernel.outbox (
  id uuid PRIMARY KEY,                       -- = EventEnvelope.EventId
  tenant_id uuid NOT NULL, environment_id uuid NOT NULL,
  type text NOT NULL,                        -- record.updated, config.published, ...
  record_id uuid, record_revision int,       -- ordering key
  envelope jsonb NOT NULL,                   -- Esmbler.Sdk.Events.EventEnvelope
  status text NOT NULL DEFAULT 'pending',    -- pending | leased | done | dead
  attempts int NOT NULL DEFAULT 0,
  available_at timestamptz NOT NULL DEFAULT now(),
  leased_by text, lease_until timestamptz, last_error text,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX outbox_ready ON kernel.outbox (available_at) WHERE status = 'pending';

CREATE TABLE kernel.inbox (
  tenant_id uuid NOT NULL, environment_id uuid NOT NULL,
  consumer text NOT NULL, event_id uuid NOT NULL,
  processed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, environment_id, consumer, event_id)
);

CREATE TABLE kernel.jobs (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, environment_id uuid NOT NULL,
  kind text NOT NULL,                        -- config.migration, workflow.timer, authz.sync, ...
  payload jsonb NOT NULL, checkpoint jsonb,  -- resumable progress
  status text NOT NULL DEFAULT 'pending',    -- pending | leased | done | dead | cancelled
  attempts int NOT NULL DEFAULT 0, max_attempts int NOT NULL DEFAULT 10,
  run_at timestamptz NOT NULL DEFAULT now(), lease_until timestamptz, leased_by text,
  principal text NOT NULL,                   -- whose authority the job runs with
  causation_depth int NOT NULL DEFAULT 0
);
```

All three tables have `FORCE ROW LEVEL SECURITY` ([ADR 0010](0010-database-migrations-and-roles.md)).

### Dispatch

The worker cannot see rows across tenants under RLS. It claims work through one narrow `SECURITY DEFINER` function, `kernel.claim_work(worker text, max int)`, owned by `esmbler_migrator` with a fixed `search_path`. It returns only `(id, tenant_id, environment_id, kind)` and sets the lease:

```sql
-- inside kernel.claim_work: round-robin across tenants, per-tenant cap
WITH ready AS (
  SELECT id, tenant_id,
         row_number() OVER (PARTITION BY tenant_id ORDER BY available_at) AS rn
  FROM kernel.outbox
  WHERE status = 'pending' AND available_at <= now()
)
SELECT o.id FROM kernel.outbox o JOIN ready USING (id)
WHERE ready.rn <= 4                                  -- per-tenant concurrency cap
  AND NOT EXISTS (SELECT 1 FROM kernel.outbox p       -- per-record ordering
                  WHERE p.record_id = o.record_id AND p.tenant_id = o.tenant_id
                    AND p.status IN ('pending','leased') AND p.record_revision < o.record_revision)
ORDER BY ready.rn, o.available_at
LIMIT $max
FOR UPDATE OF o SKIP LOCKED;
```

For each claimed item the worker opens a transaction, sets `app.tenant_id`/`app.environment_id` with `set_config(..., true)`, reads the envelope under RLS, and runs the handler.

- **Leases:** 60 s, renewed by long jobs; an expired lease returns the item to `pending`.
- **Backoff:** `min(5 s × 2^attempts, 15 min)` with ±20% jitter.
- **Dead letter:** after `max_attempts` (10) the status becomes `dead` with `last_error`; operators inspect and replay (Gate 2 UI; replay is audited and re-authorizes the principal).
- **Inbox:** a handler inserts `(consumer, event_id)` in the same transaction as its effect; a duplicate key means "already done" and the item is acknowledged.
- **Stale revisions:** projections store the last applied `record_revision` and ignore lower ones; business-transition consumers detect gaps and reload the record.
- **Cycles and fan-out:** `causation_depth` > 8 stops automation with an audited `automation.depth_exceeded`; fan-out per event is capped at 100 jobs (Gate 2 governor).
- **Tenant isolation of jobs:** a job always carries tenant, environment and principal; the handler re-checks the principal's permissions before acting (REQ-ACC-07).

```mermaid
sequenceDiagram
    participant API as API command
    participant DB as PostgreSQL
    participant W as Worker
    participant H as Handler
    API->>DB: BEGIN; SET LOCAL tenant; UPDATE record; INSERT audit; INSERT outbox; COMMIT
    W->>DB: kernel.claim_work() (SKIP LOCKED, fair)
    W->>DB: BEGIN; set_config(tenant, env); SELECT envelope
    W->>H: handle(envelope)
    H->>DB: INSERT inbox(consumer, event_id); effect
    W->>DB: UPDATE outbox SET status='done'; COMMIT
```

### Consequences

- Good: no extra broker; transactional by construction; RLS context per job is explicit and tested.
- Good: tenant fairness is visible in one SQL query and its tests.
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
