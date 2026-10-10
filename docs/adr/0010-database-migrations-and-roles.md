---
title: "ADR 0010: Database migrations and roles"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-TEN-001, REQ-TEN-002, REQ-REC-007, REQ-OPS-002, REQ-OPS-006, REQ-MIG-003, REQ-BIZ-004]
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

**Chosen option 1.** Files `packages/Esmbler.Kernel.Infrastructure/Migrations/NNNN_description.sql` are embedded resources. `MigrationRunner` applies them as `esmbler_migrator` under an advisory lock, each in its own transaction, recording `esmbler_meta.schema_migrations (version, checksum, applied_at)` (SHA-256 checksum; the meta schema is outside `kernel`, so the runtime role has no access). The API runs it at startup when `Esmbler:MigrateOnStartup` is true (Docker Compose sets this), or on demand with `dotnet run --project apps/Esmbler.Api -- migrate`. A changed checksum of an applied file stops startup. EF Core is still the data-access library (mapping, LINQ for simple reads); it never generates schema.

### Roles

Created once by `deploy/postgres/00-roles.sh` on first Postgres start; passwords come from the environment.

| Role | Attributes | Owns | Grants | Used by |
|---|---|---|---|---|
| `esmbler_migrator` | LOGIN, NOSUPERUSER, NOCREATEROLE, NOBYPASSRLS; may `SET ROLE esmbler_definer` (without inheriting it) | database `esmbler`, schema `kernel`, all tables | full DDL | Migration runner only |
| `esmbler_app` | LOGIN, NOSUPERUSER, NOBYPASSRLS, NOCREATEDB, NOCREATEROLE, not owner | nothing | `SELECT, INSERT, UPDATE, DELETE` on `kernel` tables; `INSERT, SELECT` only on `kernel.audit_log`; no `DELETE` on `kernel.tenants`, `kernel.principals`, `kernel.config_artifacts`; `SELECT, INSERT` only on `kernel.workflow_definitions`; `SELECT, INSERT, UPDATE` only on `kernel.approvals`, `kernel.approval_steps`, `kernel.approval_delegations`; `EXECUTE` on the privileged functions below | API, MCP, Worker |
| `esmbler_definer` | NOLOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, BYPASSRLS | the `SECURITY DEFINER` functions | `USAGE, CREATE` on schema `kernel`; `SELECT` on `kernel.principals`; `SELECT, INSERT` on `kernel.tenants`; `SELECT, UPDATE` on `kernel.outbox`, `kernel.jobs` | Nobody logs in as it; privileged functions run with its rights |
| `esmbler_openfga` | LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOBYPASSRLS | database `openfga` | none in `esmbler` | OpenFGA datastore ([ADR 0004](0004-authorization-engine.md)) |
| `esmbler_backup` (planned, Gate 2) | LOGIN, NOSUPERUSER, BYPASSRLS | nothing | `SELECT` on all tables (`pg_read_all_data`) | `pg_dump` and tenant restore-extract |

`PUBLIC` has no `CONNECT` on database `esmbler` and no `CREATE` on schema `public`. The OpenFGA datastore is a separate database (`openfga`) outside RLS, isolated by store ID.

### RLS policy

Every tenant table gets the same pattern:

```sql
CREATE FUNCTION kernel.current_tenant() RETURNS uuid LANGUAGE sql STABLE
AS $$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid $$;
-- kernel.current_environment() and kernel.current_principal() read app.environment_id and app.principal_id

ALTER TABLE kernel.records ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.records FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_environment_isolation ON kernel.records
  USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
  WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA kernel TO esmbler_app;
```

Unset or empty settings give `NULL`, and `tenant_id = NULL` is never true, so no rows are visible or writable. Tenant-wide identity tables (`groups`, `group_members`, `role_assignments`) use a tenant-only policy (`tenant_isolation`) without `environment_id` ([assumption ASM-016](../decisions/assumptions.md)). `kernel.tenants`, `kernel.environments`, `kernel.memberships` and `kernel.principals` are also visible to the current principal's own memberships (for the tenant switcher). `kernel.audit_log` allows tenant-wide entries with a null `environment_id`.

`kernel.audit_log` additionally has no UPDATE or DELETE grant, so an application bug cannot rewrite history.

### Transaction-local context

All data access goes through `Database` (`packages/Esmbler.Kernel.Infrastructure/Persistence/Database.cs`). Each of its entry points opens a transaction and sets the context first:

```sql
BEGIN;
SELECT set_config('app.tenant_id',      $1, true),   -- true = local to this transaction
       set_config('app.environment_id', $2, true),
       set_config('app.principal_id',   $3, true);
-- ... commands ...
COMMIT;   -- settings vanish; the pooled connection carries nothing to the next user
```

| Method | Context set | Used for |
|---|---|---|
| `InTenantAsync(tenant, environment, principal?, …)` | tenant, environment, principal | records, configuration, jobs: everything environment-scoped |
| `InTenantWideAsync(tenant, principal?, …)` | tenant, principal | identity data (groups, role assignments) and tenant-wide audit |
| `AsPrincipalAsync(principal, …)` | principal only | the principal's own memberships (tenant switcher) |
| `AnonymousAsync(…)` | nothing | login lookup, provisioning, health checks; only the privileged functions return rows |

`set_config(..., true)` is the parameterizable form of `SET LOCAL`. Code never sets context on a raw pooled connection.

### Privileged functions

Only `SECURITY DEFINER` functions owned by the `NOLOGIN BYPASSRLS` role `esmbler_definer` (not by the migrator, so they never run with owner or DDL rights), each with `SET search_path = kernel, pg_temp`, `EXECUTE` revoked from `PUBLIC` and granted to `esmbler_app`, returning minimal columns:

| Function | Purpose |
|---|---|
| `kernel.login_lookup(email)` | Password hash lookup before any context exists |
| `kernel.provision_tenant(id, slug, name)` | Insert a tenant before a context exists for it |
| `kernel.find_tenant_by_slug(slug)` | Resolve a tenant ID from its slug |
| `kernel.claim_outbox(worker, batch, lease_seconds)` | Worker claim of outbox events ([ADR 0005](0005-job-and-workflow-execution.md)) |
| `kernel.claim_jobs(worker, batch, lease_seconds)` | Fair worker claim of jobs ([ADR 0005](0005-job-and-workflow-execution.md)) |
| `kernel.refresh_lookup(hash)` | Look up a refresh token by its hash before the principal is known ([ADR 0011](0011-identity-and-development-authentication.md)); migration `0002_refresh_tokens.sql` |
| `kernel.bump_authz_revision()` | Trigger on `memberships`, `group_members` and `role_assignments` that increments `kernel.tenants.authz_revision` in the same transaction, so permission caches never reuse facts from before a revocation ([ADR 0004](0004-authorization-engine.md)); migration `0003_authz_revision.sql`. `EXECUTE` stays with `PUBLIC` because PostgreSQL only calls it as a trigger, and `esmbler_definer` may update only that column |

The first five are in `0001_kernel.sql`. `Isolation.DatabaseIsolationTests.Only_allow_listed_security_definer_functions_exist` fails if any other `SECURITY DEFINER` function appears in `kernel`, or if one is not owned by `esmbler_definer` or lacks the fixed `search_path`.

### Migrations so far

| File | Content |
|---|---|
| `0001_kernel.sql` | Kernel schema, roles' grants, RLS policies, privileged functions, outbox, inbox, jobs, drafts, artifacts, audit |
| `0002_refresh_tokens.sql` | `kernel.refresh_tokens` (hashed, owner-only RLS) and `refresh_lookup` |
| `0003_authz_revision.sql` | `kernel.tenants.authz_revision` and the `bump_authz_revision` triggers |
| `0004_refresh_session_cap.sql` | `refresh_tokens.session_started_at` for the absolute session lifetime |
| `0005_durable_jobs.sql` | Durable jobs and timers: `jobs.dedup_key` (one live job per key), `causation_id` and `depth` for automation limits, and a tenant-interleaving `claim_outbox` ([ADR 0005](0005-job-and-workflow-execution.md)) |
| `0006_workflow_versions.sql` | `kernel.workflow_definitions` (FORCE RLS, `esmbler_app` gets `SELECT`/`INSERT` only) and `records.workflow_version` with its index ([ADR 0015](0015-requests-catalog-fulfillment-and-workflow-versions.md)) |
| `0007_approvals.sql` | `kernel.approvals` (one per request approval, with a policy snapshot; at most one pending per record), `kernel.approval_steps` and `kernel.approval_delegations`; FORCE RLS on tenant and environment; `esmbler_app` gets `SELECT`/`INSERT`/`UPDATE`, never `DELETE`, so decisions stay an audit trail ([ADR 0016](0016-approvals.md)) |

### Consequences

- Good: policies, grants and functions are reviewed as SQL; the security reviewer agent and `.claude/rules/` for migrations target one folder.
- Good: isolation holds even if application code forgets a `WHERE tenant_id`.
- Bad: no generated migrations; EF model and SQL can drift, so an integration test comparing the EF model to the live schema is planned.
- Bad: we maintain a small runner (about 150 lines).

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| Plain SQL + runner | Exact SQL reviewed; RLS, roles, grants first-class | Hand-written; drift check needed |
| EF Core migrations | Generated from the model | RLS, policies, roles, grants and functions are raw `migrationBuilder.Sql` anyway; reviewers read C# that wraps SQL; easy to run as the wrong role |
| DbUp / Flyway / grate | Mature | Extra dependency (Flyway: JVM) for a feature we need in ~150 lines |

## More information

- Verification: `tests/Esmbler.IntegrationTests/Isolation/DatabaseIsolationTests.cs` (connects as `esmbler_app`): `Runtime_role_sees_only_the_current_tenant_environment`, `Writing_another_tenants_row_is_rejected_by_policy`, `Runtime_role_is_not_privileged_and_cannot_rewrite_audit` (also checks every `kernel` table has forced RLS), `Tenant_context_does_not_leak_across_pooled_connections`, `Only_allow_listed_security_definer_functions_exist`.
- Revisit triggers: dedicated-database tier (REQ-TEN-007) needs per-database migration orchestration; online migrations need lock-timeout tooling.
