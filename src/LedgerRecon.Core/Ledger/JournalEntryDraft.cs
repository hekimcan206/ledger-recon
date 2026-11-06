using System.Text.RegularExpressions;

namespace LedgerRecon.Core.Ledger;

/// <summary>One side of a journal entry: an amount (always positive) debited or credited to one account.</summary>
public sealed record PostingLine(string AccountCode, Direction Direction, long AmountMinor);

/// <summary>
/// A validated, balanced journal entry that has not been posted yet. It can only be obtained through
/// <see cref="Create"/>, so every instance satisfies the double-entry rule: total debits equal total credits.
/// The database enforces the same rule again at commit time (deferred constraint trigger).
/// </summary>
public sealed partial class JournalEntryDraft
{
    public const int MaxLines = 50;
    public const int MaxDescriptionLength = 200;
    public const int MaxExternalRefLength = 64;

    private JournalEntryDraft(string description, DateOnly valueDate, string currency, string? externalRef, IReadOnlyList<PostingLine> lines)
    {
        Description = description;
        ValueDate = valueDate;
        Currency = currency;
        ExternalRef = externalRef;
        Lines = lines;
    }

    public string Description { get; }

    public DateOnly ValueDate { get; }

    public string Currency { get; }

    /// <summary>The reference external systems (card scheme, EFT/FAST, correspondent bank) report back; used by reconciliation.</summary>
    public string? ExternalRef { get; }

    public IReadOnlyList<PostingLine> Lines { get; }

    /// <summary>Total of the debit side, which equals the total of the credit side.</summary>
    public long TotalMinor => Lines.Where(l => l.Direction == Direction.Debit).Sum(l => l.AmountMinor);

    /// <summary>Codes of every account the entry touches, distinct and ordinal-sorted.</summary>
    public IReadOnlyList<string> AccountCodes =>
        Lines.Select(l => l.AccountCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Net signed (debit-positive) balance change per account. The deltas always sum to zero.</summary>
    public IReadOnlyDictionary<string, long> NetDeltas() =>
        Lines.GroupBy(l => l.AccountCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(l => l.Direction == Direction.Debit ? l.AmountMinor : -l.AmountMinor),
                StringComparer.Ordinal);

    public static Result<JournalEntryDraft> Create(
        string? description,
        DateOnly valueDate,
        string? currency,
        string? externalRef,
        IReadOnlyList<PostingLine>? lines)
    {
        description = description?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length > MaxDescriptionLength)
        {
            return LedgerError.Validation(
                "entry.description_invalid", $"Description is required and at most {MaxDescriptionLength} characters.", "description");
        }

        currency = currency?.Trim().ToUpperInvariant();
        if (!Money.IsSupportedCurrency(currency))
        {
            return LedgerError.Validation(
                "entry.currency_unsupported", $"Currency must be one of {string.Join(", ", Money.Currencies)}.", "currency");
        }

        externalRef = string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim();
        if (externalRef is not null && (externalRef.Length > MaxExternalRefLength || !ExternalRefPattern().IsMatch(externalRef)))
        {
            return LedgerError.Validation(
                "entry.external_ref_invalid",
                $"External reference must be 1-{MaxExternalRefLength} characters: letters, digits, '-', '_', '.', '/'.",
                "externalRef");
        }

        if (lines is null || lines.Count < 2 || lines.Count > MaxLines)
        {
            return LedgerError.Validation("entry.line_count_invalid", $"An entry needs between 2 and {MaxLines} lines.", "lines");
        }

        var normalised = new PostingLine[lines.Count];
        long debits = 0, credits = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var code = NormaliseAccountCode(line.AccountCode);
            if (code is null)
            {
                return LedgerError.Validation(
                    "entry.account_code_invalid", "Account codes are 2-40 characters: A-Z, 0-9, '_', '-', '.'.", $"lines[{i}].accountCode");
            }

            if (line.AmountMinor <= 0 || line.AmountMinor > Money.MaxLineAmountMinor)
            {
                return LedgerError.Validation(
                    "entry.amount_invalid", "Line amounts must be positive and within range; the direction carries the sign.", $"lines[{i}].amount");
            }

            // Bounded by MaxLines * MaxLineAmountMinor (5e16), far below long.MaxValue: no overflow is possible.
            if (line.Direction == Direction.Debit)
            {
                debits += line.AmountMinor;
            }
            else
            {
                credits += line.AmountMinor;
            }

            normalised[i] = line with { AccountCode = code };
        }

        if (debits == 0 || credits == 0)
        {
            return LedgerError.Validation("entry.one_sided", "An entry needs at least one debit line and one credit line.", "lines");
        }

        if (debits != credits)
        {
            return LedgerError.Validation(
                "entry.unbalanced", $"Debits ({Money.Format(debits)}) must equal credits ({Money.Format(credits)}).", "lines");
        }

        return Result.Success(new JournalEntryDraft(description, valueDate, currency!, externalRef, normalised));
    }

    /// <summary>Upper-cases and validates an account code; returns null when it is not a valid code.</summary>
    public static string? NormaliseAccountCode(string? code)
    {
        var normalised = code?.Trim().ToUpperInvariant();
        return normalised is not null && AccountCodePattern().IsMatch(normalised) ? normalised : null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_.-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountCodePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_./-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalRefPattern();
}
