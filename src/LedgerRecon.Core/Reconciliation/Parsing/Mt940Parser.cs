using System.Globalization;
using System.Text.RegularExpressions;

namespace LedgerRecon.Core.Reconciliation.Parsing;

/// <summary>
/// Correspondent (nostro) bank statement in a subset of SWIFT MT940: one statement per file with tags
/// :20: (reference), :25: (account), :28C: (statement number), :60F:/:60M: (opening balance),
/// :61: (statement line) with optional :86: (information to account owner), and :62F:/:62M: (closing balance).
/// The opening balance plus all movements must equal the closing balance, otherwise the whole file is rejected:
/// a statement that does not add up cannot be trusted line by line.
/// </summary>
public static partial class Mt940Parser
{
    public static Result<ParsedFile> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var records = new List<StatementRecord>();
        var issues = new List<ParseIssue>();
        string? currency = null;
        long? opening = null, closing = null;
        var hasReference = false;
        var movements = 0L;
        StatementRecord? last = null;

        foreach (var (number, raw) in Csv.Lines(content))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith(":20:", StringComparison.Ordinal))
            {
                if (hasReference)
                {
                    return LedgerError.Unprocessable("file.multiple_statements", "Only one statement (:20:) per file is supported.");
                }

                hasReference = true;
            }
            else if (line.StartsWith(":60F:", StringComparison.Ordinal) || line.StartsWith(":60M:", StringComparison.Ordinal))
            {
                if (!TryBalance(line[5..], out currency, out var value))
                {
                    return LedgerError.Unprocessable("file.opening_balance_invalid", $"Line {number}: opening balance is not valid.");
                }

                opening = value;
            }
            else if (line.StartsWith(":61:", StringComparison.Ordinal))
            {
                last = null;
                var match = StatementLine().Match(line[4..]);
                if (currency is null)
                {
                    return LedgerError.Unprocessable("file.opening_balance_missing", "Statement lines appear before the opening balance (:60F:).");
                }

                if (!match.Success
                    || !TryYyMmDd(match.Groups["vd"].Value, out var valueDate)
                    || !decimal.TryParse(match.Groups["amt"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                    || !Ledger.Money.TryToMinor(amount, out var minor))
                {
                    // An unreadable line breaks the balance check below, so the file is rejected rather than half-trusted.
                    return LedgerError.Unprocessable("file.statement_line_invalid", $"Line {number}: field 61 is not valid.");
                }

                // Statement credit = money arrived at the correspondent = debit on our nostro account. Reversals flip it.
                var signed = match.Groups["dc"].Value switch
                {
                    "C" or "RD" => minor,
                    _ => -minor,
                };

                movements += signed;
                var reference = match.Groups["ref"].Value;
                last = new StatementRecord(number, reference is "" or "NONREF" ? null : reference, valueDate, signed, currency);
                records.Add(last);
            }
            else if (line.StartsWith(":86:", StringComparison.Ordinal) && last is not null)
            {
                records[^1] = last with { Narrative = line[4..].Trim() };
                last = null;
            }
            else if (line.StartsWith(":62F:", StringComparison.Ordinal) || line.StartsWith(":62M:", StringComparison.Ordinal))
            {
                if (!TryBalance(line[5..], out var closingCurrency, out var value) || closingCurrency != currency)
                {
                    return LedgerError.Unprocessable("file.closing_balance_invalid", $"Line {number}: closing balance is not valid.");
                }

                closing = value;
            }
        }

        if (!hasReference || opening is null || closing is null)
        {
            return LedgerError.Unprocessable("file.statement_incomplete", "The statement needs :20:, :60F: and :62F: tags.");
        }

        if (opening.Value + movements != closing.Value)
        {
            return LedgerError.Unprocessable(
                "file.statement_does_not_balance",
                $"Opening {Ledger.Money.Format(opening.Value)} + movements {Ledger.Money.Format(movements)} != closing {Ledger.Money.Format(closing.Value)}.");
        }

        return Result.Success(new ParsedFile(ReconSource.Correspondent, records, issues));
    }

    private static bool TryBalance(string value, out string? currency, out long minor)
    {
        currency = null;
        minor = 0;
        var match = Balance().Match(value);
        if (!match.Success
            || !TryYyMmDd(match.Groups["d"].Value, out _)
            || !decimal.TryParse(match.Groups["amt"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || !Ledger.Money.TryToMinor(amount, out minor)
            || !Ledger.Money.IsSupportedCurrency(match.Groups["ccy"].Value))
        {
            return false;
        }

        currency = match.Groups["ccy"].Value;
        minor = match.Groups["dc"].Value == "D" ? -minor : minor;
        return true;
    }

    private static bool TryYyMmDd(string value, out DateOnly date) =>
        DateOnly.TryParseExact("20" + value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    [GeneratedRegex(@"^(?<dc>[CD])(?<d>\d{6})(?<ccy>[A-Z]{3})(?<amt>\d{1,15},\d{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Balance();

    // YYMMDD [MMDD] (D|C|RD|RC) [funds code] amount N/F/S+3 customer-ref [//bank-ref] [supplementary]
    [GeneratedRegex(@"^(?<vd>\d{6})(?<ed>\d{4})?(?<dc>RC|RD|C|D)(?<fc>[A-Z])?(?<amt>\d{1,15},\d{0,2})(?<type>[NSF][A-Z0-9]{3})(?<ref>[^/]{0,16})(?://(?<bref>\S{1,16}))?.*$", RegexOptions.CultureInvariant)]
    private static partial Regex StatementLine();
}
