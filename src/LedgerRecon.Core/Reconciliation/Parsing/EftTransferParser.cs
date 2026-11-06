namespace LedgerRecon.Core.Reconciliation.Parsing;

/// <summary>
/// Daily interbank transfer report for the bank's settlement account at the central bank, covering both EFT
/// (the Turkish central bank's interbank transfer system) and FAST (its 24/7 instant payment system).
/// The column layout is synthetic. Counterparty IBAN and name are read for validation only and are never kept,
/// because reconciliation needs neither (data minimisation, KVKK art. 4).
/// </summary>
public static class EftTransferParser
{
    public const string Header = "value_date,channel,transfer_id,direction,amount,currency,counterparty_iban,counterparty_name,description";

    public static Result<ParsedFile> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var records = new List<StatementRecord>();
        var issues = new List<ParseIssue>();
        var headerSeen = false;

        foreach (var (number, line) in Csv.Lines(content))
        {
            if (!headerSeen)
            {
                if (!string.Equals(line.Trim(), Header, StringComparison.OrdinalIgnoreCase))
                {
                    return LedgerError.Unprocessable("file.header_invalid", $"Expected EFT/FAST header '{Header}'.");
                }

                headerSeen = true;
                continue;
            }

            var f = Csv.Split(line);
            if (f is null || f.Length != 9)
            {
                issues.Add(new ParseIssue(number, "Expected 9 fields."));
                continue;
            }

            var channel = f[1].Trim().ToUpperInvariant();
            var direction = f[3].Trim().ToUpperInvariant();
            var reference = f[2].Trim();
            if (!Csv.TryDate(f[0], out var valueDate))
            {
                issues.Add(new ParseIssue(number, "value_date is not a yyyy-MM-dd date."));
            }
            else if (channel is not ("EFT" or "FAST"))
            {
                issues.Add(new ParseIssue(number, "channel must be EFT or FAST."));
            }
            else if (direction is not ("IN" or "OUT"))
            {
                issues.Add(new ParseIssue(number, "direction must be IN or OUT."));
            }
            else if (reference.Length is 0 or > 64)
            {
                issues.Add(new ParseIssue(number, "transfer_id is required (at most 64 characters)."));
            }
            else if (!Csv.TryAmount(f[4], out var amount) || amount <= 0)
            {
                issues.Add(new ParseIssue(number, "amount must be a positive decimal with at most two places."));
            }
            else if (!Ledger.Money.IsSupportedCurrency(f[5].Trim()))
            {
                issues.Add(new ParseIssue(number, "Unsupported currency."));
            }
            else
            {
                // Money in (IN) debits the settlement account in our books; money out credits it.
                var signed = direction == "IN" ? amount : -amount;
                records.Add(new StatementRecord(number, reference, valueDate, signed, f[5].Trim(), $"{channel} {f[8].Trim()}"));
            }
        }

        if (!headerSeen)
        {
            return LedgerError.Unprocessable("file.empty", "The file has no header line.");
        }

        return Result.Success(new ParsedFile(ReconSource.Eft, records, issues));
    }
}
