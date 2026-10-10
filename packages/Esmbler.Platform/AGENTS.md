# Esmbler.Platform

Shared platform services that plug into kernel hooks: rules (`Rules/`), workflow (`Workflow/`), assignment (`Assignment/`). In-app notifications are an event consumer in `Esmbler.Kernel.Infrastructure` (`Events/NotificationConsumer.cs`).

- Rules: data-scope conditional and validation rules on every write; read-only evaluated on the prior state; set_value in order; errors fail closed for validation.
- Workflow: state changes only via transitions (from-state, roles, guard, required fields, set expressions).
- Invariants: never reference modules or infrastructure; deterministic, no network in synchronous hooks.
- Tests: `dotnet test tests/Esmbler.UnitTests --filter FullyQualifiedName~Records`.
