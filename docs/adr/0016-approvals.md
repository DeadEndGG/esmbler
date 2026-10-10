---
title: "ADR 0016: Approvals"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-BIZ-004, REQ-AUTHZ-011, REQ-MIG-003, REQ-ACC-05, REQ-AISAFE-001]
---

# ADR 0016: Approvals

## Context and problem statement

Gate 2 inserts approvals between request submission and fulfillment. The master spec asks for approval policies with sequential and parallel stages, delegation, expiry and escalation, separation of duties, a defined behavior for requester changes after approval, and for active-instance behavior when a policy changes ("Approvals" row, "Governance"). Pending approvals must survive workflow migrations ("Migration and recovery"). The AI never approves anything on a person's behalf.

How are approval policies modeled, where do approval instances live, who may decide, and how does an approval drive the request's workflow?

## Decision drivers

- Policies are configuration: drafted, reviewed, published, upgraded and rolled back like every other spec, and editable by the AI builder through typed overlay operations.
- Decisions are human and attributable: the decider, the person they act for, the time and the reason are audited.
- Authorization is enforced on the server for every decision; the requester never approves their own request.
- Approvals in flight keep the rules they started under.
- Tenant isolation: every new table has FORCE RLS.

## Considered options

1. **Policies as spec objects referenced by catalog items; approval instances and approver steps in kernel tables; an outbox consumer starts approvals and a decision service drives the request through system transitions.**
2. Approvals as records of an approval entity (a module-defined "approval" entity with its own workflow).
3. Approvals as workflow guards (a transition that requires N approvals recorded as activity).

## Decision outcome

**Chosen option 1.**

### Policy model

- `ApprovalPolicySpec` joins the spec catalog (`SpecCatalog.ApprovalPolicies`): ID, label, description, the request entity it applies to, and ordered `stages`.
- A stage (`ApprovalStageSpec`) has an ID, a label, a `mode` (`all`: every approver must approve; `any`: the first approval decides), its `approvers`, an optional `when` condition over `record` and `variables` (a stage whose condition is false is skipped), and optional expiry: `expiresAfterHours` with `onExpiry` `reject` or `escalate` and `escalateTo` approvers.
- An approver (`ApproverSpec`) is a `group` (group key, any member decides), a `role` (any holder decides), or a `field` (`record.<field>` or `variables.<key>` holding a user or group, resolved when the stage starts).
- Stages run in order (sequential); the approvers of one stage decide in parallel.
- A catalog item names its policy (`approvalPolicy`). Items without one go straight to the agent queue as before.

### Request binding

The request binding gains the approval state, three system transitions (into approval, approved, declined) and an approvers field:

- On create, if the item has a policy, `ApprovalConsumer` fires the "into approval" transition and starts the approval. A request whose stages are all skipped is approved at once.
- When the last stage is approved, the "approved" transition moves the request into fulfillment, so the fulfillment consumer creates its tasks.
- When a stage is rejected or expires without escalation, the "declined" transition rejects the request.
- The approvers field (type `principals`, a platform-maintained list of user and group IDs) is an access field with relation `approver`. Approvers therefore see the request they decide on, with the field permissions of their own roles, and cannot edit it.

### Storage and engine

- `kernel.approvals` (one per request approval: policy ID, a snapshot of the policy, status, current stage, requester) and `kernel.approval_steps` (one per approver per stage: approver kind and reference, status, decider, the person acted for, comment, due time, escalation flag). `kernel.approval_delegations` holds out-of-office delegations. All three have FORCE RLS on tenant and environment; `esmbler_app` may select, insert and update, never delete.
- `ApprovalService.DecideAsync` locks the request row, checks eligibility, records the decision, evaluates the stage and either waits, starts the next stage, or finishes the approval with a system transition, all in one transaction with audit and events.
- Stage expiry is a durable timer (`approval.expire`, dedup key per approval and stage). On expiry, `reject` declines the request and `escalate` adds steps for `escalateTo` with a second deadline; a second expiry declines.
- When the request enters a cancelled-category state (cancelled by the requester, rejected, or a workflow `cancel` policy), the consumer cancels its pending steps.

### Who may decide

- The person is the step's approver (direct user, member of the group, or holder of the role, checked with fresh facts), or the active delegate of such a person. A delegate's decision records both people.
- Separation of duties: the requester can never decide on their own request, directly or through a delegate, whatever roles they hold; one person decides at most one step per stage, so a person in two groups cannot satisfy both halves of an `all` stage.
- AI agents cannot decide. Decisions require an interactive human session; MCP has no decision tool (REQ-AISAFE-001).
- Rejecting requires a comment.

### Delegation

A person delegates their approvals to another member of the workspace for a period (out of office). Delegations are their own records: the delegator creates and revokes them; workspace admins can list them. A delegation cannot point to the delegator, and an active delegation is not chained (the delegate of a delegate cannot act). When a delegation starts, the delegate is added to the approvers field of the delegator's pending requests so they can read them.

### Changes during and after approval

- Answers are frozen once the request leaves its initial state (ADR 0015), so a requester cannot change what was approved. To change a request after submission, the requester cancels it and raises a new one.
- Policy updates never change approvals in flight: each approval runs on the policy snapshot it started with. New requests use the new policy. The publish review notes how many approvals are pending.
- A workflow migration must keep requests in the approval state in that state or move them to a cancelled state (validator), so pending approvals are preserved or explicitly cancelled.

### Overlay operations and MCP

| Operation | Effect |
| --- | --- |
| `approval_policy.add` | Adds a tenant policy (`tenant__` ID) |
| `approval_policy.update` | Changes label, description and, for tenant policies, stages; a permission change, because it changes who approves |
| `approval_policy.remove` | Removes a tenant policy that no catalog item uses; a permission change |
| `catalog_item.update` with `approvalPolicy` | Sets the item's policy; clearing it is a permission change |

The MCP server exposes `list_approval_policies`, `add_approval_policy` and the `approval_policy` argument of `update_catalog_item`. It has no tool to decide approvals.

### Consequences

- Good: one approval engine for every request entity and module; policies ride drafts, review, publish and upgrades.
- Good: approvers get a narrow, audited read path to the requests they decide on, without new roles on the entity.
- Bad: the approvers field is maintained by the platform and grows with every stage; it never shrinks, so approvers keep seeing requests they decided on (intended for audit, and noted in the threat model).
- Bad: starting an approval is eventually consistent, like fulfillment.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Spec policies, kernel approval tables, consumer and decision service | Reuses drafts and publish; strong separation of duties; pinned snapshots | New tables and services |
| 2. Approval records of a module entity | Reuses the record engine and pages | Approvers need broad entity permissions; separation of duties and delegation would be rules, easy to weaken; every module would redefine it |
| 3. Workflow guards counting approvals | Small | No stages, expiry, delegation or audit of decisions |

## More information

- Related: [ADR 0015](0015-requests-catalog-fulfillment-and-workflow-versions.md) (requests and fulfillment), [ADR 0005](0005-job-and-workflow-execution.md) (jobs and timers), [ADR 0004](0004-authorization-engine.md) (authorization).
- Verification: `Approvals.ApprovalTests`, `Api.ApprovalApiTests`, `Configuration.CatalogOverlayTests`, Playwright `approvals.spec.ts`.
