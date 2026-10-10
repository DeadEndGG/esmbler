---
title: "ADR 0010: Database migrations and roles"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-TEN-001, REQ-TEN-002, REQ-REC-007, REQ-OPS-002, REQ-OPS-006]
---

# ADR 0010: Database migrations and roles

## Context and problem statement

Tenant isolation rests on PostgreSQL row-level security. The master spec ("Database enforcement") requires runtime roles that are not superusers, have no BYPASSRLS and do not own protected tables; FORCE ROW LEVEL SECURITY; constrained privileged functions; separate migration and backup roles; and transaction-scoped tenant context under connection pooling. The execution brief prescribes EF Core with Npgsql and `SET LOCAL` context. How are schema changes applied, and which roles exist?

## Decision drivers

- Reviewers must read the exact SQL for policies, grants and functions.
- Missing tenant context returns no rows (fail closed), never all rows.
- Context cannot leak between pooled connections.
- Migrations run once, in order, with tamper detection.

## Considered options

1. **Plain versioned SQL files embedded in `Esmbler.Kernel.Infrastructure`, applied by a small runner.**
2. EF Core migrations.
3. A third-party runner (DbUp, Flyway, grate).

## Decision outcome

**Chosen option 1.** Files `packages/Esmbler.Kernel.Infrastructure/Migrations/NNNN_description.sql` are embedded resources. `apps/Esmbler.Worker --migrate` applies them as `esmbler_migrator`, each in its own transaction, recording `kernel.schema_migrations (version, name, checksum sha256, applied_at)`. A changed checksum of an applied file stops startup. EF Core is still the data-access library (mapping, LINQ for simple reads); it never generates schema.

### Roles

| Role | Attributes | Owns | Grants | Used by |
|---|---|---|---|---|
| `esmbler_migrator` | LOGIN, NOSUPERUSER, NOBYPASSRLS | schema `kernel` and all tables, functions | full DDL | Migration runner only |
| `esmbler_app` | LOGIN, NOSUPERUSER, NOBYPASSRLS, NOCREATEDB, NOCREATEROLE, not owner | nothing | `SELECT, INSERT, UPDATE, DELETE` on tenant tables; `INSERT, SELECT` only on `kernel.audit`; `EXECUTE` on `kernel.claim_work` | API, MCP, Worker |
| `esmbler_backup` | LOGIN, NOSUPERUSER, BYPASSRLS | nothing | `SELECT` on all tables (`pg_read_all_data`) | `pg_dump` and tenant restore-extract (Gate 2) |

The OpenFGA datastore uses its own database and role, outside RLS ([ADR 0004](0004-authorization-engine.md)).

### RLS policy

Every tenant table gets the same pattern:

```sql
ALTER TABLE kernel.records ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.records FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON kernel.records
  USING (
    tenant_id      = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    AND environment_id = NULLIF(current_setting('app.environment_id', true), '')::uuid
  )
  WITH CHECK (
    tenant_id      = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    AND environment_id = NULLIF(current_setting('app.environment_id', true), '')::uuid
  );

GRANT SELECT, INSERT, UPDATE, DELETE ON kernel.records TO esmbler_app;
```

Unset or empty settings give `NULL`, and `tenant_id = NULL` is never true, so no rows are visible or writable. Tenant-wide identity tables (memberships, groups, role assignments) use a tenant-only policy without `environment_id` ([assumption ASM-016](../decisions/assumptions.md)).

`kernel.audit` additionally has no UPDATE or DELETE grant, so an application bug cannot rewrite history.

### Transaction-local context

A `TenantSessionInterceptor` (EF Core `DbTransactionInterceptor`) runs at the start of every transaction:

```sql
BEGIN;
SELECT set_config('app.tenant_id',      $1, true),   -- true = local to this transaction
       set_config('app.environment_id', $2, true),
       set_config('app.principal',      $3, true);
-- ... commands ...
COMMIT;   -- settings vanish; the pooled connection carries nothing to the next user
```

`set_config(..., true)` is the parameterizable form of `SET LOCAL`. Code paths without a transaction are rejected by the interceptor (`TenantContextMissingException`) rather than silently running without context.

### Privileged functions

Only `SECURITY DEFINER` functions owned by `esmbler_migrator`, each with `SET search_path = kernel, pg_temp`, returning minimal columns. Gate 1 has one: `kernel.claim_work` ([ADR 0005](0005-job-and-workflow-execution.md)). A test lists all `SECURITY DEFINER` functions and fails on any not in an allow-list.

### Consequences

- Good: policies, grants and functions are reviewed as SQL; the security reviewer agent and `.claude/rules/` for migrations target one folder.
- Good: isolation holds even if application code forgets a `WHERE tenant_id`.
- Bad: no generated migrations; EF model and SQL can drift, so an integration test compares the EF model to the live schema.
- Bad: we maintain a small runner (about 150 lines).

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| Plain SQL + runner | Exact SQL reviewed; RLS, roles, grants first-class | Hand-written; drift check needed |
| EF Core migrations | Generated from the model | RLS, policies, roles, grants and functions are raw `migrationBuilder.Sql` anyway; reviewers read C# that wraps SQL; easy to run as the wrong role |
| DbUp / Flyway / grate | Mature | Extra dependency (Flyway: JVM) for a feature we need in ~150 lines |

## More information

- Verification (planned, Gate 1, blocking): `Integration.Rls.Missing_context_returns_no_rows`, `Integration.Rls.Tenant_a_cannot_read_or_write_tenant_b`, `Integration.Rls.App_role_is_not_owner_and_has_no_bypassrls`, `Integration.Rls.Context_does_not_leak_across_pooled_connections`, `Integration.Rls.Audit_log_rejects_update_and_delete`, `Integration.Rls.Only_allowlisted_security_definer_functions`.
- Revisit triggers: dedicated-database tier (REQ-TEN-007) needs per-database migration orchestration; online migrations need lock-timeout tooling.
