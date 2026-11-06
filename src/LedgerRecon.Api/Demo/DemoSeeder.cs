using System.Text;
using LedgerRecon.Api.Ledger;
using LedgerRecon.Api.Reconciliation;
using LedgerRecon.Synthetic;

namespace LedgerRecon.Api.Demo;

internal sealed class DemoOptions
{
    public const string Section = "Demo";

    public int Seed { get; set; } = 20261105;

    public DateOnly BusinessDate { get; set; } = new(2026, 11, 5);

    public int TransactionsPerSource { get; set; } = 60;
}

/// <summary>
/// <c>--seed-demo</c>: opens the demo chart of accounts, posts one synthetic business day through the same posting
/// service the API uses, then reconciles the three generated files. Every write is idempotent (fixed idempotency
/// keys, identical account definitions, file-hash run dedup), so running it again changes nothing, which is
/// itself a small demonstration of exactly-once processing.
/// </summary>
internal sealed partial class DemoSeeder(
    AccountStore accounts,
    PostingService postings,
    ReconciliationService reconciliation,
    Microsoft.Extensions.Options.IOptions<DemoOptions> options,
    ILogger<DemoSeeder> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var scenario = ScenarioGenerator.Generate(o.Seed, o.BusinessDate, o.TransactionsPerSource);

        foreach (var spec in scenario.Chart.Accounts())
        {
            var created = await accounts.CreateAsync(spec.Code, spec.Name, spec.Type, spec.Currency, spec.PreventOverdraft, cancellationToken);
            if (!created.IsSuccess)
            {
                throw new InvalidOperationException($"Could not open demo account {spec.Code}: {created.Error.Message}");
            }
        }

        int posted = 0, replayed = 0;
        foreach (var entry in scenario.AllEntries)
        {
            var result = await postings.PostAsync(entry.IdempotencyKey, entry.Draft, cancellationToken);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Demo entry {entry.IdempotencyKey} was rejected: {result.Error.Message}");
            }

            if (result.Value.Replayed)
            {
                replayed++;
            }
            else
            {
                posted++;
            }
        }

        LogPosted(posted, replayed);

        foreach (var source in scenario.Sources)
        {
            var run = await reconciliation.RunAsync(
                source.Source, scenario.BusinessDate, null, source.FileName, Encoding.UTF8.GetBytes(source.FileContent), cancellationToken);
            if (!run.IsSuccess)
            {
                throw new InvalidOperationException($"Demo reconciliation of {source.FileName} failed: {run.Error.Message}");
            }

            var r = run.Value.Run;
            LogRun(source.FileName, r.Id, r.RecordCount, r.MatchedCount, r.DifferenceCount, r.PendingCount, run.Value.Replayed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo ledger: {Posted} entries posted, {Replayed} replayed (already present)")]
    private partial void LogPosted(int posted, int replayed);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reconciled {File}: run {RunId}, {Records} records, {Matched} matched, {Differences} differences, {Pending} pending (replayed: {Replayed})")]
    private partial void LogRun(string file, Guid runId, int records, int matched, int differences, int pending, bool replayed);
}
