using LedgerRecon.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(LedgerRecon.IntegrationTests.Infrastructure.PostgresFixture))]

namespace LedgerRecon.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL 17 container for the whole assembly. It is initialised with the same role script as docker
/// compose, migrated as <c>ledger_migrator</c>, and the API under test connects as <c>ledger_app</c>. Tests never
/// reset the database: each one works on its own accounts (unique code prefix) and keys, and the global invariants
/// (every currency nets to zero, no drift) must hold across everything all tests wrote.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Test-only credentials for a throwaway container; never used outside this process.
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("ledger")
        .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        // Room for the concurrency tests: several pools of 50+ connections at once.
        .WithCommand("-c", "max_connections=300")
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
            "/docker-entrypoint-initdb.d/01-roles.sh")
        .Build();

    public string SuperuserConnectionString => _container.GetConnectionString();

    public string MigratorConnectionString => WithCredentials("ledger_migrator", MigratorPassword);

    public string AppConnectionString => WithCredentials("ledger_app", AppPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var dataSource = PersistenceExtensions.CreateDataSource(MigratorConnectionString);
        await new MigrationRunner(dataSource, NullLogger<MigrationRunner>.Instance).MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public static NpgsqlDataSource CreateDataSource(string connectionString) => PersistenceExtensions.CreateDataSource(connectionString);

    private string WithCredentials(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = username,
            Password = password,
            MaxPoolSize = 60,
            // Generous connect timeout: on a loaded laptop or CI runner, 50 backends forking at once can take a while.
            Timeout = 60,
        }.ConnectionString;
}
