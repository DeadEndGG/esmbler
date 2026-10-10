# Tickets module (core.tickets)

Default Tickets/Service Requests module and the reference implementation of a module built only on the public SDK.

- Owns entity `core.tickets.ticket`, choice lists `core.tickets.*`, workflow `core.tickets.lifecycle`, views `core.tickets.record|list|quick_create`, roles agent/requester/admin.
- Releases: `Esmbler.Modules.Tickets/Releases/<version>/module.json` (1.0.0, 1.1.0). Shipped releases are immutable; changes go in a new version with declarative migrations.
- Extension points: `core.tickets.hooks.before_write`, `core.tickets.hooks.after_write`, `core.tickets.strategy.assignment`, `core.tickets.slots.record_side_panel`.
- Events published (by the record engine): `record.created|updated|transitioned|deleted|restored` and `activity.added` for `core.tickets.ticket`.
- Invariants: reference only `Esmbler.Sdk` and `Esmbler.Modules.Tickets.Contracts`; no package references.
- Tests: `dotnet test tests/Esmbler.UnitTests --filter FullyQualifiedName~Configuration`. Docs: `docs/architecture/modules/tickets.md`.
