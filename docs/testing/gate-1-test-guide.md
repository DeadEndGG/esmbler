---
title: Gate 1 test guide
status: accepted
owner: platform-lead
last_reviewed: 2026-10-10
requirements: [REQ-ACC-01, REQ-ACC-02, REQ-ACC-03, REQ-ACC-04, REQ-ACC-06, REQ-ACC-07, REQ-ACC-09, REQ-ACC-10, REQ-TEN-002, REQ-LIVE-002, REQ-AI-005, REQ-AI-007, REQ-AUTHZ-006]
---

# Gate 1 test guide

Gate 1 is the architecture proof: a tenant gets the default Tickets module, customizes it by chatting with the AI builder (fields, rules, a page card), watches the page change while it chats, publishes, works tickets under the new rules, takes the Tickets 1.1.0 upgrade without losing its changes, and a second tenant is unaffected throughout. Check out the tag `gate-1-complete` (see [human actions](../human-actions.md) if the tag is missing from the remote) to test this phase.

## Setup

1. Install Docker, the .NET 10 SDK (see `global.json`), and Node.js 24.
2. `git checkout gate-1-complete`
3. Start the system: `docker compose -f deploy/docker-compose.yml up --build -d --wait`. Behind a TLS-intercepting proxy, set `EXTRA_CA_FILE=/path/to/ca-bundle.crt` first.
4. Open <http://localhost:8080>.

The first start applies migrations `0001_kernel` to `0004_refresh_session_cap` and provisions two demo workspaces. Every demo user's password is `Esmbler!2026`.

| Person | Email | Workspace | Can |
|---|---|---|---|
| Alex Rivera | `alex@acme.example` | Acme (also a member of Globex) | Tenant administrator: customize, publish, manage modules and people; works tickets |
| Sam Okafor | `sam@acme.example` | Acme | Agent in the Service Desk and Network groups |
| Dana Kim | `dana@acme.example` | Acme | Requester: sees only their own tickets |
| Morgan Diaz | `morgan@globex.example` | Globex | Tenant administrator of the second tenant |

Without an Anthropic API key the chat panel shows a **Built-in** badge: a scripted planner understands common requests (examples below) and calls the same MCP tools Claude would. To use Claude, set `ANTHROPIC_API_KEY` before `docker compose up` (model `claude-opus-5-5`, configurable with `Esmbler__Assistant__Model`).

## Scenarios

### 1. Sign in and work tickets like in Linear

1. Sign in as Alex, choose **Acme → Production**.
2. On the Tickets list press `j`/`k` to move, `Enter` to open, `c` to create, `/` to search, `⌘K` (or `Ctrl+K`) for the command palette, `?` for all shortcuts.
3. Switch saved views (All open, Assigned to me, Unassigned…), filter by status or priority, sort by a column.
4. Change a ticket's priority straight from the list (inline select).
5. Open a ticket. Click a value (or focus it and press `E`) to edit it inline; `Enter` saves, `Esc` cancels. Use **Resolve** and the other workflow buttons; add a public reply and a work note; look at **History**.

Expected: changes save instantly (optimistic) and show an Undo toast on the list; a value the rules reject shows the reason under the field; history lists who changed what, including the configuration revision.

### 2. Customize Tickets by chatting with the AI builder (live)

1. On a ticket page press `⌘I` (or click **✦ AI builder**). The page switches to a labeled **Draft preview**.
2. Send, one at a time:
   - `Add a required "Customer impact" field to tickets and put it in the Details card`
   - `When category is access, require a justification`
   - `Show a warning callout when a ticket is unassigned`
   - `Add a Category column to the list`
3. Watch the page while the assistant works: each change appears within about a second, highlighted; new blocks carry a **New** chip.
4. Hover a step in **Changes in this draft** to highlight its elements; click **Undo** on one step, then **Redo**.
5. Click a card on the page (it becomes a chip in the composer), then ask `Add a "Vendor" field here`.
6. Direct manipulation mixes with AI edits: drag a block by its handle (or focus it and press `Alt+↑/↓`); press `/` to insert a field or callout; select a block and press `Backspace` to remove it.
7. In the draft, try values: set Category to Access and see Justification become required (values typed here are not saved).

Expected: other people keep seeing the published page (sign in as Sam in a private window to check); every step is undoable on its own; errors are explained in plain language with a suggested fix.

### 3. Publish

1. Click **Review and publish**. The dialog shows readiness and exactly what changes.
2. Click **Publish**.

Expected: a toast "Published revision N"; everyone now sees the new page. AI agents cannot publish: asking the assistant to "publish" creates a publish request that a person approves in the same dialog. A draft that changes permissions needs the permission-administration right.

### 4. Operate under the new rules

1. Create a ticket (`c`) with Category **Access** and no Justification.
2. Add a Justification and create it again.

Expected: the first attempt is refused with "Justification is required"; the second succeeds. The rule applies to the API and the AI too, not only the page.

### 5. The second tenant is unaffected

1. Sign out; sign in as Morgan (Globex).
2. Open a ticket and the list.

Expected: no Customer impact, Justification, callout, or Category column. The Globex API rejects a `tenant__customer_impact` value as an unknown field.

### 6. Upgrade Tickets to 1.1.0 without losing changes

1. As Alex, open **Configuration**.
2. Click **Preview upgrade** on `core.tickets`: the report says your customizations carry over, lists what 1.1.0 changes (Impact, Channel, Effort hours; Estimated effort deprecated) and the data migrations.
3. Click **Upgrade**.

Expected: status moves to `activated`; your fields, rule, callout and column are still there; the new Impact field appears; existing tickets keep their data. Old revisions show **Roll back** only when that is safe for the data now present.

### 7. Reset a customization to default

1. As Alex open **Configuration → Customizations**. Each customized element is listed as *added* or *changed*.
2. Click **Reset to default** on a changed element (for example the relabeled Category), or ask the assistant `Reset the category field to default`.
3. Open the AI builder: the reset is a step in the draft; review and publish.

Expected: the element returns to the default module's definition after publishing. Removing an added field that a rule still uses shows that rule as an error in the draft until you handle it.

### 8. Delete and restore a ticket

1. On a ticket, click **Delete** and confirm.
2. Click **Undo** in the toast.

Expected: the ticket disappears from the list and comes back with its history; restore is possible for 30 days by anyone allowed to delete tickets.

### 9. Migration failure and recovery

1. Stop the worker and start it with a fault: `docker compose -f deploy/docker-compose.yml run -d --name worker-fault -e Esmbler__FaultInjection__Migration=convert_effort:1 worker` (after `docker compose stop worker`).
2. As Morgan (Globex, still on 1.0.0) start the upgrade from **Configuration**.
3. The upgrade shows `failed` with the migration and batch that failed.
4. Remove the faulty worker (`docker rm -f worker-fault`), start the normal one (`docker compose start worker`), and click **Resume**.

Expected: the migration resumes from its checkpoint without converting any ticket twice; values that can't be converted (for example "about 3h") stay in Estimated effort and are listed; the new version activates only after all migrations finish.

### 10. Revocation

1. As Alex open **People and roles** and remove Sam from both groups.
2. In Sam's window, reload the Tickets list.

Expected: Sam no longer sees tickets on the next request. Revoking a builder's role stops their publish immediately and cuts off their AI agent at its next tool call; a queued job whose creator lost the right fails closed.

### 11. Use the MCP server from your own agent

1. Get an agent token: sign in through the API and call `POST /api/auth/agent-token` (see `tests/Esmbler.IntegrationTests/Infrastructure/McpFactory.cs` for the exact calls).
2. Point an MCP client (for example MCP Inspector) at `http://localhost:5090/mcp` with `Authorization: Bearer <token>`.

Expected: discovery, build, review and lifecycle tools are listed; there is no publish tool; API tokens are refused; errors come back as `{code, message, target, suggestion}`. The default delegation has no record access; request `records:read` explicitly to let an agent read tickets.

## Automated evidence

Run from the repository root.

| Suite | Command | Result at `gate-1-complete` |
|---|---|---|
| Unit | `dotnet test tests/Esmbler.UnitTests` | 121 passed |
| Architecture | `dotnet test tests/Esmbler.ArchitectureTests` | 7 passed (includes "assistant changes workspaces only through MCP" and "only the assistant calls the model provider") |
| Integration (Testcontainers, needs Docker) | `dotnet test tests/Esmbler.IntegrationTests` | 112 passed (includes the 43 evaluation cases and the 100k-ticket benchmark) |
| Frontend | `cd apps/web && npx ng test --watch=false` | 84 passed (expression evaluator conformance) |
| Browser acceptance (stack running) | `cd tests/e2e && npm ci && npx playwright test` | 9 passed on a clean stack |

| Requirement | Automated test |
|---|---|
| REQ-ACC-01 tenant customization, B unchanged | `Acceptance.Gate1PathTests.Customize_with_ai_publish_operate_upgrade_and_isolate`; `ConfigLifecycleTests.Tenant_A_customization_leaves_tenant_B_unchanged`; Playwright `@REQ-ACC-01` |
| REQ-ACC-02 competing edits | `ConfigLifecycleTests.Competing_edits_and_stale_publishes_are_rejected_with_details`; `RecordServiceTests.Create_update_conflict_and_history`; Playwright `@REQ-ACC-02` |
| REQ-ACC-03 upgrade keeps overlays, blocks conflicts | `Configuration.UpgradeTests.*`; unit `UpgradeTests`; Playwright `@REQ-ACC-03` |
| REQ-ACC-04 migration failure and resume | `UpgradeTests.Upgrade_preserves_customizations_and_recovers_from_a_partial_migration_failure` |
| REQ-ACC-06 duplicate and reordered events | `EventDeliveryTests.*` |
| REQ-ACC-07 revocation | `Identity.RevocationTests.*` |
| REQ-ACC-09 preview has no side effects | `Acceptance.PreviewSafetyTests.Simulation_and_preview_have_no_side_effects`; Playwright `@REQ-ACC-09` |
| REQ-ACC-10 embedded instructions | `McpServerTests.Embedded_instructions_in_records_cannot_widen_authority`; AI eval case `injection-in-record-text-is-ignored` |
| AI builder evaluation | `Assistant.AssistantTests.Scripted_planner_passes_the_evaluation_case` (43 cases in `tests/ai-evals/builder-cases.json`, including malicious, policy and unsupported requests; every case also checks for unintended permission changes or publishes); Claude runs the same cases when `ANTHROPIC_API_KEY` is set |
| Tenant isolation in the database | `Isolation.DatabaseIsolationTests.*` |
| Live editing: point-and-prompt, per-step undo | Playwright `@REQ-LIVE-003 @REQ-LAY-003` |
| Keyboard-first UX | Playwright `@REQ-DS-002` |
| Accessibility (WCAG 2.2 AA, axe) | Playwright `@REQ-DS-003` (login, list, record, draft mode, configuration) |
| Security review regressions | `Api.SecurityHardeningTests.*`, `Mcp.McpServerTests.Tool_calls_are_rate_limited_per_agent_session`, `Disabling_the_agent_stops_its_next_call` |
| Activation health check and reset to default | `Configuration.ActivationHealthTests.*`, `Configuration.ResetToDefaultTests.*` |

### Experience targets (proposed, measured)

Measured by `tests/e2e/specs/experience.spec.ts` on the Docker Compose stack with the built-in planner (20 samples of adding a field to a card, method in [ASM-036](../decisions/assumptions.md)); results are written to `tests/e2e/test-results/experience.json` on every run.

| Target | Proposed | Measured (2026-10-10) |
|---|---|---|
| Simple change, prompt to previewed draft (p50) | under 10 s | 650 ms (p95 916 ms; the first sample warms the connection at 4.8 s) |
| AI change visible on the page after its tool call (p95) | under 1 s | 388 ms (p50 269 ms) |
| Every AI change undoable in one step | yes | yes (per-step Undo; `ConfigLifecycleTests.Invalid_operations_are_refused_with_actionable_errors_and_undo_respects_dependencies`) |
| Evaluation tasks completed without unintended changes | at least 80% | 43 of 43 with the built-in planner; Claude not yet measured (needs an API key, KI-007) |

### Query latency at 100,000 tickets

`Records.QueryBenchmarkTests` seeds 100,000 tickets in one tenant environment (Testcontainers Postgres 18) and runs each list shape 20 times through the record service, permission predicate included.

| Query | p50 | p95 |
|---|---|---|
| Default list, sorted by updated, page of 50 | 62 ms | 74 ms |
| Saved view "open", sorted by priority | 165 ms | 231 ms |
| Filter bar: status and category | 66 ms | 98 ms |
| Full-text search | 85 ms | 104 ms |
| Requester: only related tickets | 76 ms | 222 ms |
| Deep page (offset 5,000) | 40 ms | 54 ms |

## Known limits at this gate

See [known issues](../known-issues.md). In short: the built-in planner understands a fixed set of phrasings; Claude-driven runs need an API key; the per-tenant AI spend budget, expand/contract labels in upgrade reports, element-to-step highlighting and stylelint design lint are scheduled for Gate 2.
