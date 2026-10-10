# Tickets module (core.tickets)

Default Tickets/Service Requests module and the reference implementation of a module built only on the public SDK.

- Owns entities `core.tickets.ticket`, `core.tickets.request` and `core.tickets.request_task` (1.2.0), choice lists `core.tickets.*`, workflows `core.tickets.lifecycle|request_lifecycle|task_lifecycle`, views `core.tickets.record|list|quick_create|request_record|request_list|task_record|task_list`, catalog items `core.tickets.catalog.*`, approval policies `core.tickets.approval.manager|access` (1.3.0), roles agent/requester/admin and approver (1.3.0, `TicketsIds.ApproverRole`).
- Releases: `Esmbler.Modules.Tickets/Releases/<version>/module.json` (1.0.0, 1.1.0, 1.2.0, 1.3.0, 1.4.0). Shipped releases are immutable; changes go in a new version with declarative migrations.
- Extension points: `core.tickets.hooks.before_write`, `core.tickets.hooks.after_write`, `core.tickets.strategy.assignment`, `core.tickets.slots.record_side_panel`.
- Events published: by the record engine, `record.created|updated|transitioned|deleted|restored` and `activity.added` for each entity; declared by the module (1.4.0, ADR 0017), `core.tickets.ticket.escalated` (transition `escalate`), `core.tickets.ticket.priority_raised` (rule `core.tickets.rules.p1_raised`, when a ticket becomes P1) and `core.tickets.ticket.resolved` (transition `resolve`). Event type IDs stay in the `core.tickets.` namespace (validator). Request fulfillment (task creation, release, completion) is platform behavior driven by the request binding (`FulfillmentConsumer`, ADR 0015), not module code; so are approvals (`ApprovalService`, ADR 0016), declared by the binding's `approval` block and the catalog items' `approvalPolicy`.
- Invariants: reference only `Esmbler.Sdk` and `Esmbler.Modules.Tickets.Contracts`; no package references.
- Tests: `dotnet test tests/Esmbler.UnitTests --filter FullyQualifiedName~Configuration`. Docs: `docs/architecture/modules/tickets.md`.
