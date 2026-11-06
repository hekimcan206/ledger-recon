using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace LedgerRecon.Api.Persistence;

internal static class PersistenceExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddSingleton(_ => CreateDataSource(connectionString));
        services.AddSingleton<MigrationRunner>();
        return services;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // A request must never queue forever behind a hot account lock: after lock_timeout it fails with 55P03,
        // the transaction (including its idempotency claim) rolls back, and the client gets a retryable 503.
        builder.ConnectionStringBuilder.Options ??= "-c lock_timeout=5000 -c idle_in_transaction_session_timeout=30000";

        // No Kerberos here; skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.Build();
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(cancellationToken);
}

/// <summary>Readiness: can we open a connection and run a trivial query within the probe timeout?</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.", ex);
        }
    }
}
