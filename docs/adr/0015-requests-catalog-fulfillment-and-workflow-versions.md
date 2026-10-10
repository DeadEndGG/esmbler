---
title: "ADR 0015: Requests, catalog items, fulfillment tasks and workflow versions"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-BIZ-004, REQ-MIG-003, REQ-ACC-05, REQ-EVT-004, REQ-AI-003]
---

# ADR 0015: Requests, catalog items, fulfillment tasks and workflow versions

## Context and problem statement

Gate 2 turns Tickets into Tickets and Service Requests. The master spec asks for typed request variables, fulfillment tasks with dependencies, partial completion, rejection and cancellation ("Requests and catalog"), and for versioned workflow definitions where existing instances stay pinned, migrate through an explicit state mapping, or are cancelled with approval ("Migration and recovery"). Duplicate or out-of-order events must not create duplicate fulfillment work (scenario 6). Tenants and the AI builder must be able to add catalog items and change workflows through typed overlay operations, like any other configuration.

How are catalog items, variables, tasks and workflow versions modeled, and which component does what?

## Decision drivers

- First-party modules stay declarative JSON on the public SDK; the platform supplies generic behavior.
- Configuration changes remain typed overlay operations with conflict detection, reviewable in a draft.
- Tenant isolation: every new table has FORCE RLS; nothing reads across tenants.
- Fulfillment is idempotent and survives retries, duplicate events and concurrent task completion.
- A published workflow change never strands or silently rewrites running instances.

## Considered options

1. **Catalog items as spec objects; requests and tasks as ordinary records bound by a request binding on the entity; fulfillment as an outbox event consumer; workflow definitions registered per version at activation and pinned per record.**
2. Catalog items as records (data, not configuration).
3. Fulfillment inside the request's write transaction (synchronous hook).

## Decision outcome

**Chosen option 1.**

### Catalog items and typed variables

- `CatalogItemSpec` joins the spec catalog (`SpecCatalog.CatalogItems`): ID, label, description, category, icon, the request entity it creates, `variables`, `tasks`, `active`, `order`.
- A variable (`VariableSpec`) has a key, label, help, required flag and a type from a subset of field types: `short_text`, `long_text`, `integer`, `decimal`, `date`, `date_time`, `single_select`, `multi_select`, `toggle`, `email`, `user`, `group`. Choice variables use a choice list. Values are normalized exactly like fields of the same type (`FieldNormalizer`).
- Two field types carry a request: `catalog_item` (the item ID, validated against the active configuration) and `variables` (a JSON object keyed by variable key). The variables field is never filterable, sortable or searchable.
- `RequestHook` (platform write hook, order 150) validates and normalizes variables against the request's catalog item on create and update: unknown keys, wrong types, missing required values and inactive items are field errors on `variables.<key>`. The catalog item cannot change after create.
- Catalog items are configuration because they are authored and reviewed like forms (draft, publish, upgrade) and the master catalog lists "Catalog item" as an experience element. Option 2 would put form definitions outside drafts, publish approval and upgrades.

### Request binding and fulfillment

- An entity that receives requests declares `request` (`RequestBindingSpec`): its catalog item and variables fields, the task entity and the task's request, key and title fields, the request states that start fulfillment, the system transition fired when fulfillment completes, the outcome field, and the task release and cancel transitions.
- A task template (`FulfillmentTaskSpec`) has an ID unique within its item, a title, an optional description and assignment group key, `dependsOn` (other task IDs; acyclic), and an optional `when` condition over `variables` and `record`. Tasks whose condition is false are not created; dependencies on them count as satisfied.
- `FulfillmentConsumer` (an outbox event consumer, so it inherits inbox deduplication and per-record ordering) reacts to request and task events:
  - **Start:** when a request enters a start state, create one task per applicable template, keyed by `(request, task key)`. Existing keys are skipped, so a replayed or duplicated event never creates a second task. Tasks start `blocked`; templates with no unmet dependencies are released at once.
  - **Progress:** when a task reaches a `resolved`, `closed` or `cancelled` category state, release blocked siblings whose dependencies are all finished. A skipped (cancelled) dependency counts as finished, so the remaining work can go on (ASM-041).
  - **Completion:** when every task is finished, fire the request's completion transition and set the outcome to `complete`, or `partial` when any task was cancelled (skipped). If every task was skipped, the outcome is `partial` too; the request owner decides what happens next.
  - **Rejection and cancellation:** when a request enters a `cancelled` category state, cancel its open and blocked tasks.
- System writes use the record engine with a system actor (`WriteOperations.System` for field sets, a system transition for state changes) and expected revisions; a stale revision makes the consumer retry. A transition that is no longer valid (for example a second "complete" after the first won the race) is a no-op, so concurrent completions converge.
- Request versus task SLAs are owned in G2-M4: the request carries the requester-facing SLA, tasks carry team SLAs.
- Option 3 would couple the requester's write latency to task fan-out and would not get retries, backoff and dead letters for free.

### Workflow versions (REQ-MIG-003)

- New table `kernel.workflow_definitions` (tenant, environment, workflow, version, content hash, definition, first configuration revision), FORCE RLS. Records gain `workflow_version`.
- At activation, the activator registers each workflow whose content hash (state field, initial state, states, transitions) differs from its latest registered version as version n + 1. Before that, it registers the previous active revision's definitions if they are missing, and pins records that have no version to the definition they were running. This one-time step covers records created before this change.
- On create, a record is pinned to the current version of its entity's workflow. Transitions use the pinned definition; field validation, rules and permissions always use the current configuration ("authorization is checked using current access rules"). Transition buttons come from the pinned definition too.
- `WorkflowSpec.activeInstances` sets what happens to running instances when a published change creates a new version:
  - `pin` (default): instances finish on the version they started with.
  - `migrate` with a `stateMap` (old state to new state; unmapped states that still exist keep their ID): a durable job (`workflow.migrate`, checkpointed and idempotent) moves instances in batches, re-pins them, and audits each move. Instances whose state has no target stay pinned and are reported.
  - `cancel` with a `cancelState` in the `cancelled` category: the job moves active instances there. The publish approval, which the review dialog shows with the instance counts, is the approval for the cancellation.
- Instances in terminal categories (`resolved`, `closed`, `cancelled`) are never migrated or cancelled.
- The draft review lists each changed workflow with its active instances per pinned version and the policy outcome before anyone publishes.

### Overlay operations

New typed operations, each with a description, targets, composer support, validation and conflict detection:

| Operation | Effect |
| --- | --- |
| `catalog_item.add` | Adds a tenant catalog item (`tenant__` ID) with its variables and tasks |
| `catalog_item.update` | Patches label, description, category, icon, active, order; replaces variables and tasks of tenant items only; `expected` values detect conflicts |
| `catalog_item.remove` | Removes a tenant item; base items are deactivated instead |
| `workflow.add_state` | Adds a tenant state and its option in the state field's choice list |
| `workflow.add_transition` | Adds a tenant transition |
| `workflow.update_transition` | Patches label, from, to, guard, roles, required fields, set values, tone or enabled; base transitions accept only label, guard message and enabled |
| `workflow.set_active_instances` | Sets the active-instance policy |

The MCP server exposes them as `add_catalog_item`, `update_catalog_item`, `add_workflow_state`, `add_transition`, `update_transition` and `set_workflow_policy`.

### Consequences

- Good: one record engine, one authorization model and one audit trail for tickets, requests and tasks.
- Good: catalog changes and workflow changes ride the existing draft, review, publish, upgrade and rollback machinery.
- Good: fulfillment is idempotent by construction (`(request, task key)`), so duplicate events and retries are safe.
- Bad: fulfillment is eventually consistent; tasks appear a moment after "Start fulfillment" (typically well under a second with the worker's polling).
- Bad: definitions of old workflow versions are kept as long as any record pins them.
- Neutral: rolling back re-registers the restored definitions by content, so they get their earlier version number back; records keep the version they are pinned to, because every version's definition is stored.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Spec catalog items, bound records, event consumer, registered versions | Reuses drafts, publish, upgrades, permissions and outbox; idempotent; AI edits through overlay ops | New spec objects, ops and a consumer to maintain |
| 2. Catalog items as records | Quick to edit | No draft, review or upgrade; two ways to define forms |
| 3. Synchronous fulfillment | Tasks visible immediately | Slower writes; no retries or dead letters; harder to make idempotent |

## More information

- Module behavior: [Tickets module](../architecture/modules/tickets.md).
- Related: [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (overlay format), [ADR 0005](0005-job-and-workflow-execution.md) (jobs and outbox), [ADR 0009](0009-rule-expression-language.md) (expressions).
- Verification: `Requests.FulfillmentTests`, `Configuration.WorkflowVersioningTests`, `Configuration.CatalogOverlayTests`, Playwright `requests.spec.ts`.
