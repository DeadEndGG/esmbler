---
title: "ADR 0005: Job and workflow execution"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-EVT-001, REQ-EVT-002, REQ-EVT-003, REQ-EVT-004, REQ-EVT-005, REQ-EVT-006, REQ-EVT-009, REQ-TEN-002, REQ-TEN-006, REQ-MIG-002, REQ-MIG-003, REQ-ACC-06, REQ-ACC-11, REQ-BIZ-004]
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

- `kernel.claim_outbox` returns `(id, tenant_id, environment_id, attempts)` for pending events whose lease is free, interleaved across tenants: every tenant's oldest event is claimed before any tenant's second-oldest (`row_number() OVER (PARTITION BY tenant_id ...)`, migration `0005_durable_jobs`), so one tenant's backlog cannot delay another's events (REQ-ACC-11).
- `kernel.claim_jobs` returns `(id, tenant_id, environment_id, kind, attempts)` and is fair across tenants: at most one job per tenant per claim round, and none for a tenant that already has a running job with a live lease. It also reclaims `running` jobs whose lease expired (the worker crashed), which then resume from their checkpoint. Claimed jobs move to `running`.

```sql
-- inside kernel.claim_jobs (abridged)
WITH candidates AS (
  SELECT DISTINCT ON (j.tenant_id) j.id FROM kernel.jobs j
  WHERE ((j.status = 'pending' AND j.available_at <= now())
         OR (j.status = 'running' AND j.locked_until < now()))      -- crashed worker
    AND NOT EXISTS (SELECT 1 FROM kernel.jobs r
                    WHERE r.tenant_id = j.tenant_id AND r.status = 'running'
                      AND r.locked_until > now() AND r.id <> j.id)
  ORDER BY j.tenant_id, j.available_at
), next AS (
  SELECT j.id FROM kernel.jobs j JOIN candidates c ON c.id = j.id
  ORDER BY j.available_at LIMIT p_batch FOR UPDATE OF j SKIP LOCKED
)
UPDATE kernel.jobs j SET status = 'running', locked_until = now() + make_interval(secs => p_lease_seconds), ...
```

For each claimed item the worker opens a transaction through `Database.InTenantAsync` (which sets `app.tenant_id`/`app.environment_id` with `set_config(..., true)`), reads the item under RLS, and runs the handler (for an outbox event, one such transaction per consumer since G2-M17; see below). `apps/Esmbler.Worker` hosts `OutboxDispatcher` and `JobRunner` (`packages/Esmbler.Kernel.Infrastructure/Events/`). Gate 1 consumers are `RecordStateProjection` and `NotificationConsumer` (in-app notifications). Gate 2 adds `FulfillmentConsumer` (consumer name `fulfillment`), which creates, releases and cancels request fulfillment tasks and completes requests; it reconciles against the request's current state under a row lock, so duplicate or reordered events converge ([ADR 0015](0015-requests-catalog-fulfillment-and-workflow-versions.md)). G2-M3 adds `ApprovalConsumer` (consumer name `approvals`), which starts the approval of a request in its initial state whose catalog item names a policy (once per request) and cancels the pending approval when the request enters a cancelled-category state; like fulfillment it reads current state under a row lock, not the event payload ([ADR 0016](0016-approvals.md)).

**Job handlers (Gate 2).** Each job kind has an `IJobHandler` (`Events/Jobs.cs`) registered in DI: `Kind`, the `RequiredAction` its creator must still hold, `RunAsync(JobContext)`, and an optional `OnAbandonedAsync`. `JobContext` gives the payload, the previous attempt's checkpoint, `SaveCheckpointAsync` and `RenewLeaseAsync`. A handler throws `JobTerminalException` for failures retrying cannot fix. The first kind is `upgrade.run` (`UpgradeJobHandler`, [ADR 0003](0003-configuration-artifact-and-overlay-format.md)). The second is `workflow.migrate` (`WorkflowMigrationJobHandler`, [ADR 0015](0015-requests-catalog-fulfillment-and-workflow-versions.md)): scheduled by `ConfigActivator` when a published workflow gets a new definition version and its active-instance policy is `migrate` or `cancel` (dedup key `workflow.migrate:{workflow}`, replacing a live job); it moves running instances in checkpointed batches of 200, audits each move and emits a `record.transitioned` event. It has no `RequiredAction`, because the publish approval covers it. The third is `approval.expire` (`ApprovalExpiryJobHandler`, [ADR 0016](0016-approvals.md)): a timer scheduled when an approval stage with `expiresAfterHours` starts (dedup key `approval.expire:{approval}:{stage}`, and `…:escalated` for the second deadline after an escalation), cancelled when the stage or approval finishes. On expiry it escalates once to the stage's `escalateTo` approvers, or declines the request. It has no `RequiredAction`: it is a platform timer set by a published policy and acts for nobody. The fourth is `sla.check` (`SlaCheckJobHandler`, [ADR 0018](0018-sla-and-business-hours.md)): one live job per running SLA timer (dedup key `sla:{timer}`) at its next warning or due time; it publishes `platform.sla.warning` or `platform.sla.breached` once, then completes itself and schedules the next check in the same transaction, because the scheduler cannot move a running job. It has no `RequiredAction` either.

**Scheduling and timers.** `JobScheduler.ScheduleAsync(db, tenant, environment, JobRequest)` runs inside the caller's tenant transaction, so a job exists exactly when the change that needs it commits. Timers are jobs with a future `RunAt` and a `DedupKey` (for example `sla:{timer}` or `approval.expire:{approval}:{stage}`): a unique index allows one live job per key; scheduling again keeps the live job or, with `ReplaceExisting`, moves it; `JobScheduler.CancelAsync(db, key)` cancels it.

- **Leases:** outbox claims use 60 s leases in batches of 50; job claims use 5-minute leases in batches of 10, renewed by long jobs. An outbox event whose lease expired is claimable again (it stays `pending` until dispatched); an expired running job is reclaimed by `claim_jobs`.
- **Backoff:** `min(5 s × 2^(attempts−1), 15 min)`; jobs add ±20% jitter.
- **Per-consumer delivery (G2-M17, [ADR 0017](0017-event-audit-alert-notification-services.md)):** each consumer of an event runs in its own tenant-context transaction. A failing consumer is recorded in `kernel.consumer_failures` (attempts, last error) and retried with the event's backoff, while the consumers that succeeded commit and are never re-run (inbox). After 10 attempts that consumer is dead for that event and the event counts as dispatched once every other consumer is done.
- **Dead letter:** a consumer becomes dead for an event after 10 attempts (queue `consumers`), an outbox event becomes `dead` only when the event itself cannot be read, jobs after `max_attempts` (default 5), with `last_error`; jobs denied on re-authorization or failing terminally become `failed`. `OperationsService` (`/api/admin/operations/queues`, `/dead-letters`, `POST /{queue}/{id}/replay`, page **Operations**) shows queue depth and the oldest waiting age, lists dead letters (jobs, events and consumers), and replays them (a consumer replay re-runs only that consumer: the other consumers are deduplicated by the inbox); replay needs `permissions.admin`, is audited (`operations.replay`), and the job is re-authorized as its creator when it runs again.
- **Inbox:** a handler inserts `(consumer, event_id)` in the same transaction as its effect; a duplicate key means "already done" and the item is acknowledged.
- **Retention:** dispatched events older than 14 days (never under 7) are removed hourly, at most 10,000 per run and one worker at a time, with their inbox marks and consumer failures by `kernel.purge_outbox(days)` (`SECURITY DEFINER`, migration `0009_event_platform.sql`); pending and dead events, and events with a dead consumer, are kept until replayed.
- **Leases per event:** the dispatcher renews an event's lease before working on it and skips it if another worker took it over; status updates apply only while it still holds the lease. The audit log, not the outbox, is the long-term record.
- **Per-record ordering and stale revisions:** `claim_outbox` does not order per record, so consumers must tolerate reordering: projections (`kernel.record_projections`) store the last applied `record_revision` and ignore lower ones; business-transition consumers detect gaps and reload the record.
- **Governors:** a job request deeper than 8 automated steps (`JobRequest.Depth`, from the envelope's `Depth`) is refused with an audited `automation.depth_exceeded`; one causing event may start at most 100 jobs (`automation.fanout_exceeded`); an environment may hold at most 50,000 waiting jobs (`quota.jobs_exceeded`); jobs with a dedup key (timers such as `sla.check` and `approval.expire`, one per object) are exempt, so deadlines never silently stop. Refusals return `null` and are audited in the caller's transaction instead of failing the caller's change.
- **Tenant isolation of jobs:** a job always carries tenant, environment and principal (`created_by`); before each attempt `JobRunner` re-authorizes that principal with fresh facts for the right the job kind needs, and a denied job fails closed without retrying (REQ-ACC-07).

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
- Bad: Postgres polling (outbox every 500 ms when idle, jobs every 1 s) adds latency and limits throughput to a few thousand events/s, far above pilot need; a `LISTEN` wake-up can be added if needed.

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| Own outbox/jobs | Same transaction and RLS context; fairness under our control | Code to maintain |
| Hangfire + PostgreSQL | Mature dashboard, retries | No per-job RLS context control; FIFO queues, weak tenant fairness; its own schema outside our migrations and policies |
| MassTransit / Wolverine + outbox | Proven outbox and sagas | Broker or extra abstraction unnecessary at pilot scale; licensing churn (MassTransit v9 commercial) |
| Temporal / Elsa | Durable timers, long-running workflows | Extra cluster or runtime; tenant isolation is our problem again; not needed until approvals and SLA timers |

## More information

- Revisit triggers: approvals and SLA timers in Gate 2 need long-running compensation that the state machine cannot express (re-evaluate Temporal/Elsa); oldest pending event age p95 > 30 s; more than 50 tenants.
- Verification: `Events.EventDeliveryTests.Record_change_audit_and_event_commit_together`, `Events.EventDeliveryTests.Resolving_notifies_the_requester_once_even_when_redelivered`, `Events.EventDeliveryTests.Projection_ignores_stale_and_duplicate_revisions`, `Identity.RevocationTests.Queued_job_is_reauthorized_and_fails_closed_when_its_owner_lost_the_right`, `Configuration.UpgradeTests.Upgrade_preserves_customizations_and_recovers_from_a_partial_migration_failure`. `Events.DurableJobsTests` (tenant fairness for jobs and events, dead letters and replay, timers, governors, checkpoints; REQ-ACC-11). The full load test with p95 queue-wait budgets is part of G2-M16.
- Related: [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (migration jobs), [ADR 0004](0004-authorization-engine.md) (job re-authorization).
