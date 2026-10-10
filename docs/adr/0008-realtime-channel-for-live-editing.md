---
title: "ADR 0008: Real-time channel for live editing"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-LIVE-001, REQ-LIVE-002, REQ-LIVE-003, REQ-LIVE-004, REQ-LAY-004, REQ-TEN-002]
---

# ADR 0008: Real-time channel for live editing

## Context and problem statement

While a user chats with the assistant beside the real Tickets page, each change the AI makes must render on that page within one second of the tool call (p95), step by step, highlighted, without a reload (execution brief section 3). Drag-and-drop edits mix with AI edits on the same draft, and version checks stop them overwriting each other. Build tools run in `apps/Esmbler.Mcp`; the browser is connected to `apps/Esmbler.Api`. How does a change get from an MCP tool call to the renderer?

## Decision drivers

- p95 < 1 s from tool completion to rendered change.
- Two writer processes (MCP, API) and many API instances later.
- No tenant data crosses a draft group boundary.
- Gaps or reconnects never leave the page silently stale.

## Considered options

1. **SignalR hub in the API + Postgres `LISTEN/NOTIFY`** as the cross-process signal.
2. SignalR with a Redis backplane, MCP publishing to Redis.
3. Server-Sent Events from the API.
4. Client polling of the draft version.

## Decision outcome

**Chosen option 1.** Hub `/hubs/live` in `apps/Esmbler.Api` (`apps/Esmbler.Api/Endpoints/LiveHub.cs`). Every draft write, whether it comes from the API or the MCP server, goes through `DraftService` (`packages/Esmbler.Kernel.Infrastructure/Configuration/DraftService.cs`), which calls `pg_notify('esmbler_draft', …)` inside the same transaction (`NotifyAsync`), so the notification is delivered only on commit. `PublishService` sends `publish_requested` and `published` notifications the same way. Each API instance runs one `DraftNotificationListener` (a hosted service with a dedicated Npgsql connection, `LISTEN esmbler_draft`, reconnecting after one second on failure) that forwards each notification to the environment's draft group. Status: implemented (G1-M10).

```mermaid
sequenceDiagram
    participant M as Planner (Claude or scripted)
    participant MCP as Esmbler.Mcp tool
    participant DB as PostgreSQL
    participant L as API DraftNotificationListener
    participant H as SignalR hub /hubs/live
    participant R as Angular renderer
    M->>MCP: add_field(..., expected_version 12)
    MCP->>DB: BEGIN; check version 12; INSERT kernel.draft_commands seq 13; drafts.version = 13
    MCP->>DB: pg_notify('esmbler_draft', {t, e, d, v: 13, s: 13, k: apply, targets, agent, at}); COMMIT
    MCP-->>M: ok (step 13, version 13)
    DB-->>L: notification (after commit)
    L->>H: group draft:{t}:{e}
    H-->>R: draftChanged (IDs only)
    R->>R: highlight targets; reload /api/drafts/current and the draft view under the viewer's own permissions
```

### Notification payload

The `pg_notify` payload carries IDs, counters and a timestamp only (well under the 8,000-byte limit, no record or configuration content). The listener forwards it unchanged as the `draftChanged` message:

```json
{ "t": "6f1c…", "e": "a7d2…", "d": "0192f4…", "v": 13, "s": 13, "k": "apply",
  "targets": ["core.tickets.ticket/tenant__vendor", "tenant__n_7f3a91c2de"], "agent": true, "at": 1791612345678 }
```

| Field | Meaning |
|---|---|
| `t`, `e` | Tenant and environment (used only to pick the group) |
| `d`, `v`, `s` | Draft ID, draft version after the change, step sequence (0 for draft-wide events) |
| `k` | `apply`, `undo`, `redo`, `discard`, `publish_requested` or `published` |
| `targets` | Element IDs the step touched, for highlighting |
| `agent` | `true` when an AI agent made the change |
| `at` | Server time of the change (epoch milliseconds), used to measure live latency |
| `r` | Published revision (`published` events only) |

The original design had the listener load the command and send recomposed views in the event. The implementation sends IDs only and lets each client reload the draft state and views through the normal API, so field permissions and hidden-field filtering are applied by the same code as every other read (REQ-AUTHZ-004), and no view content passes through the listener.

### Groups and authorization

- Draft group `draft:{tenantId:N}:{environmentId:N}`. There is at most one open draft per tenant environment (unique index `drafts_one_open`), so the group is per environment.
- `WatchDraft()` takes tenant and environment from the connection's token only and calls `DraftService.CurrentAsync`, which fails unless the caller may edit configuration (`config.edit`); only then is the connection added to the group. `UnwatchDraft()` leaves it.
- Every connection also joins `user:{principalId:N}`, used for the assistant's progress events (`assistantEvent`, [ADR 0013](0013-ai-assistant-model-adapter.md)).
- People who are not building never join the draft group and keep seeing the published page (verified by `Acceptance.PreviewSafetyTests` and Playwright `@REQ-ACC-09`).
- Revocation: membership is checked when a connection joins; a later permission change does not close open connections. Because events carry only IDs and every reload is authorized, a revoked builder learns at most that a step happened until they reconnect.

### Ordering and consistency

- Every `draftChanged` event makes the client highlight `targets` and reload `GET /api/drafts/current` (and the pages' draft views), so a missed or duplicate event cannot leave the page stale for longer than the next event; per-event version-gap tracking was not needed.
- On reconnect the client re-joins with `WatchDraft()` and reloads a full snapshot.
- The user's own edits send `expectedVersion`; a stale one gets `409 draft_conflict` ([ADR 0003](0003-configuration-artifact-and-overlay-format.md)) and the client reloads.
- The renderer keys nodes by ID (Angular `@for` tracked by ID), so unchanged nodes keep their component state and touched nodes are highlighted.

### Latency budget (p95)

| Step | Budget |
|---|---|
| Commit + notify delivery | 50 ms |
| Listener forward + SignalR send (WebSockets) | 50 ms |
| Client reload of draft state and view | 250 ms |
| Renderer patch + paint | 150 ms |
| Margin (GC, contention) | 500 ms |
| **Total** | **1,000 ms** |

Measured by `tests/e2e/specs/experience.spec.ts` on the Docker Compose stack: the event's `at` timestamp to the new field's label rendering on the page, 20 samples per run. Latest run: AI step visible p95 about 390 ms; prompt to previewed draft p50 about 650 ms (target under 10 s). `LiveChannelTests.Applied_command_reaches_the_watching_builder_within_a_second` checks the server path in CI.

### Consequences

- Good: no new infrastructure; notifications are transactional; any API instance can serve any client because every instance listens.
- Good: events carry no content, so authorization stays in the normal read path.
- Bad: one long-lived `LISTEN` connection per API instance (outside the request pool); `NOTIFY` is not durable, so correctness relies on reloading a snapshot on every event and on reconnect.
- Bad: each event costs every watching builder one reload request; fine for a handful of builders per environment, revisit for Gate 4 multi-builder presence.

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| SignalR + LISTEN/NOTIFY | Transactional, no extra service | Not durable; one listener connection per instance |
| SignalR + Redis backplane | Standard scale-out | Extra stateful service; non-transactional with the command |
| SSE | Simple, HTTP only | One direction; no group management; reconnect handling by hand |
| Polling | Trivial | Latency or load: 1 s budget needs sub-second polling for every open draft |

## More information

- Revisit triggers: more than 10 API instances or notify throughput above 1,000/s (consider Redis backplane); reload load from many concurrent builders; Gate 4 multi-builder presence; closing hub connections on revocation.
- Verification: `Api.LiveChannelTests.Applied_command_reaches_the_watching_builder_within_a_second`, `Api.LiveChannelTests.Requesters_cannot_watch_drafts`, Playwright `experience.spec.ts` (`@REQ-LIVE-002`) and `gate1.spec.ts` (`@REQ-LIVE-003`, `@REQ-ACC-09`).
