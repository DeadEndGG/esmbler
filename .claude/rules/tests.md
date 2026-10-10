---
paths:
  - "tests/**"
---

# Tests

- Integration tests use `PostgresFixture` (real Postgres 18, production roles) and connect as `esmbler_app`; never as superuser except to assert role properties.
- Every new write path needs tests for: authorization denial, tenant/environment isolation, and validation errors.
- Acceptance scenarios map to `REQ-ACC-*` IDs; name the ID in the test class summary and update `docs/spec/traceability.md`.
- Never skip, disable, or quarantine a test to get green; fix the cause or log a known issue.
