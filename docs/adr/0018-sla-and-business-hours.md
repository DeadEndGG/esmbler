---
title: "ADR 0018: SLAs and business hours"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-BIZ-005, REQ-MIG-004, REQ-ACC-13, REQ-EVT-008]
---

# ADR 0018: SLAs and business hours

## Context and problem statement

Gate 2 needs service levels: response and resolution targets on tickets, a requester-facing fulfillment target on requests and team targets on fulfillment tasks (ADR 0015). The master spec ("SLA/business hours" row, "Migration and recovery") asks for start, pause, resume and stop rules, business versus elapsed time, reopening, time zones, daylight-saving transitions, holidays, versioned calendars and policies with a defined effect of edits on running timers and recorded breaches, and historical breach reporting. Acceptance scenario 13 includes "daylight-saving SLA transitions behave correctly".

How are SLA policies and calendars modeled, where do timers live, when do they change, and how are warnings and breaches detected?

## Decision drivers

- Policies and calendars are configuration: drafted, reviewed, published, upgraded, edited by the AI builder through typed overlay operations, and shipped by modules.
- A timer changes exactly when its record does, in revision order, with no lost or reordered updates.
- Deadlines are correct across nights, weekends, holidays, time zones and daylight-saving changes.
- Edits never rewrite running timers or recorded breaches.
- Breaches are detected on time even when nobody touches the record, and published as events (ADR 0017).
- Tenant isolation: every new table has FORCE RLS.

## Considered options

1. **Spec objects for calendars and policies; timers in a kernel table updated inside the record write transaction by a pure engine; one durable job per running timer for warnings and breaches.**
2. Timers driven by an outbox consumer reacting to record events.
3. SLA fields on the record (due date, breached flag) computed by rules.

## Decision outcome

**Chosen option 1.**

### Model

- `BusinessCalendarSpec`: an IANA time zone, working intervals per weekday in local wall-clock time (several per day allowed, `24:00` for midnight), and holidays as local dates.
- `SlaPolicySpec`: an entity, an optional `when` condition, `enabled`, and 1–5 targets. A target (`SlaTargetSpec`) has `start` (null: on create), `stop` (required), `pause`, `onReopen` (`resume`, `restart` or `none`), a calendar (null: around the clock), goals in order (`minutes`, an optional `when`, an optional calendar override, `24x7` for elapsed time) and `warnAtPercent`.
- Overlay operations: `business_calendar.add|update|remove` and `sla_policy.add|update|remove`. Tenants change default calendars and policies (hours, holidays, time zone, goals, conditions, enabled) but cannot remove them; a calendar in use cannot be removed. None of these changes permissions.

### Business time

`Kernel/Sla/BusinessTime` converts each local working interval to UTC day by day through the calendar's time zone. 09:00 stays 09:00 across a daylight-saving change, so a working day keeps its local length and a 24-hour local day has 23 or 25 hours. A start time inside a spring-forward gap moves to the first valid minute; an ambiguous fall-back time starts at its first occurrence and ends at its second. Holidays remove whole local days.

### Timers

- `kernel.sla_timers` holds one row per record, target and cycle: status (`running`, `paused`, `stopped`, `cancelled`), measured seconds before the current segment, segment start, due and warning instants, warned, breached and stopped times, outcome (`met` or `breached`), and a **snapshot** of the target, chosen goal and calendar. FORCE RLS; `esmbler_app` may select, insert and update, never delete (rows are the breach history).
- `SlaService.ApplyAsync` runs in every record write transaction (create, update, transition, system writes) while the record row is locked, after the record is written. The pure `SlaEngine` then:
  - moves existing timers on their **snapshot's** conditions: it pauses, resumes and stops them, and on reopen resumes, restarts (a new cycle) or does nothing;
  - starts timers for targets that have none, from the **current** policies: the first goal whose condition holds, the calendar snapshotted.
- Delete cancels open timers. Restore evaluates the record as if created: targets whose timers were all cancelled start again in a new cycle (cycles are unique per record and target).
- A workflow migration that moves a record's state (`migrate` or `cancel` policy) moves its clocks in the same transaction, as a transition would. Data migrations of module upgrades convert field values only and do not move clocks; on-create targets added by an upgrade apply to records created afterwards (ASM-056).
- Conditions of running timers are evaluated without the entity schema, so a field removed later reads as null instead of freezing the clock; new timers are validated against the schema. Policies are re-checked when a target restarts: a disabled or no longer applicable policy starts no new cycle.
- A deadline that passed before a write (for example a pause) is recorded as a breach at the deadline.
- Due and warning times are computed in business time from the remaining goal. A paused timer has no due time.
- One durable job per running timer (`sla.check`, dedup key `sla:{timer}`) fires at the next warning or due time. It locks the record, re-reads the timer and publishes `platform.sla.warning` or `platform.sla.breached` once. It locks the record before reading the timer, then completes itself and schedules the next check in the same transaction, so a write that moves the timer later always finds a pending job to move. Keyed timer jobs are exempt from the per-environment job quota (they are bounded by the timers they belong to), so a busy environment never silently stops detecting breaches.
- A breach is recorded once (`breached_at`) and stays recorded: stopping later gives outcome `breached`, and reopening and meeting the goal never clears it.

### Limits

Calendars allow at most 4 intervals a day of at least 15 minutes, at most 400 holidays with labels up to 80 characters; an entity has at most 10 SLA policies of 1–5 targets; a goal must fit within a year of its calendar's working time. Conditions cannot read fields that any permission set hides, because every reader of the record sees which goal applies and whether the clock runs. Timers record the configuration revision they started under (`config_revision`) for reports.

### Changes while timers run (REQ-MIG-004)

- Running timers keep their snapshot: goal, conditions and calendar. A publish that changes a goal, a condition, the time zone, the hours or the holidays affects only timers that start afterwards. Recorded breaches and outcomes never change.
- The draft review lists, per changed policy (including policies whose calendar changed), how many timers are running and keep their snapshot.
- Disabling or removing a policy starts no new timers; running ones finish on their snapshot.

### Read paths and events

- `GET /api/records/{entity}/{id}/sla` returns the record's latest timer per target to people who can read the record.
- The `activity.sla` element renders it: on track, at risk, paused, breached, met or missed; time left or overrun; goal and calendar.
- Platform event types `platform.sla.warning` and `platform.sla.breached` (source `platform.sla`) carry policy, target, goal, due time and timer ID.

### MCP

`list_sla_policies`, `add_business_calendar`, `update_business_calendar`, `add_sla_policy` and `set_sla_goals`.

### Tickets 1.5.0

- Calendar `core.tickets.calendar.business_hours`: America/New_York, Monday to Friday 08:00–18:00, US federal holidays 2026–2027 (ASM-054).
- Ticket policy: first response (stops when the ticket leaves `new`) and resolution (pauses on hold, stops when resolved, closed or cancelled, resumes on reopen). P1 is measured around the clock; P2–P4 in business hours.
- Request fulfillment: pauses while awaiting approval.
- Task completion: starts when a task can be worked.
- The `activity.sla` panel is on the ticket, request and task pages.

### Consequences

- Good: timers are exact and ordered with their record, and breaches are detected on time without polling.
- Good: history is immutable and auditable, and edits are safe.
- Good: SLAs are configuration the AI builder can tune.
- Bad: every write of an entity with a policy reads its timers (one indexed query).
- Bad: one job per running timer.
- Bad: conditions are evaluated only on writes and checks, so a condition over time alone (for example `now`) does not move a timer by itself.
- Neutral: breach reports for dashboards read `kernel.sla_timers` (G2-M9).
- Bad: a running timer keeps its goal when the priority changes (ASM-057).

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Spec objects, kernel timers in the write transaction, durable checks | Exact, ordered, pinned snapshots, on-time breaches, configuration-driven | A table, a job per timer, a query per write |
| 2. Outbox consumer | Decoupled from writes | Events can arrive out of order or late; pause and resume times drift; extra reconciliation |
| 3. Fields on the record | Simple to show and filter | Breaches need someone to write the record; history is lost; recomputation on calendar change rewrites the past |

## More information

- Related: [ADR 0005](0005-job-and-workflow-execution.md) (durable jobs and timers), [ADR 0015](0015-requests-catalog-fulfillment-and-workflow-versions.md) (requests and tasks), [ADR 0017](0017-event-audit-alert-notification-services.md) (platform events), [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (overlay operations).
- Verification:
  - Unit: `Sla.BusinessTimeTests`, `Sla.SlaEngineTests`, `Configuration.SlaPolicyTests`.
  - Integration: `Records.SlaTests`, `Mcp.McpServerTests.Agent_tunes_service_levels_and_adds_business_hours`.
  - Playwright: `sla.spec.ts`.
