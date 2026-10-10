# Esmbler.Sdk

The public SDK: everything first-party and third-party modules may depend on (dogfooding rule, REQ-MOD-001).

- `Specs/`: spec model (entities, fields, rules, workflows with active-instance policies, views/layout nodes, roles, permission sets, migrations, catalog items and request bindings in `Specs/Requests.cs`, approval policies and the approval binding in `Specs/Approvals.cs`, event types and emits in `Specs/Events.cs` (ADR 0017); field type `principals` for platform-maintained user and group lists). Serialized with `SpecJson` (camelCase, snake_case enums).
- `Overlay/`: typed overlay operations (Command pattern, 28 ops including `approval_policy.add|update|remove` and `event_type.add|remove`) and namespace rules (`tenant__` prefix). Ops that change who approves (an item's `approvalPolicy`, a policy's stages, policy removal) report `AffectsPermissions`; `ConfigCompiler.PermissionDiff` also flags them at publish.
- `Registry/`: element registry model and the Gate 1 catalog (`Registry/Elements/*.json`, schema `Schemas/element.schema.json`).
- `Modules/`: `IModuleDefinition`, release loading, `SemVer`. `Events/`: event envelope (v2, read alongside v1) and platform event type IDs.
- Invariants: no references to internal projects; additive, backward-compatible changes only; every element/spec change updates schemas and generated docs.
- Tests: `dotnet test tests/Esmbler.UnitTests`. Docs: `docs/reference/element-registry-schema.md`, `docs/adr/0003-configuration-artifact-and-overlay-format.md`.
