# Esmbler.Platform

Shared platform services that plug into kernel hooks (order in parentheses): workflow (`Workflow/`, 100), catalog requests (`Requests/RequestHook.cs`, 150), assignment (`Assignment/`, 200), rules (`Rules/`, 300). In-app notifications and request fulfillment are event consumers in `Esmbler.Kernel.Infrastructure` (`Events/NotificationConsumer.cs`, `Events/FulfillmentConsumer.cs`).

- Rules: data-scope conditional and validation rules on every write; read-only evaluated on the prior state; set_value in order; errors fail closed for validation.
- Workflow: state changes only via transitions (from-state, roles, guard, required fields, set expressions), evaluated on the record's pinned definition version (`RecordWriteContext.EffectiveWorkflow`). Disabled transitions are refused; system transitions are hidden and accepted only from system writers (`SystemActor`).
- Requests: the catalog item must exist, target the entity and be active on create, and never changes; variables are normalized like fields of their type, with errors on `variables.<key>` (ADR 0015).
- Invariants: never reference modules or infrastructure; deterministic, no network in synchronous hooks.
- Tests: `dotnet test tests/Esmbler.UnitTests --filter FullyQualifiedName~Records`.
