using System.Net;
using System.Net.Http.Json;
using LedgerRecon.IntegrationTests.Infrastructure;

namespace LedgerRecon.IntegrationTests;

public sealed class PostingApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(db.AppConnectionString);
    private readonly string _p = Infrastructure.Ledger.NewPrefix();
    private HttpClient _client = null!;

    private string Vault => _p + "VAULT";

    private string Wallet => _p + "WALLET";

    public async ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _client.OpenAsync(Vault, "asset");
        await _client.OpenAsync(Wallet, "liability", preventOverdraft: true);
        await _client.OpenAsync(_p + "USD_CASH", "asset", "USD");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Posting_returns_201_with_the_entry_and_moves_both_balances()
    {
        var response = await _client.SendAsync(Infrastructure.Ledger.PostEntry("k-" + _p, Infrastructure.Ledger.Transfer(Vault, Wallet, 125.40m, externalRef: "RRN-1")));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("Idempotent-Replayed").Should().Equal("false");
        var entry = await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.EntryDto>();
        entry!.ExternalRef.Should().Be("RRN-1");
        response.Headers.Location!.ToString().Should().Be($"/api/v1/journal-entries/{entry.Id}");

        (await _client.BalanceAsync(Vault)).Should().Be(125.40m);
        (await _client.BalanceAsync(Wallet)).Should().Be(-125.40m);
        (await _client.GetFromJsonAsync<Infrastructure.Ledger.AccountDto>($"/api/v1/accounts/{Wallet}"))!.NormalBalance.Should().Be(125.40m);
    }

    [Fact]
    public async Task Replay_with_same_key_and_payload_returns_the_original_entry_without_writing()
    {
        var key = "replay-" + _p;
        var first = await _client.SendAsync(Infrastructure.Ledger.PostEntry(key, Infrastructure.Ledger.Transfer(Vault, Wallet, 10m)));
        // Same payload written differently: lower-case codes and "10.00" instead of "10".
        var second = await _client.SendAsync(Infrastructure.Ledger.PostEntry(key, Infrastructure.Ledger.Transfer(Vault.ToLowerInvariant(), Wallet.ToLowerInvariant(), 10.00m)));

        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        (await second.Content.ReadFromJsonAsync<Infrastructure.Ledger.EntryDto>())!.Id
            .Should().Be((await first.Content.ReadFromJsonAsync<Infrastructure.Ledger.EntryDto>())!.Id);
        (await _client.BalanceAsync(Vault)).Should().Be(10m);
    }

    [Fact]
    public async Task Same_key_with_a_different_payload_is_rejected_with_422()
    {
        var key = "reuse-" + _p;
        await _client.SendAsync(Infrastructure.Ledger.PostEntry(key, Infrastructure.Ledger.Transfer(Vault, Wallet, 10m)));
        var response = await _client.SendAsync(Infrastructure.Ledger.PostEntry(key, Infrastructure.Ledger.Transfer(Vault, Wallet, 11m)));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be("idempotency.key_reused");
        (await _client.BalanceAsync(Vault)).Should().Be(10m);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_a_400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/journal-entries", Infrastructure.Ledger.Transfer(Vault, Wallet, 1m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be("idempotency.key_missing");
    }

    [Fact]
    public async Task Unbalanced_entry_is_a_400_and_writes_nothing()
    {
        var body = new
        {
            description = "bad",
            currency = "TRY",
            lines = new object[] { new { accountCode = Vault, direction = "debit", amount = 10m }, new { accountCode = Wallet, direction = "credit", amount = 9.99m } },
        };
        var response = await _client.SendAsync(Infrastructure.Ledger.PostEntry("unbalanced-" + _p, body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be("entry.unbalanced");
        (await _client.BalanceAsync(Vault)).Should().Be(0m);
    }

    [Theory]
    [InlineData("UNKNOWN_ACCOUNT", "entry.unknown_account")]
    [InlineData("USD_CASH", "entry.currency_mismatch")]
    public async Task Rules_that_need_the_database_are_422(string otherAccountSuffix, string code)
    {
        var response = await _client.SendAsync(Infrastructure.Ledger.PostEntry("rule-" + _p, Infrastructure.Ledger.Transfer(_p + otherAccountSuffix, Wallet, 5m)));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be(code);
    }

    [Fact]
    public async Task Rejected_request_releases_its_key_so_the_same_key_succeeds_once_funds_arrive()
    {
        var key = "withdraw-" + _p;
        var withdraw = Infrastructure.Ledger.Transfer(Wallet, Vault, 50m);

        var rejected = await _client.SendAsync(Infrastructure.Ledger.PostEntry(key, withdraw));
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await rejected.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be("account.insufficient_funds");

        await _client.SendAsync(Infrastructure.Ledger.PostEntry("deposit-" + _p, Infrastructure.Ledger.Transfer(Vault, Wallet, 80m)));
        var retried = await _client.SendAsync(Infrastructure.Ledger.PostEntry(key, withdraw));

        retried.StatusCode.Should().Be(HttpStatusCode.Created);
        retried.Headers.GetValues("Idempotent-Replayed").Should().Equal("false");
        (await _client.BalanceAsync(Wallet)).Should().Be(-30m);
    }

    [Fact]
    public async Task Entry_can_be_read_back_with_its_postings()
    {
        var created = await (await _client.SendAsync(Infrastructure.Ledger.PostEntry("read-" + _p, Infrastructure.Ledger.Transfer(Vault, Wallet, 7.25m))))
            .Content.ReadFromJsonAsync<Infrastructure.Ledger.EntryDto>();

        var json = await _client.GetStringAsync($"/api/v1/journal-entries/{created!.Id}");
        json.Should().Contain($"\"accountCode\":\"{Vault}\"").And.Contain("\"direction\":\"debit\"").And.Contain("\"amount\":7.25");
        (await _client.GetAsync($"/api/v1/journal-entries/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Account_creation_is_idempotent_for_identical_definitions_only()
    {
        var code = _p + "EXTRA";
        (await _client.PostAsJsonAsync("/api/v1/accounts", new { code, name = "x", type = "asset", currency = "TRY" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/v1/accounts", new { code, name = "x", type = "asset", currency = "TRY" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsJsonAsync("/api/v1/accounts", new { code, name = "x", type = "expense", currency = "TRY" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync("/api/v1/accounts", new { code = "a b", name = "x", type = "asset", currency = "TRY" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<Infrastructure.Ledger.AccountDto[]>($"/api/v1/accounts?prefix={_p}"))!.Should().HaveCount(4);
    }

    [Fact]
    public async Task Trial_balance_nets_to_zero_across_everything_every_test_has_written()
    {
        await _client.SendAsync(Infrastructure.Ledger.PostEntry("tb-" + _p, Infrastructure.Ledger.Transfer(Vault, Wallet, 1_000m)));
        var json = await _client.GetStringAsync("/api/v1/ledger/trial-balance");
        json.Should().Contain("\"balanced\":true").And.Contain("\"driftingAccounts\":0");
    }

    [Fact]
    public async Task Health_endpoints_report_live_and_ready()
    {
        (await _client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetStringAsync("/health/ready")).Should().Contain("\"database\":{\"status\":\"Healthy\"");
        (await _client.GetAsync("/openapi/v1.json")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
