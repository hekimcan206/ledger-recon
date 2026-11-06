using LedgerRecon.Api.Persistence;
using LedgerRecon.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LedgerRecon.IntegrationTests;

/// <summary>
/// The database defends the ledger's invariants on its own, independent of the C# code: raw SQL that bypasses
/// the service, as the application role or even as the schema owner, cannot corrupt the books.
/// </summary>
public sealed class DatabaseInvariantTests(PostgresFixture db)
{
    private static async Task<(long A, long B)> TwoAccountsAsync(NpgsqlDataSource dataSource, string currencyB = "TRY")
    {
        var p = Infrastructure.Ledger.NewPrefix();
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO ledger.accounts (code, name, type, currency) VALUES ($1, 'a', 'asset', 'TRY'), ($2, 'b', 'asset', $3) RETURNING id
            """);
        cmd.Parameters.Add(new() { Value = p + "A" });
        cmd.Parameters.Add(new() { Value = p + "B" });
        cmd.Parameters.Add(new() { Value = currencyB });
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var a = reader.GetInt64(0);
        await reader.ReadAsync();
        return (a, reader.GetInt64(0));
    }

    private static async Task<PostgresException> InsertEntryAsync(NpgsqlDataSource dataSource, long debitAccount, long creditAccount, long debit, long credit, string currency = "TRY")
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var id = Guid.NewGuid();
        await using var batch = new NpgsqlBatch(connection, tx);
        var entry = new NpgsqlBatchCommand("""
            INSERT INTO ledger.journal_entries (id, idempotency_key, request_hash, description, value_date, currency)
            VALUES ($1, $2, $3, 'raw sql', DATE '2026-11-05', $4)
            """);
        entry.Parameters.Add(new() { Value = id });
        entry.Parameters.Add(new() { Value = "raw-" + id });
        entry.Parameters.Add(new() { Value = new byte[32] });
        entry.Parameters.Add(new() { Value = currency });
        var postings = new NpgsqlBatchCommand("""
            INSERT INTO ledger.postings (entry_id, line_no, account_id, currency, direction, amount_minor)
            VALUES ($1, 1, $2, $4, 'D', $5), ($1, 2, $3, $4, 'C', $6)
            """);
        postings.Parameters.Add(new() { Value = id });
        postings.Parameters.Add(new() { Value = debitAccount });
        postings.Parameters.Add(new() { Value = creditAccount });
        postings.Parameters.Add(new() { Value = currency });
        postings.Parameters.Add(new() { Value = debit });
        postings.Parameters.Add(new() { Value = credit });
        batch.BatchCommands.Add(entry);
        batch.BatchCommands.Add(postings);

        var act = async () =>
        {
            await batch.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        };
        return (await act.Should().ThrowAsync<PostgresException>()).Which;
    }

    [Fact]
    public async Task Unbalanced_entry_fails_at_commit_through_the_deferred_trigger()
    {
        await using var app = PostgresFixture.CreateDataSource(db.AppConnectionString);
        var (a, b) = await TwoAccountsAsync(app);

        var error = await InsertEntryAsync(app, a, b, debit: 10_000, credit: 9_999);

        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("journal_entry_balanced");
    }

    [Fact]
    public async Task Entry_without_postings_fails_at_commit()
    {
        await using var app = PostgresFixture.CreateDataSource(db.AppConnectionString);
        await using var connection = await app.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ledger.journal_entries (id, idempotency_key, request_hash, description, value_date, currency)
            VALUES (gen_random_uuid(), 'empty-' || gen_random_uuid(), decode(repeat('00', 32), 'hex'), 'empty', DATE '2026-11-05', 'TRY')
            """, connection, tx);
        await cmd.ExecuteNonQueryAsync();

        var error = (await tx.Invoking(t => t.CommitAsync()).Should().ThrowAsync<PostgresException>()).Which;
        error.ConstraintName.Should().Be("journal_entry_balanced");
    }

    [Fact]
    public async Task Posting_currency_must_match_the_account_currency()
    {
        await using var app = PostgresFixture.CreateDataSource(db.AppConnectionString);
        var (a, b) = await TwoAccountsAsync(app, currencyB: "USD");

        var error = await InsertEntryAsync(app, a, b, 100, 100);

        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.Should().Be("postings_account_currency_fkey");
    }

    [Theory]
    [InlineData("UPDATE ledger.postings SET amount_minor = amount_minor + 1")]
    [InlineData("DELETE FROM ledger.journal_entries")]
    [InlineData("DELETE FROM ledger.accounts")]
    [InlineData("TRUNCATE ledger.postings")]
    [InlineData("SELECT * FROM ledger_admin.schema_migrations")]
    [InlineData("CREATE TABLE ledger.sneaky (id int)")]
    public async Task Application_role_cannot_rewrite_history_or_change_schema(string sql)
    {
        await using var app = PostgresFixture.CreateDataSource(db.AppConnectionString);
        await using var cmd = app.CreateCommand(sql);

        var error = (await cmd.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("UPDATE ledger.postings SET amount_minor = amount_minor WHERE id = (SELECT min(id) FROM ledger.postings)")]
    [InlineData("DELETE FROM ledger.journal_entries WHERE id = (SELECT entry_id FROM ledger.postings ORDER BY id LIMIT 1)")]
    public async Task Even_the_schema_owner_cannot_edit_posted_history(string sql)
    {
        // Make sure at least one entry exists, posted through the API.
        await using (var factory = new ApiFactory(db.AppConnectionString))
        {
            using var client = factory.CreateClient();
            var p = Infrastructure.Ledger.NewPrefix();
            await client.OpenAsync(p + "X", "asset");
            await client.OpenAsync(p + "Y", "asset");
            (await client.SendAsync(Infrastructure.Ledger.PostEntry("owner-" + p, Infrastructure.Ledger.Transfer(p + "X", p + "Y", 1m)))).EnsureSuccessStatusCode();
        }

        await using var migrator = PostgresFixture.CreateDataSource(db.MigratorConnectionString);
        await using var cmd = migrator.CreateCommand(sql);
        var error = (await cmd.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
        error.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task Overdraft_protection_holds_even_for_a_direct_balance_update()
    {
        await using var app = PostgresFixture.CreateDataSource(db.AppConnectionString);
        await using var insert = app.CreateCommand("INSERT INTO ledger.accounts (code, name, type, currency, prevent_overdraft) VALUES ($1, 'w', 'liability', 'TRY', true)");
        var code = Infrastructure.Ledger.NewPrefix() + "W";
        insert.Parameters.Add(new() { Value = code });
        await insert.ExecuteNonQueryAsync();

        await using var update = app.CreateCommand("UPDATE ledger.accounts SET balance_minor = 1 WHERE code = $1");
        update.Parameters.Add(new() { Value = code });
        var error = (await update.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which;
        error.ConstraintName.Should().Be("accounts_no_overdraft");
    }

    [Fact]
    public async Task Migrations_rerun_as_no_op_and_refuse_an_edited_script()
    {
        await using var migrator = PostgresFixture.CreateDataSource(db.MigratorConnectionString);
        var runner = new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance);
        (await runner.MigrateAsync()).Should().Be(0);

        await using var tamper = migrator.CreateCommand("UPDATE ledger_admin.schema_migrations SET sha256 = 'edited' WHERE version = 2");
        await tamper.ExecuteNonQueryAsync();
        try
        {
            (await runner.Invoking(r => r.MigrateAsync()).Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*0002_reconciliation changed after it was applied*");
        }
        finally
        {
            await using var restore = migrator.CreateCommand("UPDATE ledger_admin.schema_migrations SET sha256 = $1 WHERE version = 2");
            restore.Parameters.Add(new() { Value = MigrationRunner.Load().Single(m => m.Version == 2).Sha256 });
            await restore.ExecuteNonQueryAsync();
        }
    }
}
