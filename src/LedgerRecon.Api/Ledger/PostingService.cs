using System.Data;
using System.Diagnostics;
using LedgerRecon.Api.Infrastructure;
using LedgerRecon.Core;
using LedgerRecon.Core.Ledger;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace LedgerRecon.Api.Ledger;

internal sealed record EntryLineView(int LineNo, string AccountCode, Direction Direction, long AmountMinor);

internal sealed record EntryView(
    Guid Id,
    string IdempotencyKey,
    string Description,
    DateOnly ValueDate,
    string Currency,
    string? ExternalRef,
    DateTimeOffset PostedAt,
    IReadOnlyList<EntryLineView> Lines);

/// <param name="Replayed">True when the key had already been used with the same payload and nothing new was written.</param>
internal sealed record PostOutcome(EntryView Entry, bool Replayed);

/// <summary>
/// Posts journal entries exactly once per idempotency key and keeps account balances consistent under concurrency.
/// </summary>
/// <remarks>
/// One database transaction per request:
/// <list type="number">
/// <item><b>Claim the key.</b> <c>INSERT ... ON CONFLICT (idempotency_key) DO NOTHING</c>. The unique index is the
/// arbiter: a concurrent request with the same key blocks on the uncommitted index entry until the first commits
/// (then it sees the row and replays) or rolls back (then it proceeds as the first). No advisory lock, no
/// check-then-insert race, and the claim and the effects commit or vanish together.</item>
/// <item><b>Lock the accounts.</b> <c>SELECT ... FOR UPDATE</c> on the touched accounts in ascending id order, so two
/// entries touching the same accounts always wait in the same order and cannot deadlock.</item>
/// <item><b>Check rules on the locked rows</b> (existence, currency, overdraft), update balances, insert postings.</item>
/// <item><b>Commit.</b> The deferred constraint trigger re-checks that debits equal credits.</item>
/// </list>
/// Under <see cref="IsolationStrategy.Serializable"/> step 2 reads without locks and PostgreSQL's SSI aborts
/// conflicting transactions (40001), which are retried. See docs/adr/0002 for the measured comparison.
/// </remarks>
internal sealed partial class PostingService(
    NpgsqlDataSource dataSource,
    IOptions<LedgerOptions> options,
    LedgerMetrics metrics,
    TimeProvider timeProvider,
    ILogger<PostingService> logger)
{
    private readonly LedgerOptions _options = options.Value;

    public IsolationStrategy Strategy => _options.IsolationStrategy;

    public async Task<Result<PostOutcome>> PostAsync(string idempotencyKey, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var started = Stopwatch.GetTimestamp();
        var hash = RequestFingerprint.Compute(draft);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await PostOnceAsync(idempotencyKey, hash, draft, cancellationToken);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!result.IsSuccess)
                {
                    metrics.Rejected(result.Error.Code);
                }
                else if (result.Value.Replayed)
                {
                    metrics.Replayed(elapsed);
                }
                else
                {
                    metrics.Posted(elapsed);
                }

                return result;
            }
            catch (PostgresException ex) when (IsRetryable(ex) && attempt < _options.MaxAttempts)
            {
                metrics.Retried(ex.SqlState);
                LogRetry(ex.SqlState, attempt);
                // Full jitter, capped: spreads retries of transactions that collided on the same rows.
                var ceiling = Math.Min(200, 2 << Math.Min(attempt, 7));
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, ceiling)), timeProvider, cancellationToken);
            }
        }
    }

    private bool IsRetryable(PostgresException ex) =>
        _options.IsolationStrategy == IsolationStrategy.Serializable
        && ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private async Task<Result<PostOutcome>> PostOnceAsync(string key, byte[] hash, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        var serializable = _options.IsolationStrategy == IsolationStrategy.Serializable;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(
            serializable ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Claim the idempotency key.
        var entryId = Guid.CreateVersion7(timeProvider.GetUtcNow());
        DateTimeOffset postedAt;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO ledger.journal_entries (id, idempotency_key, request_hash, description, value_date, currency, external_ref)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING posted_at
            """, connection, tx))
        {
            claim.Parameters.Add(new() { Value = entryId });
            claim.Parameters.Add(new() { Value = key });
            claim.Parameters.Add(new() { Value = hash });
            claim.Parameters.Add(new() { Value = draft.Description });
            claim.Parameters.Add(new() { Value = draft.ValueDate });
            claim.Parameters.Add(new() { Value = draft.Currency });
            claim.Parameters.Add(new() { Value = (object?)draft.ExternalRef ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            var inserted = await claim.ExecuteScalarAsync(cancellationToken);
            if (inserted is null)
            {
                return await ReplayAsync(connection, tx, key, hash, cancellationToken);
            }

            postedAt = new DateTimeOffset(DateTime.SpecifyKind((DateTime)inserted, DateTimeKind.Utc));
        }

        // 2. Lock (or, under SSI, read) the touched accounts in a fixed order.
        var codes = draft.AccountCodes;
        var accounts = new Dictionary<string, LockedAccount>(StringComparer.Ordinal);
        await using (var select = new NpgsqlCommand(
            serializable
                ? "SELECT id, code, type, currency, prevent_overdraft, balance_minor FROM ledger.accounts WHERE code = ANY($1) ORDER BY id"
                : "SELECT id, code, type, currency, prevent_overdraft, balance_minor FROM ledger.accounts WHERE code = ANY($1) ORDER BY id FOR UPDATE",
            connection, tx))
        {
            select.Parameters.Add(new() { Value = codes.ToArray() });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var account = new LockedAccount(
                    reader.GetInt64(0), reader.GetString(1), AccountTypes.FromDbValue(reader.GetString(2)), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5));
                accounts[account.Code] = account;
            }
        }

        // 3. Rules that need current state. Every early return rolls back, releasing the idempotency claim, so a
        //    rejected request can be retried with the same key once the cause is fixed (e.g. after a deposit).
        var missing = codes.Where(c => !accounts.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
        {
            return LedgerError.Unprocessable("entry.unknown_account", $"Unknown account(s): {string.Join(", ", missing)}.");
        }

        var wrongCurrency = accounts.Values.Where(a => a.Currency != draft.Currency).Select(a => a.Code).ToArray();
        if (wrongCurrency.Length > 0)
        {
            return LedgerError.Unprocessable(
                "entry.currency_mismatch", $"Entry currency is {draft.Currency}; account(s) {string.Join(", ", wrongCurrency)} use another currency.");
        }

        var deltas = draft.NetDeltas();
        foreach (var (code, delta) in deltas)
        {
            var account = accounts[code];
            var after = account.BalanceMinor + delta;
            if (account.PreventOverdraft && account.Type.NormalBalance(after) < 0)
            {
                return LedgerError.Unprocessable(
                    "account.insufficient_funds",
                    $"Account {code} would go to {Money.Format(account.Type.NormalBalance(after))} {draft.Currency}; overdraft is not allowed.");
            }
        }

        // 4. Apply. Both statements go in one round trip.
        var touched = deltas.Where(d => d.Value != 0).OrderBy(d => accounts[d.Key].Id).ToArray();
        await using (var batch = new NpgsqlBatch(connection, tx))
        {
            var update = new NpgsqlBatchCommand("""
                UPDATE ledger.accounts AS a
                   SET balance_minor = a.balance_minor + d.delta, version = a.version + 1, updated_at = now()
                  FROM unnest($1::bigint[], $2::bigint[]) AS d(id, delta)
                 WHERE a.id = d.id
                """);
            update.Parameters.Add(new() { Value = touched.Select(d => accounts[d.Key].Id).ToArray() });
            update.Parameters.Add(new() { Value = touched.Select(d => d.Value).ToArray() });

            var insert = new NpgsqlBatchCommand("""
                INSERT INTO ledger.postings (entry_id, line_no, account_id, currency, direction, amount_minor)
                SELECT $1, l.line_no, l.account_id, $2, l.direction, l.amount
                  FROM unnest($3::smallint[], $4::bigint[], $5::text[], $6::bigint[]) AS l(line_no, account_id, direction, amount)
                """);
            insert.Parameters.Add(new() { Value = entryId });
            insert.Parameters.Add(new() { Value = draft.Currency });
            insert.Parameters.Add(new() { Value = Enumerable.Range(1, draft.Lines.Count).Select(i => (short)i).ToArray() });
            insert.Parameters.Add(new() { Value = draft.Lines.Select(l => accounts[l.AccountCode].Id).ToArray() });
            insert.Parameters.Add(new() { Value = draft.Lines.Select(l => l.Direction == Direction.Debit ? "D" : "C").ToArray() });
            insert.Parameters.Add(new() { Value = draft.Lines.Select(l => l.AmountMinor).ToArray() });

            batch.BatchCommands.Add(update);
            batch.BatchCommands.Add(insert);
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var lines = draft.Lines.Select((l, i) => new EntryLineView(i + 1, l.AccountCode, l.Direction, l.AmountMinor)).ToArray();
        return Result.Success(new PostOutcome(
            new EntryView(entryId, key, draft.Description, draft.ValueDate, draft.Currency, draft.ExternalRef, postedAt, lines),
            Replayed: false));
    }

    private static async Task<Result<PostOutcome>> ReplayAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string key, byte[] hash, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("SELECT id, request_hash FROM ledger.journal_entries WHERE idempotency_key = $1", connection, tx);
        cmd.Parameters.Add(new() { Value = key });
        Guid id;
        byte[] storedHash;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                // ON CONFLICT saw a row this snapshot cannot see; only possible under SSI and retried as 40001.
                throw new InvalidOperationException("Idempotency key conflicted but no entry is visible.");
            }

            id = reader.GetGuid(0);
            storedHash = (byte[])reader[1];
        }

        if (!storedHash.AsSpan().SequenceEqual(hash))
        {
            return LedgerError.Unprocessable(
                "idempotency.key_reused", "This Idempotency-Key was already used with a different request payload.");
        }

        var entry = await LoadAsync(connection, tx, id, cancellationToken);
        return Result.Success(new PostOutcome(entry!, Replayed: true));
    }

    public async Task<EntryView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, null, id, cancellationToken);
    }

    private static async Task<EntryView?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT e.id, e.idempotency_key, e.description, e.value_date, e.currency, e.external_ref, e.posted_at,
                   p.line_no, a.code, p.direction, p.amount_minor
              FROM ledger.journal_entries e
              JOIN ledger.postings p ON p.entry_id = e.id
              JOIN ledger.accounts a ON a.id = p.account_id
             WHERE e.id = $1
             ORDER BY p.line_no
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        EntryView? entry = null;
        var lines = new List<EntryLineView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entry ??= new EntryView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), lines);
            lines.Add(new EntryLineView(reader.GetInt16(7), reader.GetString(8), reader.GetString(9) == "D" ? Direction.Debit : Direction.Credit, reader.GetInt64(10)));
        }

        return entry;
    }

    private sealed record LockedAccount(long Id, string Code, AccountType Type, string Currency, bool PreventOverdraft, long BalanceMinor);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retrying posting after SQLSTATE {SqlState} (attempt {Attempt})")]
    private partial void LogRetry(string sqlState, int attempt);
}
