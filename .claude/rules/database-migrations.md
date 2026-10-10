---
paths:
  - "packages/Esmbler.Kernel.Infrastructure/Migrations/**"
  - "deploy/postgres/**"
---

# Database migrations

- Never edit an applied migration; add `NNNN_description.sql`. The runner refuses changed checksums.
- Every new tenant table: `tenant_id` (+ `environment_id` if environment-scoped), `ENABLE` and `FORCE ROW LEVEL SECURITY`, and a policy using `kernel.current_tenant()` / `kernel.current_environment()` for both `USING` and `WITH CHECK`.
- Grants: DML for `esmbler_app` only; append-only tables revoke UPDATE/DELETE. No TRUNCATE, no ownership.
- Cross-tenant access only via a narrow `SECURITY DEFINER` function owned by `esmbler_definer` with `SET search_path = kernel, pg_temp`; add it to the allow-list test in `DatabaseIsolationTests`.
- Physical per-tenant DDL (columns, tables, indexes) is prohibited (ADR 0002).
- Prove isolation with an integration test that connects as `esmbler_app`.
