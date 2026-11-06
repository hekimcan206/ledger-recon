using System.Security.Cryptography;
using System.Text;
using LedgerRecon.Api.Infrastructure;
using LedgerRecon.Core;
using LedgerRecon.Core.Reconciliation;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace LedgerRecon.Api.Reconciliation;

internal sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    /// <summary>Default clearing/nostro account per source; a run may name another account explicitly.</summary>
    public Dictionary<ReconSource, string> Accounts { get; init; } = new()
    {
        [ReconSource.Card] = "CARD_CLEARING",
        [ReconSource.Eft] = "CB_SETTLEMENT_TRY",
        [ReconSource.Correspondent] = "NOSTRO_USD",
    };

    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;
}

internal sealed record ReconRunView(
    Guid Id,
    ReconSource Source,
    DateOnly BusinessDate,
    string AccountCode,
    string? FileName,
    string FileSha256,
    int RecordCount,
    int LedgerItemCount,
    int MatchedCount,
    int DifferenceCount,
    int PendingCount,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, int> Kinds);

internal sealed record ReconRunOutcome(ReconRunView Run, bool Replayed);

/// <summary>
/// Runs end-of-day matching of one external file against the open postings of a clearing account and stores the
/// difference report. Concurrency design (docs/adr/0003):
/// <list type="bullet">
/// <item>An advisory transaction lock keyed on the account serialises runs for the same account, without touching
/// the account row, so postings to that clearing account keep flowing while a run is in progress.</item>
/// <item>The run reads committed postings only (READ COMMITTED inside the lock): a posting that commits during the
/// run is simply picked up by the next run, it can never be half-seen.</item>
/// <item>Consumed postings get a primary-key row, so even without the lock a posting could not be matched twice.</item>
/// <item>The same file for the same source and business date (by SHA-256) returns the existing run.</item>
/// </list>
/// </summary>
internal sealed class ReconciliationService(NpgsqlDataSource dataSource, IOptions<ReconciliationOptions> options, LedgerMetrics metrics)
{
    private const int AdvisoryLockNamespace = 0x4C52; // "LR"
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<Result<ReconRunOutcome>> RunAsync(
        ReconSource source, DateOnly businessDate, string? accountCode, string? fileName, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Length > options.Value.MaxFileBytes)
        {
            return LedgerError.Validation("file.size_invalid", $"The file must be between 1 byte and {options.Value.MaxFileBytes} bytes.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return LedgerError.Unprocessable("file.encoding_invalid", "The file is not valid UTF-8.");
        }

        var parsed = ReconFiles.Parse(source, text);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        accountCode ??= options.Value.Accounts.GetValueOrDefault(source);
        var sha = SHA256.HashData(content);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        var account = await FindAccountAsync(connection, tx, accountCode, cancellationToken);
        if (account is null)
        {
            return LedgerError.Unprocessable("recon.account_unknown", $"Clearing account '{accountCode}' does not exist.");
        }

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1, $2)", connection, tx))
        {
            lockCmd.Parameters.Add(new() { Value = AdvisoryLockNamespace });
            // The two-key form takes int4; account ids beyond 2^31 fold onto the same lock, which only over-serialises.
            lockCmd.Parameters.Add(new() { Value = unchecked((int)account.Value.Id) });
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = await FindRunAsync(connection, tx, source, businessDate, sha, cancellationToken);
        if (existing is not null)
        {
            return Result.Success(new ReconRunOutcome(existing, Replayed: true));
        }

        var ledger = await LoadOpenItemsAsync(connection, tx, account.Value.Id, businessDate, cancellationToken);
        var result = ReconciliationEngine.Reconcile(parsed.Value, ledger, MatchPolicy.For(source), businessDate);

        var runId = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ledger.recon_runs (id, source, business_date, account_id, file_name, file_sha256,
                                           record_count, ledger_item_count, matched_count, difference_count, pending_count)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """, connection, tx))
        {
            insert.Parameters.Add(new() { Value = runId });
            insert.Parameters.Add(new() { Value = SourceName(source) });
            insert.Parameters.Add(new() { Value = businessDate });
            insert.Parameters.Add(new() { Value = account.Value.Id });
            insert.Parameters.Add(new() { Value = (object?)fileName ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            insert.Parameters.Add(new() { Value = sha });
            insert.Parameters.Add(new() { Value = parsed.Value.Records.Count + parsed.Value.Issues.Count });
            insert.Parameters.Add(new() { Value = ledger.Count });
            insert.Parameters.Add(new() { Value = result.Items.Count(i => i.Kind.IsMatch()) });
            insert.Parameters.Add(new() { Value = result.Differences.Count() });
            insert.Parameters.Add(new() { Value = result.Count(DifferenceKind.Pending) });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteItemsAsync(connection, runId, result, cancellationToken);

        var consumed = result.Items.Where(i => i.Ledger is not null && i.Kind.ConsumesLedgerItem()).ToArray();
        await using (var consume = new NpgsqlCommand("""
            INSERT INTO ledger.recon_consumed_postings (posting_id, run_id, kind)
            SELECT p, $1, k FROM unnest($2::bigint[], $3::text[]) AS c(p, k)
            """, connection, tx))
        {
            consume.Parameters.Add(new() { Value = runId });
            consume.Parameters.Add(new() { Value = consumed.Select(i => i.Ledger!.PostingId).ToArray() });
            consume.Parameters.Add(new() { Value = consumed.Select(i => i.Kind.ToString()).ToArray() });
            await consume.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        foreach (var (kind, count) in result.Summary().Where(k => k.Value > 0))
        {
            metrics.ReconItems(SourceName(source), kind.ToString(), count);
        }

        var run = await FindRunAsync(connection, null, source, businessDate, sha, cancellationToken);
        return Result.Success(new ReconRunOutcome(run!, Replayed: false));
    }

    public async Task<ReconRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await QueryRunAsync(connection, null, "r.id = $1", [id], cancellationToken);
    }

    public async Task<IReadOnlyList<ReconRunView>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM ledger.recon_runs ORDER BY created_at DESC, id DESC LIMIT $1", connection))
        {
            cmd.Parameters.Add(new() { Value = limit });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var runs = new List<ReconRunView>();
        foreach (var id in ids)
        {
            runs.Add((await QueryRunAsync(connection, null, "r.id = $1", [id], cancellationToken))!);
        }

        return runs;
    }

    public async Task<IReadOnlyList<ReconItem>> GetItemsAsync(Guid runId, bool differencesOnly, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT kind, line_number, file_reference, file_date, file_amount_minor, posting_id, entry_id,
                   ledger_reference, ledger_value_date, ledger_amount_minor, difference_minor, currency, detail
              FROM ledger.recon_items WHERE run_id = $1 ORDER BY id
            """, connection);
        cmd.Parameters.Add(new() { Value = runId });
        var items = new List<ReconItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = Enum.Parse<DifferenceKind>(reader.GetString(0));
            if (differencesOnly && !kind.IsDifference())
            {
                continue;
            }

            int? line = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            var currency = reader.IsDBNull(11) ? string.Empty : reader.GetString(11);
            var record = reader.IsDBNull(3)
                ? null
                : new StatementRecord(line ?? 0, reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateOnly>(3), reader.GetInt64(4), currency);
            var ledger = reader.IsDBNull(5)
                ? null
                : new LedgerItem(reader.GetInt64(5), reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetFieldValue<DateOnly>(8), reader.GetInt64(9), currency);
            items.Add(new ReconItem(kind, record, ledger, reader.GetInt64(10), reader.GetString(12), record is null ? line : null));
        }

        return items;
    }

    private static async Task<(long Id, string Currency)?> FindAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string? code, CancellationToken cancellationToken)
    {
        if (code is null)
        {
            return null;
        }

        await using var cmd = new NpgsqlCommand("SELECT id, currency FROM ledger.accounts WHERE code = $1", connection, tx);
        cmd.Parameters.Add(new() { Value = code });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    /// <summary>Postings on the account up to the business date that no earlier run has consumed.</summary>
    private static async Task<IReadOnlyList<LedgerItem>> LoadOpenItemsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, long accountId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT p.id, p.entry_id, e.external_ref, e.value_date,
                   CASE p.direction WHEN 'D' THEN p.amount_minor ELSE -p.amount_minor END, p.currency
              FROM ledger.postings p
              JOIN ledger.journal_entries e ON e.id = p.entry_id
             WHERE p.account_id = $1
               AND e.value_date <= $2
               AND NOT EXISTS (SELECT 1 FROM ledger.recon_consumed_postings c WHERE c.posting_id = p.id)
             ORDER BY p.id
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = accountId });
        cmd.Parameters.Add(new() { Value = businessDate });
        var items = new List<LedgerItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LedgerItem(
                reader.GetInt64(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateOnly>(3), reader.GetInt64(4), reader.GetString(5)));
        }

        return items;
    }

    /// <summary>Binary COPY: a report can have thousands of lines; one round trip writes them all.</summary>
    private static async Task WriteItemsAsync(NpgsqlConnection connection, Guid runId, ReconResult result, CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync("""
            COPY ledger.recon_items (run_id, kind, line_number, file_reference, file_date, file_amount_minor, posting_id, entry_id,
                                     ledger_reference, ledger_value_date, ledger_amount_minor, difference_minor, currency, detail)
            FROM STDIN (FORMAT BINARY)
            """, cancellationToken);
        foreach (var item in result.Items)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(runId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(item.Kind.ToString(), NpgsqlDbType.Text, cancellationToken);
            await WriteNullable(importer, item.LineNumber, NpgsqlDbType.Integer, cancellationToken);
            await WriteNullable(importer, item.Record?.Reference, NpgsqlDbType.Text, cancellationToken);
            await WriteNullable(importer, item.Record?.Date, NpgsqlDbType.Date, cancellationToken);
            await WriteNullable(importer, item.Record?.AmountMinor, NpgsqlDbType.Bigint, cancellationToken);
            await WriteNullable(importer, item.Ledger?.PostingId, NpgsqlDbType.Bigint, cancellationToken);
            await WriteNullable(importer, item.Ledger?.EntryId, NpgsqlDbType.Uuid, cancellationToken);
            await WriteNullable(importer, item.Ledger?.Reference, NpgsqlDbType.Text, cancellationToken);
            await WriteNullable(importer, item.Ledger?.ValueDate, NpgsqlDbType.Date, cancellationToken);
            await WriteNullable(importer, item.Ledger?.AmountMinor, NpgsqlDbType.Bigint, cancellationToken);
            await importer.WriteAsync(item.DifferenceMinor, NpgsqlDbType.Bigint, cancellationToken);
            await WriteNullable(importer, item.Record?.Currency ?? item.Ledger?.Currency, NpgsqlDbType.Char, cancellationToken);
            await importer.WriteAsync(item.Detail, NpgsqlDbType.Text, cancellationToken);
        }

        await importer.CompleteAsync(cancellationToken);
    }

    private static async Task WriteNullable(NpgsqlBinaryImporter importer, object? value, NpgsqlDbType type, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken);
        }
        else
        {
            await importer.WriteAsync(value, type, cancellationToken);
        }
    }

    private static async Task<ReconRunView?> FindRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, ReconSource source, DateOnly businessDate, byte[] sha, CancellationToken cancellationToken) =>
        await QueryRunAsync(connection, tx, "r.source = $1 AND r.business_date = $2 AND r.file_sha256 = $3", [SourceName(source), businessDate, sha], cancellationToken);

    private static async Task<ReconRunView?> QueryRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string where, object[] args, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT r.id, r.source, r.business_date, a.code, r.file_name, r.file_sha256, r.record_count, r.ledger_item_count,
                   r.matched_count, r.difference_count, r.pending_count, r.created_at,
                   coalesce((SELECT jsonb_object_agg(k.kind, k.n) FROM (SELECT kind, count(*) AS n FROM ledger.recon_items i
                              WHERE i.run_id = r.id GROUP BY kind) k), jsonb_build_object())::text
              FROM ledger.recon_runs r JOIN ledger.accounts a ON a.id = r.account_id
             WHERE {where}
            """, connection, tx);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new() { Value = arg });
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var kinds = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(12)) ?? [];
        return new ReconRunView(
            reader.GetGuid(0), Enum.Parse<ReconSource>(reader.GetString(1), ignoreCase: true), reader.GetFieldValue<DateOnly>(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), Convert.ToHexStringLower((byte[])reader[5]), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetFieldValue<DateTimeOffset>(11), kinds);
    }

    internal static string SourceName(ReconSource source) => source.ToString().ToLowerInvariant();
}
