---
paths:
  - "packages/Esmbler.Kernel/Authorization/**"
  - "packages/Esmbler.Kernel.Infrastructure/Authorization/**"
  - "packages/Esmbler.Kernel.Infrastructure/Identity/**"
---

# Authorization and policy code

- Decisions come from `EffectivePermissions` (pinned config + relationship facts). Do not add per-endpoint permission checks that bypass it.
- Fail closed: any graph error must surface as `AuthorizationUnavailableException` → deny.
- Agents: intersection with scopes; never grant `config.publish`, `permissions.admin`, `modules.manage`, or `records.export` to an agent context.
- Permission-affecting overlay ops (`AffectsPermissions`) require `permissions.admin` in addition to `config.publish`.
- Lists: authorization is part of the SQL WHERE (`QueryCompiler.PermissionPredicate`); never filter after paging. Keep the SQL predicate and `EffectivePermissions.CanRead` equivalent (conformance test).
- Relationship writes: revoke in the graph before committing the DB change; grant after commit with retry.
