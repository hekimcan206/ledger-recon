namespace LedgerRecon.Api.Ledger;

/// <summary>How concurrent postings are kept consistent (see docs/adr/0002).</summary>
internal enum IsolationStrategy
{
    /// <summary>
    /// Default. READ COMMITTED; the unique idempotency key arbitrates duplicates, and the touched account rows are
    /// locked with SELECT ... FOR UPDATE in ascending id order, which rules out deadlocks. No retries are needed.
    /// </summary>
    RowLocks,

    /// <summary>
    /// SERIALIZABLE (SSI) without explicit locks; conflicting transactions abort with 40001 and are retried with
    /// jittered exponential backoff. Kept as a measured alternative.
    /// </summary>
    Serializable,
}

internal sealed class LedgerOptions
{
    public const string Section = "Ledger";

    public IsolationStrategy IsolationStrategy { get; set; } = IsolationStrategy.RowLocks;

    /// <summary>Attempts for one request under <see cref="IsolationStrategy.Serializable"/>, including the first.</summary>
    public int MaxAttempts { get; set; } = 12;
}
