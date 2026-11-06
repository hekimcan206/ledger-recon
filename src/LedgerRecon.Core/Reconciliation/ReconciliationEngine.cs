namespace LedgerRecon.Core.Reconciliation;

/// <summary>
/// Matches one external file against the open ledger items of its clearing account. Pure and deterministic:
/// the same inputs always give the same report in the same order, which is what the golden tests rely on.
/// </summary>
/// <remarks>
/// Matching runs in three passes, strongest evidence first:
/// <list type="number">
/// <item>Reference match (optionally on a prefix, for formats that truncate references). The amount, currency
/// and date of the pair decide between matched, within tolerance, and a mismatch kind. A reference seen more
/// often in the file than in the ledger yields duplicates.</item>
/// <item>Fallback for records whose reference matched nothing: exact amount and currency, inside the date window,
/// closest date first. Reported as <see cref="DifferenceKind.MatchedByAmountAndDate"/> so a person confirms it.</item>
/// <item>Leftovers: file records are missing in the ledger; ledger items older than the window are missing in the
/// file, younger ones stay pending for the next run.</item>
/// </list>
/// </remarks>
public static class ReconciliationEngine
{
    public static ReconResult Reconcile(ParsedFile file, IReadOnlyList<LedgerItem> ledger, MatchPolicy policy, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(policy);

        var items = new List<ReconItem>();
        foreach (var issue in file.Issues)
        {
            items.Add(new ReconItem(DifferenceKind.Unparseable, null, null, 0, issue.Message, issue.LineNumber));
        }

        var unpairedLedger = new SortedDictionary<long, LedgerItem>();
        foreach (var item in ledger)
        {
            unpairedLedger.Add(item.PostingId, item);
        }

        var ledgerByReference = ledger
            .Select(l => (Key: Key(l.Reference, policy), Item: l))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList(), StringComparer.Ordinal);

        var firstLineByReference = new Dictionary<string, int>(StringComparer.Ordinal);
        var withoutReferenceMatch = new List<StatementRecord>();

        // Pass 1: reference.
        foreach (var record in file.Records.OrderBy(r => r.LineNumber))
        {
            var key = Key(record.Reference, policy);
            if (key is null)
            {
                withoutReferenceMatch.Add(record);
                continue;
            }

            var isRepeat = !firstLineByReference.TryAdd(key, record.LineNumber);
            if (ledgerByReference.TryGetValue(key, out var group))
            {
                var candidate = group
                    .Where(l => unpairedLedger.ContainsKey(l.PostingId))
                    .OrderBy(l => Math.Abs(record.AmountMinor - l.AmountMinor))
                    .ThenBy(l => DayDistance(record.Date, l.ValueDate))
                    .ThenBy(l => l.PostingId)
                    .FirstOrDefault();

                if (candidate is null)
                {
                    items.Add(Duplicate(record, firstLineByReference[key]));
                    continue;
                }

                unpairedLedger.Remove(candidate.PostingId);
                items.Add(Classify(record, candidate, policy));
            }
            else if (isRepeat)
            {
                items.Add(Duplicate(record, firstLineByReference[key]));
            }
            else
            {
                withoutReferenceMatch.Add(record);
            }
        }

        // Pass 2: amount + date fallback, then pass 3 for the file side.
        foreach (var record in withoutReferenceMatch)
        {
            var candidate = policy.FallbackOnAmountAndDate
                ? unpairedLedger.Values
                    .Where(l => l.AmountMinor == record.AmountMinor
                        && string.Equals(l.Currency, record.Currency, StringComparison.Ordinal)
                        && DayDistance(record.Date, l.ValueDate) <= policy.DateWindowDays)
                    .OrderBy(l => DayDistance(record.Date, l.ValueDate))
                    .ThenBy(l => l.PostingId)
                    .FirstOrDefault()
                : null;

            if (candidate is null)
            {
                items.Add(new ReconItem(
                    DifferenceKind.MissingInLedger, record, null, record.AmountMinor, "No ledger posting with this reference or amount."));
                continue;
            }

            unpairedLedger.Remove(candidate.PostingId);
            items.Add(new ReconItem(
                DifferenceKind.MatchedByAmountAndDate,
                record,
                candidate,
                0,
                $"Matched on amount and date only: file reference '{record.Reference ?? "-"}', ledger reference '{candidate.Reference ?? "-"}'."));
        }

        // Pass 3: ledger side.
        var cutoff = businessDate.AddDays(-policy.DateWindowDays);
        foreach (var item in unpairedLedger.Values)
        {
            items.Add(item.ValueDate < cutoff
                ? new ReconItem(DifferenceKind.MissingInFile, null, item, -item.AmountMinor, $"Not reported within {policy.DateWindowDays} day(s) of the value date.")
                : new ReconItem(DifferenceKind.Pending, null, item, 0, "Inside the date window; carried to the next run."));
        }

        return new ReconResult(
            [.. items.OrderBy(i => i.LineNumber ?? int.MaxValue).ThenBy(i => i.Ledger?.PostingId ?? long.MaxValue)]);
    }

    private static ReconItem Classify(StatementRecord record, LedgerItem ledger, MatchPolicy policy)
    {
        if (!string.Equals(record.Currency, ledger.Currency, StringComparison.Ordinal))
        {
            return new ReconItem(
                DifferenceKind.CurrencyMismatch, record, ledger, 0, $"File currency {record.Currency}, ledger currency {ledger.Currency}.");
        }

        var difference = record.AmountMinor - ledger.AmountMinor;
        if (Math.Abs(difference) > policy.AmountToleranceMinor)
        {
            return new ReconItem(
                DifferenceKind.AmountMismatch, record, ledger, difference,
                $"File {Ledger.Money.Format(record.AmountMinor)}, ledger {Ledger.Money.Format(ledger.AmountMinor)}.");
        }

        var days = DayDistance(record.Date, ledger.ValueDate);
        if (days > policy.DateWindowDays)
        {
            return new ReconItem(
                DifferenceKind.DateMismatch, record, ledger, difference,
                $"File date {record.Date:yyyy-MM-dd}, ledger value date {ledger.ValueDate:yyyy-MM-dd} ({days} days apart, window {policy.DateWindowDays}).");
        }

        return difference == 0
            ? new ReconItem(DifferenceKind.Matched, record, ledger, 0, string.Empty)
            : new ReconItem(
                DifferenceKind.MatchedWithinTolerance, record, ledger, difference,
                $"Difference {Ledger.Money.Format(difference)} within tolerance {Ledger.Money.Format(policy.AmountToleranceMinor)}.");
    }

    private static ReconItem Duplicate(StatementRecord record, int firstLine) =>
        new(DifferenceKind.DuplicateInFile, record, null, record.AmountMinor, $"Reference already reported on line {firstLine}.");

    private static int DayDistance(DateOnly a, DateOnly b) => Math.Abs(a.DayNumber - b.DayNumber);

    /// <summary>Normalised comparison key for a reference, or null when there is no usable reference.</summary>
    public static string? Key(string? reference, MatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var key = reference?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key) || key == "NONREF")
        {
            return null;
        }

        return policy.ReferenceLength is { } length && key.Length > length ? key[..length] : key;
    }
}
