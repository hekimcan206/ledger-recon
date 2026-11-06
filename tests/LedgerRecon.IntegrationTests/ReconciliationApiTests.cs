using System.Net;
using System.Net.Http.Json;
using System.Text;
using LedgerRecon.Api.Demo;
using LedgerRecon.Api.Ledger;
using LedgerRecon.Core.Reconciliation;
using LedgerRecon.IntegrationTests.Infrastructure;
using LedgerRecon.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerRecon.IntegrationTests;

public sealed class ReconciliationApiTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 5);
    private readonly ApiFactory _factory = new(db.AppConnectionString);
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed record RunDto(Guid Id, string Source, string AccountCode, int RecordCount, int LedgerItemCount, int MatchedCount, int DifferenceCount, int PendingCount, Dictionary<string, int> Kinds);

    private sealed record ItemDto(string Kind, int? Line, string? FileReference, string? LedgerReference, decimal Difference);

    /// <summary>Opens a prefixed copy of the demo chart and posts a generated day through the real posting service.</summary>
    private async Task<Scenario> LoadScenarioAsync(int seed, int transactions = 40, DefectRates? rates = null)
    {
        var prefix = Infrastructure.Ledger.NewPrefix();
        var scenario = ScenarioGenerator.Generate(seed, Day, transactions, rates, keyPrefix: prefix, chart: new DemoChart(prefix));
        var accounts = _factory.Services.GetRequiredService<AccountStore>();
        var postings = _factory.Services.GetRequiredService<PostingService>();
        foreach (var a in scenario.Chart.Accounts())
        {
            (await accounts.CreateAsync(a.Code, a.Name, a.Type, a.Currency, a.PreventOverdraft, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        foreach (var e in scenario.AllEntries)
        {
            (await postings.PostAsync(e.IdempotencyKey, e.Draft, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        return scenario;
    }

    private Task<HttpResponseMessage> UploadAsync(SourceScenario source, string content, DateOnly? day = null) =>
        _client.PostAsync(
            $"/api/v1/reconciliation/runs?source={source.Source}&businessDate={day ?? Day:yyyy-MM-dd}&account={source.ClearingAccount}&fileName={source.FileName}",
            new StringContent(content, Encoding.UTF8, "text/plain"));

    [Fact]
    public async Task Each_source_reports_exactly_the_injected_differences_end_to_end()
    {
        var scenario = await LoadScenarioAsync(seed: 1105);

        foreach (var source in scenario.Sources)
        {
            var response = await UploadAsync(source, source.FileContent);
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var run = (await response.Content.ReadFromJsonAsync<RunDto>())!;

            var items = (await _client.GetFromJsonAsync<ItemDto[]>($"/api/v1/reconciliation/runs/{run.Id}/items"))!;
            var actual = items.Select(i => $"{i.Kind}|{i.LedgerReference ?? i.FileReference}").Order(StringComparer.Ordinal);
            var expected = source.Expected.Select(e => $"{e.Kind}|{e.Reference}").Order(StringComparer.Ordinal);
            actual.Should().Equal(expected, $"source {source.Source}");

            run.LedgerItemCount.Should().Be(source.LedgerItems.Count);
            run.DifferenceCount.Should().Be(source.Expected.Count(e => e.Kind.IsDifference()));
        }
    }

    [Fact]
    public async Task Same_file_uploaded_again_returns_the_existing_run()
    {
        var scenario = await LoadScenarioAsync(seed: 7);
        var card = scenario.Sources.Single(s => s.Source == ReconSource.Card);

        var first = await UploadAsync(card, card.FileContent);
        var second = await UploadAsync(card, card.FileContent);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<RunDto>())!.Id.Should().Be((await first.Content.ReadFromJsonAsync<RunDto>())!.Id);
    }

    [Fact]
    public async Task Concurrent_uploads_of_the_same_file_create_one_run()
    {
        var scenario = await LoadScenarioAsync(seed: 8);
        var eft = scenario.Sources.Single(s => s.Source == ReconSource.Eft);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => UploadAsync(eft, eft.FileContent)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(5);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<RunDto>())!.Id));
        ids.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Pending_items_are_offered_again_and_matched_by_the_next_days_file()
    {
        var scenario = await LoadScenarioAsync(seed: 9, transactions: 10, rates: DefectRates.None);
        var card = scenario.Sources.Single(s => s.Source == ReconSource.Card);
        var lines = card.FileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Day 1: the acquirer reports everything except the most recent transaction (inside the 2-day window).
        var today = lines.Skip(1).OrderByDescending(l => l.Split(',')[1], StringComparer.Ordinal).First();
        var day1 = string.Join('\n', lines.Where(l => l != today)) + "\n";
        var day2 = lines[0] + "\n" + today + "\n";

        var run1 = (await (await UploadAsync(card, day1)).Content.ReadFromJsonAsync<RunDto>())!;
        run1.PendingCount.Should().Be(1);
        run1.DifferenceCount.Should().Be(0);

        var run2 = (await (await UploadAsync(card, day2, Day.AddDays(1))).Content.ReadFromJsonAsync<RunDto>())!;
        run2.LedgerItemCount.Should().Be(1, "only the carried-over posting is still open");
        run2.MatchedCount.Should().Be(1);
    }

    [Fact]
    public async Task Difference_report_is_available_as_csv()
    {
        var scenario = await LoadScenarioAsync(seed: 10);
        var nostro = scenario.Sources.Single(s => s.Source == ReconSource.Correspondent);
        var run = (await (await UploadAsync(nostro, nostro.FileContent)).Content.ReadFromJsonAsync<RunDto>())!;

        var response = await _client.GetAsync($"/api/v1/reconciliation/runs/{run.Id}/items?differencesOnly=true&format=csv");

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await response.Content.ReadAsStringAsync();
        csv.Should().StartWith(ReconFiles.ReportHeader);
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().Be(run.DifferenceCount + 1);
        csv.Should().NotContain("\nMatched,").And.NotContain("\nPending,");
    }

    [Theory]
    [InlineData("correspondent", ":20:X\n:60F:C261104USD100,00\n:61:2611051105C1,00NTRFREF1\n:62F:C261105USD100,00\n", "file.statement_does_not_balance")]
    [InlineData("card", "not,a,card,file\n", "file.header_invalid")]
    public async Task Malformed_files_are_rejected_with_422(string source, string content, string code)
    {
        var response = await _client.PostAsync(
            $"/api/v1/reconciliation/runs?source={source}&businessDate=2026-11-05", new StringContent(content, Encoding.UTF8, "text/plain"));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be(code);
    }

    [Theory]
    [InlineData("source=swift&businessDate=2026-11-05", "recon.source_invalid")]
    [InlineData("source=card", "recon.business_date_missing")]
    [InlineData("source=card&businessDate=2026-11-05&account=bad%20code", "recon.account_invalid")]
    public async Task Invalid_parameters_are_400(string query, string code)
    {
        var response = await _client.PostAsync($"/api/v1/reconciliation/runs?{query}", new StringContent("x"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<Infrastructure.Ledger.ProblemDto>())!.Code.Should().Be(code);
    }

    [Fact]
    public async Task Demo_seed_is_idempotent_when_run_twice()
    {
        var seeder = _factory.Services.GetRequiredService<DemoSeeder>();
        await seeder.RunAsync(CancellationToken.None);
        string[] demoAccounts = ["CARD_CLEARING", "CB_SETTLEMENT_TRY", "NOSTRO_USD"];
        var runs1 = (await _client.GetFromJsonAsync<RunDto[]>("/api/v1/reconciliation/runs?limit=200"))!.Where(r => demoAccounts.Contains(r.AccountCode)).ToArray();
        var balance1 = await _client.BalanceAsync("CARD_CLEARING");

        await seeder.RunAsync(CancellationToken.None);

        var runs2 = (await _client.GetFromJsonAsync<RunDto[]>("/api/v1/reconciliation/runs?limit=200"))!.Where(r => demoAccounts.Contains(r.AccountCode)).ToArray();
        runs2.Select(r => r.Id).Should().BeEquivalentTo(runs1.Select(r => r.Id));
        runs1.Should().HaveCount(3);
        (await _client.BalanceAsync("CARD_CLEARING")).Should().Be(balance1);
        runs1.Should().Contain(r => r.Source == "Card" && r.Kinds.ContainsKey("Unparseable"));
    }
}
