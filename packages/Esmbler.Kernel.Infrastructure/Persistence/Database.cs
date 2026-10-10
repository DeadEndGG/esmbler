using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Esmbler.Kernel.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    /// <summary>Runtime role (esmbler_app): DML only, subject to RLS.</summary>
    public required string AppConnectionString { get; set; }

    /// <summary>Owner role (esmbler_migrator): used only by the migration runner.</summary>
    public string? MigratorConnectionString { get; set; }
}

/// <summary>
/// Unit of work with transaction-scoped tenant context (ADR 0010, REQ-TEN-001). Every transaction begins with
/// set_config(..., is_local = true), so pooled connections never carry another tenant's context.
/// </summary>
public sealed class Database(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource => dataSource;

    public Task<T> InTenantAsync<T>(Guid tenantId, Guid environmentId, Guid? principalId, Func<KernelDbContext, Task<T>> work, CancellationToken cancellationToken) =>
        RunAsync(tenantId, environmentId, principalId, work, cancellationToken);

    public Task InTenantAsync(Guid tenantId, Guid environmentId, Guid? principalId, Func<KernelDbContext, Task> work, CancellationToken cancellationToken) =>
        RunAsync<bool>(tenantId, environmentId, principalId, async db =>
        {
            await work(db);
            return true;
        }, cancellationToken);

    /// <summary>Tenant-wide (no environment) work, e.g. identity administration.</summary>
    public Task<T> InTenantWideAsync<T>(Guid tenantId, Guid? principalId, Func<KernelDbContext, Task<T>> work, CancellationToken cancellationToken) =>
        RunAsync(tenantId, null, principalId, work, cancellationToken);

    /// <summary>Principal-only context (login, tenant switcher): sees the principal's own memberships.</summary>
    public Task<T> AsPrincipalAsync<T>(Guid principalId, Func<KernelDbContext, Task<T>> work, CancellationToken cancellationToken) =>
        RunAsync(null, null, principalId, work, cancellationToken);

    /// <summary>No context at all: only SECURITY DEFINER functions return data.</summary>
    public Task<T> AnonymousAsync<T>(Func<KernelDbContext, Task<T>> work, CancellationToken cancellationToken) =>
        RunAsync(null, null, null, work, cancellationToken);

    private async Task<T> RunAsync<T>(Guid? tenantId, Guid? environmentId, Guid? principalId, Func<KernelDbContext, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT set_config('app.tenant_id', $1, true), set_config('app.environment_id', $2, true), set_config('app.principal_id', $3, true)";
            cmd.Parameters.Add(new NpgsqlParameter { Value = tenantId?.ToString() ?? string.Empty });
            cmd.Parameters.Add(new NpgsqlParameter { Value = environmentId?.ToString() ?? string.Empty });
            cmd.Parameters.Add(new NpgsqlParameter { Value = principalId?.ToString() ?? string.Empty });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var options = new DbContextOptionsBuilder<KernelDbContext>()
            .UseNpgsql(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        await using var db = new KernelDbContext(options);
        await db.Database.UseTransactionAsync(tx, cancellationToken);
        try
        {
            var result = await work(db);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
