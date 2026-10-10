# Esmbler.Kernel.Infrastructure

Adapters and application services for the kernel: PostgreSQL (EF Core + Npgsql), SQL migrations, RLS unit of work, OpenFGA, record/config/draft/publish/upgrade/preview services, activation health check, outbox dispatcher, job runner, notification consumer.

- Data access only through `Persistence/Database` (`InTenantAsync`, `InTenantWideAsync`, `AsPrincipalAsync`, `AnonymousAsync`), which sets `app.tenant_id`/`app.environment_id`/`app.principal_id` transaction-locally.
- Migrations: `Migrations/NNNN_*.sql`, applied by `MigrationRunner` as `esmbler_migrator`; never edit applied files.
- Authorization store: one OpenFGA store per tenant, model `Authorization/platform-model.fga(.json)`; derived from `kernel.group_members`/`kernel.role_assignments`. `AccessService` caches facts per (tenant, principal, `authz_revision`) for 5 s; sensitive actions (publish, rollback, modules, permission admin) and job re-authorization pass `fresh: true`.
- Activation (`ConfigActivator`) runs `ActivationHealthCheck` inside the activation transaction; a failure rolls the revision back.
- Invariants: record change + audit + outbox in one transaction; queries filter by permission inside SQL; no secrets in audit details.
- Tests: `dotnet test tests/Esmbler.IntegrationTests` (needs Docker).
