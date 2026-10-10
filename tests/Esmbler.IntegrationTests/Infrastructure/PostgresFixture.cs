using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Esmbler.IntegrationTests.Infrastructure;

/// <summary>
/// Real PostgreSQL 18 with the production role setup (deploy/postgres/00-roles.sh) and migrations applied as the
/// owner role. Tests connect as the runtime role esmbler_app, so RLS is exercised exactly as in production.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppPassword = "app-test-password";
    public const string MigratorPassword = "migrator-test-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithEnvironment("ESMBLER_APP_PASSWORD", AppPassword)
        .WithEnvironment("ESMBLER_MIGRATOR_PASSWORD", MigratorPassword)
        .WithResourceMapping(new FileInfo(Path.Combine(RepoRoot(), "deploy", "postgres", "00-roles.sh")), "/docker-entrypoint-initdb.d/")
        .Build();

    public string AppConnectionString { get; private set; } = "";

    public string MigratorConnectionString { get; private set; } = "";

    public string SuperuserConnectionString { get; private set; } = "";

    public NpgsqlDataSource AppDataSource { get; private set; } = null!;

    public Database Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(SuperuserConnectionString) { Database = "esmbler" };
        MigratorConnectionString = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Username = "esmbler_migrator", Password = MigratorPassword }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Username = "esmbler_app", Password = AppPassword, MaxPoolSize = 20 }.ConnectionString;
        await new MigrationRunner(NullLogger<MigrationRunner>.Instance).RunAsync(MigratorConnectionString, default);
        AppDataSource = NpgsqlDataSource.Create(AppConnectionString);
        Database = new Database(AppDataSource);
    }

    public async Task DisposeAsync()
    {
        await AppDataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Esmbler.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
