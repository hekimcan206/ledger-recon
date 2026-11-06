using System.Net;
using System.Net.Http.Json;
using LedgerRecon.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LedgerRecon.IntegrationTests;

/// <summary>
/// Races against real PostgreSQL, run under both isolation strategies. Requests are released together through a
/// barrier so they genuinely overlap inside the database rather than trickling in one by one.
/// </summary>
public sealed class ConcurrencyTests(PostgresFixture db)
{
    public static TheoryData<string> Strategies() => ["RowLocks", "Serializable"];

    /// <summary>
    /// Opens the pool's connections up front, so the race is about rows and locks in PostgreSQL, not about 50 backends
    /// being forked at the same instant.
    /// </summary>
    private static async Task WarmUpAsync(ApiFactory factory, int connections)
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var opened = await Task.WhenAll(Enumerable.Range(0, connections).Select(_ => dataSource.OpenConnectionAsync().AsTask()));
        foreach (var connection in opened)
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage[]> FireTogether(ApiFactory factory, HttpClient client, int count, Func<int, HttpRequestMessage> request)
    {
        await WarmUpAsync(factory, count);
        using var start = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var message = request(i);
            await start.WaitAsync();
            return await client.SendAsync(message);
        }).ToArray();
        start.Release(count);
        return await Task.WhenAll(tasks);
    }

    private async Task<long> ScalarAsync(string sql, params object[] args)
    {
        await using var dataSource = PostgresFixture.CreateDataSource(db.AppConnectionString);
        await using var cmd = dataSource.CreateCommand(sql);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Fifty_parallel_identical_requests_create_exactly_one_entry(string strategy)
    {
        await using var factory = new ApiFactory(db.AppConnectionString, strategy);
        using var client = factory.CreateClient();
        var p = Infrastructure.Ledger.NewPrefix();
        await client.OpenAsync(p + "VAULT", "asset");
        await client.OpenAsync(p + "WALLET", "liability");
        var key = "same-" + p;

        var responses = await FireTogether(factory, client, 50, _ => Infrastructure.Ledger.PostEntry(key, Infrastructure.Ledger.Transfer(p + "VAULT", p + "WALLET", 99.99m)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        responses.Count(r => r.Headers.GetValues("Idempotent-Replayed").Single() == "false").Should().Be(1, "exactly one request may do the work");
        (await ScalarAsync("SELECT count(*) FROM ledger.journal_entries WHERE idempotency_key = $1", key)).Should().Be(1);
        (await ScalarAsync("SELECT count(*) FROM ledger.postings p JOIN ledger.journal_entries e ON e.id = p.entry_id WHERE e.idempotency_key = $1", key)).Should().Be(2);
        (await client.BalanceAsync(p + "VAULT")).Should().Be(99.99m);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Parallel_withdrawals_never_overdraw_a_protected_account(string strategy)
    {
        await using var factory = new ApiFactory(db.AppConnectionString, strategy);
        using var client = factory.CreateClient();
        var p = Infrastructure.Ledger.NewPrefix();
        await client.OpenAsync(p + "VAULT", "asset");
        await client.OpenAsync(p + "WALLET", "liability", preventOverdraft: true);
        (await client.SendAsync(Infrastructure.Ledger.PostEntry("fund-" + p, Infrastructure.Ledger.Transfer(p + "VAULT", p + "WALLET", 2_000m)))).EnsureSuccessStatusCode();

        // 50 distinct withdrawals of 100 against a balance of 2 000: exactly 20 can succeed.
        var responses = await FireTogether(factory, client, 50, i => Infrastructure.Ledger.PostEntry($"w{i}-{p}", Infrastructure.Ledger.Transfer(p + "WALLET", p + "VAULT", 100m)));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        var busy = responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        if (strategy == "RowLocks")
        {
            // Lock waits, never failures: every request gets a definite answer.
            (created, rejected, busy).Should().Be((20, 30, 0));
        }
        else
        {
            // SSI on one hot row: some requests can exhaust their retries and get a retryable 503 (ledger.busy).
            // Money is still never created or overdrawn; see docs/benchmarks.md for how often this happens.
            created.Should().BeLessThanOrEqualTo(20);
            (created + rejected + busy).Should().Be(50);
        }

        (await client.BalanceAsync(p + "WALLET")).Should().Be(-2_000m + (created * 100m));
        (await client.BalanceAsync(p + "VAULT")).Should().Be(2_000m - (created * 100m));
        (await client.GetFromJsonAsync<Infrastructure.Ledger.AccountDto>($"/api/v1/accounts/{p}WALLET"))!.NormalBalance.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Opposing_transfers_between_two_accounts_complete_without_deadlocks(string strategy)
    {
        await using var factory = new ApiFactory(db.AppConnectionString, strategy);
        using var client = factory.CreateClient();
        var p = Infrastructure.Ledger.NewPrefix();
        await client.OpenAsync(p + "A", "asset");
        await client.OpenAsync(p + "B", "asset");

        // Even requests debit B and credit A, odd ones the reverse: the classic lock-ordering deadlock if locks were taken in
        // request order. Ordered FOR UPDATE (RowLocks) or SSI retries (Serializable) must absorb it.
        var responses = await FireTogether(factory, client, 40, i => Infrastructure.Ledger.PostEntry(
            $"x{i}-{p}", i % 2 == 0 ? Infrastructure.Ledger.Transfer(p + "B", p + "A", 3m) : Infrastructure.Ledger.Transfer(p + "A", p + "B", 5m)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || (strategy == "Serializable" && r.StatusCode == HttpStatusCode.ServiceUnavailable));
        responses.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError);

        // Even: B +3, A -3. Odd: A +5, B -5 (debit-positive). Only committed requests count.
        var evenOk = responses.Where((r, i) => i % 2 == 0 && r.StatusCode == HttpStatusCode.Created).Count();
        var oddOk = responses.Where((r, i) => i % 2 == 1 && r.StatusCode == HttpStatusCode.Created).Count();
        (await client.BalanceAsync(p + "A")).Should().Be((oddOk * 5m) - (evenOk * 3m));
        (await client.BalanceAsync(p + "B")).Should().Be((evenOk * 3m) - (oddOk * 5m));
        if (strategy == "RowLocks")
        {
            (evenOk, oddOk).Should().Be((20, 20));
        }
    }
}
