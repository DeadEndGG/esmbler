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

**Chosen option 1.** Hub `/hubs/live` in `apps/Esmbler.Api`. Both the API and MCP write draft commands to Postgres and call `pg_notify('esmbler_draft', …)` in the same transaction, so the notification is delivered only on commit. Each API instance holds one dedicated Npgsql connection with `LISTEN esmbler_draft`. Status: planned for G1-M10; the `kernel.drafts` and `kernel.draft_commands` tables exist, the hub and listener do not yet.

```mermaid
sequenceDiagram
    participant M as Model / scripted planner
    participant MCP as Esmbler.Mcp tool
    participant DB as PostgreSQL
    participant L as API listener (LISTEN)
    participant H as SignalR hub /hubs/live
    participant R as Angular renderer
    M->>MCP: add_field(..., expectedDraftVersion 12)
    MCP->>DB: BEGIN; check version 12; INSERT kernel.draft_commands seq 13; drafts.version=13
    MCP->>DB: pg_notify('esmbler_draft', {t,e,draftId,version:13,seq:13}); COMMIT
    MCP-->>M: ok (seq 13, version 13)
    DB-->>L: notification (after commit)
    L->>DB: InTenantAsync(t, e); load command 13; recompose changed view
    L->>H: group draft:{t}:{e}:{draftId}
    H-->>R: draftChanged (version 13)
    R->>R: version == last+1? patch nodes by ID, highlight changedTargets
```

### Notification and event payloads

The `pg_notify` payload carries IDs only (well under the 8,000-byte limit, no record or config content):

```json
{ "t": "6f1c…", "e": "a7d2…", "draftId": "0192f4…", "version": 13, "seq": 13 }
```

The listener reads the command (its `op`, `summary`, `targets`, `actor`, `agent`) under that tenant's RLS context (`Database.InTenantAsync`) and sends:

```json
{
  "type": "draftChanged",
  "draftId": "0192f4…", "version": 13, "seq": 13, "kind": "apply",
  "actor": "user:alex", "agent": "agent:assistant-alex",
  "ops": [{ "op": "node.insert", "summary": "Insert field \"tenant__vendor\" into core.tickets.record.card_classification (after core.tickets.record.f_category)" }],
  "changedTargets": ["tenant__n_7f3a91c2de"],
  "removedTargets": [],
  "views": { "core.tickets.record": { "id": "core.tickets.record", "layout": { "id": "core.tickets.record", "type": "layout.page", "children": ["…"] } } },
  "diagnostics": []
}
```

`kind` is `apply`, `undo`, `redo` or `discard`. Only changed views are sent; field specs referenced by new nodes are included, filtered by the viewer's field permissions.

### Groups and authorization

- Group name `draft:{tenantId}:{environmentId}:{draftId}`.
- `JoinDraft(draftId)` checks: the connection's token `tid`/`eid` match the draft's tenant/environment, and the user has `config.edit` there. Otherwise `HubException("forbidden")`.
- Users without the draft open are never in the group and keep seeing the published page.
- Revocation: a membership or permission change closes affected connections (hub tracks user → connections).

### Ordering and version checks

- Client keeps `lastVersion`. `version == lastVersion + 1` → apply. `version <= lastVersion` → ignore (duplicate). `version > lastVersion + 1` → gap: call `GET /api/drafts/{id}` for a snapshot, then continue.
- On reconnect the client always takes a snapshot.
- The user's own edits send `expectedDraftVersion`; a stale one gets `409 draft_conflict` ([ADR 0003](0003-configuration-artifact-and-overlay-format.md)), the client reloads and re-applies if the edit does not overlap.
- Renderer patches the tree keyed by node ID: new nodes fade in with a highlight, removed nodes fade out, unchanged nodes keep component state (Angular `@for` tracked by ID, signals per node).

### Latency budget (p95)

| Step | Budget |
|---|---|
| Commit + notify delivery | 50 ms |
| Listener: load command, recompose view | 250 ms |
| SignalR send (WebSockets) | 50 ms |
| Renderer patch + paint | 150 ms |
| Margin (GC, contention) | 500 ms |
| **Total** | **1,000 ms** |

Measured in Playwright: the tool result timestamp (from the MCP call's `kernel.audit_log` row) to a `data-node-id` element becoming visible with the highlight class.

### Consequences

- Good: no new infrastructure; notifications are transactional; any API instance can serve any client because every instance listens.
- Bad: one long-lived `LISTEN` connection per API instance (outside the request pool); `NOTIFY` is not durable, so correctness relies on version gap detection and snapshots.
- Bad: full view per event (typically 10–30 KB); JSON Patch may be needed for very large views.

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| SignalR + LISTEN/NOTIFY | Transactional, no extra service | Not durable; one listener connection per instance |
| SignalR + Redis backplane | Standard scale-out | Extra stateful service; non-transactional with the command |
| SSE | Simple, HTTP only | One direction; no group management; reconnect handling by hand |
| Polling | Trivial | Latency or load: 1 s budget needs sub-second polling for every open draft |

## More information

- Revisit triggers: more than 10 API instances or notify throughput above 1,000/s (consider Redis backplane); view payloads above 64 KB (switch to JSON Patch); Gate 4 multi-builder presence.
- Verification (planned): `Acceptance.LiveEditing.Ai_change_visible_within_1s_p95`, `Integration.Live.Join_other_tenant_draft_is_forbidden`, `Unit.Renderer.Version_gap_triggers_snapshot`.
