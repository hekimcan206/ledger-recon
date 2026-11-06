namespace LedgerRecon.Core.Reconciliation;

/// <summary>The external files reconciled at end of day.</summary>
public enum ReconSource
{
    /// <summary>Acquirer card settlement file (CSV), matched on the retrieval reference number (RRN).</summary>
    Card,

    /// <summary>Interbank transfer report covering EFT and FAST (CSV), matched on the transfer id.</summary>
    Eft,

    /// <summary>Correspondent (nostro) bank statement in an MT940 subset, matched on the customer reference.</summary>
    Correspondent,
}

/// <summary>
/// One movement reported by an external file, already converted to the ledger's point of view:
/// <see cref="AmountMinor"/> is the signed (debit-positive) change on the clearing/nostro account.
/// </summary>
public sealed record StatementRecord(
    int LineNumber,
    string? Reference,
    DateOnly Date,
    long AmountMinor,
    string Currency,
    string? Narrative = null);

/// <summary>An unreconciled posting on the clearing/nostro account, signed debit-positive.</summary>
public sealed record LedgerItem(
    long PostingId,
    Guid EntryId,
    string? Reference,
    DateOnly ValueDate,
    long AmountMinor,
    string Currency);

/// <summary>A record line that could not be parsed; reported, not silently dropped.</summary>
public sealed record ParseIssue(int LineNumber, string Message);

/// <summary>Result of parsing one external file.</summary>
public sealed record ParsedFile(ReconSource Source, IReadOnlyList<StatementRecord> Records, IReadOnlyList<ParseIssue> Issues);

/// <summary>
/// Matching rules for one source.
/// </summary>
/// <param name="AmountToleranceMinor">
/// Largest absolute difference still accepted as a match (flagged as within tolerance). Correspondent banks
/// deduct charges from incoming wires, so a nostro credit can be slightly below the booked amount.
/// </param>
/// <param name="DateWindowDays">How far the file date may lie from the ledger value date (card settlement is T+1).</param>
/// <param name="ReferenceLength">
/// Compare only the first N characters of references; MT940 field 61 keeps 16 characters of the customer reference.
/// Null compares full references.
/// </param>
/// <param name="FallbackOnAmountAndDate">
/// When a record's reference matches nothing, pair it with a ledger item of the exact same amount inside the
/// date window. Such matches are reported separately so an operator can confirm them.
/// </param>
public sealed record MatchPolicy(
    long AmountToleranceMinor,
    int DateWindowDays,
    int? ReferenceLength = null,
    bool FallbackOnAmountAndDate = true)
{
    public static MatchPolicy For(ReconSource source) => source switch
    {
        ReconSource.Card => new MatchPolicy(AmountToleranceMinor: 0, DateWindowDays: 2),
        ReconSource.Eft => new MatchPolicy(AmountToleranceMinor: 0, DateWindowDays: 1),
        ReconSource.Correspondent => new MatchPolicy(AmountToleranceMinor: 5_000, DateWindowDays: 2, ReferenceLength: 16),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}

/// <summary>Outcome categories. Everything except the three "matched" kinds and <see cref="Pending"/> is a difference.</summary>
public enum DifferenceKind
{
    /// <summary>Reference, amount, currency and date agree.</summary>
    Matched,

    /// <summary>Reference matches and the amount differs by no more than the tolerance (e.g. bank charges).</summary>
    MatchedWithinTolerance,

    /// <summary>No reference match, but exactly one plausible ledger item by amount and date. Needs confirmation.</summary>
    MatchedByAmountAndDate,

    /// <summary>Reference matches but the amount differs by more than the tolerance.</summary>
    AmountMismatch,

    /// <summary>Reference and amount match but the dates are further apart than the window allows.</summary>
    DateMismatch,

    /// <summary>Reference matches but the currencies differ.</summary>
    CurrencyMismatch,

    /// <summary>The file reports a movement the ledger does not have.</summary>
    MissingInLedger,

    /// <summary>The ledger has a movement the file should have reported by now (older than the date window).</summary>
    MissingInFile,

    /// <summary>The same reference appears more often in the file than in the ledger.</summary>
    DuplicateInFile,

    /// <summary>A file line could not be parsed.</summary>
    Unparseable,

    /// <summary>Ledger item not reported yet but still inside the date window; carried to the next run. Not a difference.</summary>
    Pending,
}

public static class DifferenceKinds
{
    public static bool IsMatch(this DifferenceKind kind) =>
        kind is DifferenceKind.Matched or DifferenceKind.MatchedWithinTolerance or DifferenceKind.MatchedByAmountAndDate;

    public static bool IsDifference(this DifferenceKind kind) => !kind.IsMatch() && kind != DifferenceKind.Pending;

    /// <summary>Kinds that consume the ledger item, so later runs do not offer it again.</summary>
    public static bool ConsumesLedgerItem(this DifferenceKind kind) =>
        kind.IsMatch() || kind is DifferenceKind.AmountMismatch or DifferenceKind.DateMismatch or DifferenceKind.CurrencyMismatch;
}

/// <summary>One line of the reconciliation report.</summary>
/// <param name="Kind">Outcome category.</param>
/// <param name="Record">The file side, when there is one.</param>
/// <param name="Ledger">The ledger side, when there is one.</param>
/// <param name="Detail">Human-readable explanation for an operator.</param>
/// <param name="SourceLine">File line for items without a parsed record (unparseable lines).</param>
/// <param name="DifferenceMinor">File amount minus ledger amount, when both sides exist; otherwise the amount of the side that exists.</param>
public sealed record ReconItem(
    DifferenceKind Kind,
    StatementRecord? Record,
    LedgerItem? Ledger,
    long DifferenceMinor,
    string Detail,
    int? SourceLine = null)
{
    /// <summary>File line the item refers to; null for ledger-only items.</summary>
    public int? LineNumber => Record?.LineNumber ?? SourceLine;
}

public sealed record ReconResult(IReadOnlyList<ReconItem> Items)
{
    public int Count(DifferenceKind kind) => Items.Count(i => i.Kind == kind);

    public IEnumerable<ReconItem> Differences => Items.Where(i => i.Kind.IsDifference());

    public IReadOnlyDictionary<DifferenceKind, int> Summary() =>
        Enum.GetValues<DifferenceKind>().ToDictionary(k => k, Count);
}
