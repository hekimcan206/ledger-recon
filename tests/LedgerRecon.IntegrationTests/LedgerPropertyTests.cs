using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LedgerRecon.Api.Ledger;
using LedgerRecon.Core.Ledger;
using LedgerRecon.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerRecon.IntegrationTests;

/// <summary>
/// Ledger invariants over random workloads against real PostgreSQL. Each generated case is a batch of transfers
/// between a fresh set of accounts, with some requests repeated (same key, same payload), all submitted
/// concurrently. After the batch:
/// <list type="bullet">
/// <item>every committed entry balances (debits = credits) in the database;</item>
/// <item>the balances of the case's accounts sum to zero (money is neither created nor destroyed);</item>
/// <item>every stored balance equals the sum of its postings (no drift) and the expected net of distinct requests;</item>
/// <item>exactly one entry exists per distinct key (repeats were replays);</item>
/// <item>replaying the whole batch again changes nothing.</item>
/// </list>
/// </summary>
public sealed class LedgerPropertyTests(PostgresFixture db) : IAsyncLifetime
{
    private const int Accounts = 4;
    private ApiFactory _factory = null!;
    private PostingService _postings = null!;
    private AccountStore _accounts = null!;

    public sealed record Op(int From, int To, long AmountMinor, int ReplayOf);

    public static Arbitrary<Op[]> Batches() =>
        Gen.ListOf(
                from debitFrom in Gen.Choose(0, Accounts - 1)
                from offset in Gen.Choose(1, Accounts - 1)
                from amount in Gen.Choose(1, 500_000)
                from replay in Gen.Frequency((7, Gen.Constant(-1)), (3, Gen.Choose(0, 1_000)))
                select new Op(debitFrom, (debitFrom + offset) % Accounts, amount, replay))
            .Where(ops => ops.Count is >= 1 and <= 40)
            .Select(ops => ops.Select((op, i) => op.ReplayOf < 0 || i == 0 ? op with { ReplayOf = -1 } : op with { ReplayOf = op.ReplayOf % i }).ToArray())
            .ToArbitrary();

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(db.AppConnectionString);
        _postings = _factory.Services.GetRequiredService<PostingService>();
        _accounts = _factory.Services.GetRequiredService<AccountStore>();

        // Warm the pool so batches race on rows, not on backend start-up.
        var dataSource = _factory.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
        var opened = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => dataSource.OpenConnectionAsync().AsTask()));
        foreach (var connection in opened)
        {
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Property(Arbitrary = [typeof(LedgerPropertyTests)], MaxTest = 40)]
    public bool Concurrent_batches_with_replays_keep_the_books_balanced_and_exactly_once(Op[] ops) =>
        // Task.Run keeps the async work off any test synchronisation context while FsCheck drives a sync property.
        Task.Run(() => RunAsync(ops)).GetAwaiter().GetResult();

    private async Task<bool> RunAsync(Op[] ops)
    {
        var prefix = Infrastructure.Ledger.NewPrefix();
        var codes = Enumerable.Range(0, Accounts).Select(i => $"{prefix}ACC{i}").ToArray();
        foreach (var code in codes)
        {
            (await _accounts.CreateAsync(code, code, AccountType.Asset, "TRY", false, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        // A repeat carries the original's key and payload: it must be answered from the existing entry.
        var requests = ops.Select((op, i) =>
        {
            var index = i;
            while (ops[index].ReplayOf >= 0)
            {
                index = ops[index].ReplayOf;
            }

            var source = ops[index];
            var draft = JournalEntryDraft.Create(
                "property", new DateOnly(2026, 11, 5), "TRY", null,
                [new PostingLine(codes[source.To], Direction.Debit, source.AmountMinor), new PostingLine(codes[source.From], Direction.Credit, source.AmountMinor)]).Value;
            return (Key: $"{prefix}op{index}", Draft: draft);
        }).ToArray();

        var outcomes = await Task.WhenAll(requests.Select(r => _postings.PostAsync(r.Key, r.Draft, CancellationToken.None)));
        outcomes.Should().OnlyContain(o => o.IsSuccess);
        var distinctKeys = requests.Select(r => r.Key).Distinct().Count();
        outcomes.Count(o => !o.Value.Replayed).Should().Be(distinctKeys);

        var expected = new long[Accounts];
        foreach (var op in ops.Where(o => o.ReplayOf < 0))
        {
            expected[op.To] += op.AmountMinor;
            expected[op.From] -= op.AmountMinor;
        }

        var before = await SnapshotAsync(prefix);
        before.Balances.Should().Equal(expected);
        before.Balances.Sum().Should().Be(0);
        before.Entries.Should().Be(distinctKeys);
        before.UnbalancedEntries.Should().Be(0);
        before.DriftingAccounts.Should().Be(0);

        var replays = await Task.WhenAll(requests.Select(r => _postings.PostAsync(r.Key, r.Draft, CancellationToken.None)));
        replays.Should().OnlyContain(o => o.IsSuccess && o.Value.Replayed);
        (await SnapshotAsync(prefix)).Should().BeEquivalentTo(before);
        return true;
    }

    private sealed record Snapshot(long[] Balances, long Entries, long UnbalancedEntries, long DriftingAccounts);

    private async Task<Snapshot> SnapshotAsync(string prefix)
    {
        await using var dataSource = PostgresFixture.CreateDataSource(db.AppConnectionString);
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new("SELECT balance_minor FROM ledger.accounts WHERE starts_with(code, $1) ORDER BY code") { Parameters = { new() { Value = prefix } } });
        batch.BatchCommands.Add(new("SELECT count(*) FROM ledger.journal_entries WHERE starts_with(idempotency_key, $1)") { Parameters = { new() { Value = prefix } } });
        batch.BatchCommands.Add(new("""
            SELECT count(*) FROM (
              SELECT p.entry_id FROM ledger.postings p JOIN ledger.journal_entries e ON e.id = p.entry_id
               WHERE starts_with(e.idempotency_key, $1)
               GROUP BY p.entry_id
              HAVING sum(CASE p.direction WHEN 'D' THEN p.amount_minor ELSE -p.amount_minor END) <> 0) x
            """) { Parameters = { new() { Value = prefix } } });
        batch.BatchCommands.Add(new("SELECT count(*) FROM ledger.account_balance_drift WHERE starts_with(code, $1)") { Parameters = { new() { Value = prefix } } });

        await using var reader = await batch.ExecuteReaderAsync();
        var balances = new List<long>();
        while (await reader.ReadAsync())
        {
            balances.Add(reader.GetInt64(0));
        }

        var counts = new long[3];
        for (var i = 0; i < 3; i++)
        {
            await reader.NextResultAsync();
            await reader.ReadAsync();
            counts[i] = reader.GetInt64(0);
        }

        return new Snapshot([.. balances], counts[0], counts[1], counts[2]);
    }
}
