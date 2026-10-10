---
paths:
  - "packages/Esmbler.Sdk/**"
  - "packages/Esmbler.Kernel/Configuration/**"
  - "modules/**/Releases/**"
---

# Spec schemas and configuration

- Spec model changes are public SDK changes: keep backward-compatible, update `Schemas/`, regenerate `docs/reference/`, and add validator checks with actionable messages (what, where, valid alternative).
- New overlay op types need: JSON discriminator, `Describe()`, `Targets()`, composer support with expected-value conflict detection, dependency-graph edges, and unit tests for apply, conflict, and upgrade.
- Shipped module releases are immutable; change behavior with a new release version and declarative migrations.
- Registry elements: JSON in `Registry/Elements/` validated by `element.schema.json`; specs reference design tokens only.
