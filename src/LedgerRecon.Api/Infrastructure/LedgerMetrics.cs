using System.Diagnostics.Metrics;

namespace LedgerRecon.Api.Infrastructure;

/// <summary>Business metrics. Exported through OpenTelemetry next to the ASP.NET Core and Npgsql meters.</summary>
internal sealed class LedgerMetrics
{
    public const string MeterName = "LedgerRecon";


    private readonly Counter<long> _posted;
    private readonly Counter<long> _replayed;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _retries;
    private readonly Histogram<double> _postingDuration;
    private readonly Counter<long> _reconItems;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _posted = meter.CreateCounter<long>("ledger.entries.posted", description: "Journal entries committed.");
        _replayed = meter.CreateCounter<long>("ledger.entries.replayed", description: "Requests answered from an existing entry (same idempotency key and payload).");
        _rejected = meter.CreateCounter<long>("ledger.entries.rejected", description: "Posting requests rejected, by error code.");
        _retries = meter.CreateCounter<long>("ledger.serialization.retries", description: "Transactions retried after a serialization failure or deadlock.");
        _postingDuration = meter.CreateHistogram<double>("ledger.posting.duration", unit: "ms", description: "Time to post one entry, including retries.");
        _reconItems = meter.CreateCounter<long>("recon.items", description: "Reconciliation report items, by source and kind.");
    }

    public void Posted(double elapsedMs)
    {
        _posted.Add(1);
        _postingDuration.Record(elapsedMs, new KeyValuePair<string, object?>("outcome", "posted"));
    }

    public void Replayed(double elapsedMs)
    {
        _replayed.Add(1);
        _postingDuration.Record(elapsedMs, new KeyValuePair<string, object?>("outcome", "replayed"));
    }

    public void Rejected(string code) => _rejected.Add(1, new KeyValuePair<string, object?>("code", code));

    public void Retried(string sqlState) => _retries.Add(1, new KeyValuePair<string, object?>("sqlstate", sqlState));

    public void ReconItems(string source, string kind, int count) =>
        _reconItems.Add(count, new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("kind", kind));

}
