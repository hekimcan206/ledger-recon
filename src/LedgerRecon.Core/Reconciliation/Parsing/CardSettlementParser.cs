using System.Text.RegularExpressions;

namespace LedgerRecon.Core.Reconciliation.Parsing;

/// <summary>
/// Acquirer card settlement file, one transaction per line. Modelled on the common shape of acquirer CSV
/// exports (settlement date, transaction date, terminal, RRN, auth code, masked PAN, gross/fee/net); the exact
/// column set is this project's own, not a scheme specification.
/// Matching uses the gross amount: the ledger books the sale gross, fees are booked when the payout arrives.
/// </summary>
public static partial class CardSettlementParser
{
    public const string Header = "settlement_date,txn_date,terminal_id,rrn,auth_code,card_masked,txn_type,gross_amount,fee_amount,net_amount,currency";

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
                    return LedgerError.Unprocessable("file.header_invalid", $"Expected card settlement header '{Header}'.");
                }

                headerSeen = true;
                continue;
            }

            var f = Csv.Split(line);
            if (f is null || f.Length != 11)
            {
                issues.Add(new ParseIssue(number, "Expected 11 fields."));
                continue;
            }

            var rrn = f[3].Trim();
            if (!Csv.TryDate(f[1], out var txnDate))
            {
                issues.Add(new ParseIssue(number, "txn_date is not a yyyy-MM-dd date."));
            }
            else if (!RrnPattern().IsMatch(rrn))
            {
                issues.Add(new ParseIssue(number, "rrn must be 12 digits."));
            }
            else if (!MaskedPanPattern().IsMatch(f[5].Trim()))
            {
                // PCI DSS: a full card number must never be stored. The line is rejected and the value is not echoed.
                issues.Add(new ParseIssue(number, "card_masked is not a masked PAN (first 6, last 4 digits); line rejected and value not stored."));
            }
            else if (!Csv.TryAmount(f[7], out var gross) || gross <= 0
                || !Csv.TryAmount(f[8], out var fee) || fee < 0
                || !Csv.TryAmount(f[9], out var net))
            {
                issues.Add(new ParseIssue(number, "gross_amount, fee_amount and net_amount must be decimals with at most two places; gross must be positive."));
            }
            else if (gross - fee != net)
            {
                issues.Add(new ParseIssue(number, "net_amount must equal gross_amount - fee_amount."));
            }
            else if (!Ledger.Money.IsSupportedCurrency(f[10].Trim()))
            {
                issues.Add(new ParseIssue(number, "Unsupported currency."));
            }
            else
            {
                var sign = f[6].Trim().ToUpperInvariant() switch
                {
                    "SALE" => 1,
                    "REFUND" => -1,
                    _ => 0,
                };

                if (sign == 0)
                {
                    issues.Add(new ParseIssue(number, "txn_type must be SALE or REFUND."));
                    continue;
                }

                records.Add(new StatementRecord(number, rrn, txnDate, sign * gross, f[10].Trim(), $"terminal {f[2].Trim()} auth {f[4].Trim()}"));
            }
        }

        if (!headerSeen)
        {
            return LedgerError.Unprocessable("file.empty", "The file has no header line.");
        }

        return Result.Success(new ParsedFile(ReconSource.Card, records, issues));
    }

    [GeneratedRegex(@"^\d{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex RrnPattern();

    [GeneratedRegex(@"^\d{6}\*{6}\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex MaskedPanPattern();
}
