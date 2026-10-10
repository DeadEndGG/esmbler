using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Esmbler.Kernel.Infrastructure.Persistence;

/// <summary>
/// Applies embedded, versioned SQL migrations as the owner role (ADR 0010). Each file runs in its own transaction
/// under an advisory lock; applied files are recorded with checksums and edits to applied files are refused.
/// </summary>
public sealed class MigrationRunner(ILogger<MigrationRunner> logger)
{
    private const long LockKey = 0x45534d42; // "ESMB"

    public static IReadOnlyList<(string Version, string Sql)> Embedded()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Path.GetFileNameWithoutExtension(n["migrations/".Length..]), reader.ReadToEnd());
            })
            .ToList();
    }

    public async Task<int> RunAsync(string migratorConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken);
        await Exec(connection, $"SELECT pg_advisory_lock({LockKey})", cancellationToken);
        try
        {
            await Exec(connection, "CREATE SCHEMA IF NOT EXISTS esmbler_meta; CREATE TABLE IF NOT EXISTS esmbler_meta.schema_migrations (version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", cancellationToken);
            var applied = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM esmbler_meta.schema_migrations", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var count = 0;
            foreach (var (version, sql) in Embedded())
            {
                var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                if (applied.TryGetValue(version, out var existing))
                {
                    if (existing != checksum)
                    {
                        throw new InvalidOperationException($"Migration {version} was edited after it was applied. Add a new migration instead.");
                    }

                    continue;
                }

                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                await using (var cmd = new NpgsqlCommand(sql, connection, tx))
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var cmd = new NpgsqlCommand("INSERT INTO esmbler_meta.schema_migrations (version, checksum) VALUES ($1, $2)", connection, tx))
                {
                    cmd.Parameters.AddWithValue(version);
                    cmd.Parameters.AddWithValue(checksum);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                logger.LogInformation("Applied migration {Version}", version);
                count++;
            }

            return count;
        }
        finally
        {
            await Exec(connection, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private static async Task Exec(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
