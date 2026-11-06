using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LedgerRecon.Core.Ledger;

namespace LedgerRecon.UnitTests.Ledger;

/// <summary>
/// The double-entry rule over arbitrary inputs: whatever lines a client sends, a draft exists only if it balances,
/// and an accepted draft never creates or destroys money.
/// </summary>
public sealed class JournalEntryPropertyTests
{
    private static readonly DateOnly Day = new(2026, 11, 5);
    private static readonly string[] Codes = ["CASH", "BANK", "REVENUE", "WALLET_1", "WALLET_2"];

    private static Gen<PostingLine> LineGen =>
        from code in Gen.Elements(Codes)
        from debit in Gen.Elements(true, false)
        from amount in Gen.Choose(1, 1_000_000)
        select new PostingLine(code, debit ? Direction.Debit : Direction.Credit, amount);

    /// <summary>Arbitrary line sets: mostly unbalanced, so a matching balancing line is appended half of the time.</summary>
    public static Arbitrary<PostingLine[]> Lines() =>
        (from lines in Gen.ListOf(LineGen).Where(l => l.Count is >= 1 and <= 20)
         from balance in Gen.Elements(true, false)
         from code in Gen.Elements(Codes)
         select balance ? Balanced(lines, code) : [.. lines]).ToArbitrary();

    private static PostingLine[] Balanced(IReadOnlyList<PostingLine> lines, string code)
    {
        var net = lines.Sum(l => l.Direction == Direction.Debit ? l.AmountMinor : -l.AmountMinor);
        return net == 0
            ? [.. lines]
            : [.. lines, new PostingLine(code, net > 0 ? Direction.Credit : Direction.Debit, Math.Abs(net))];
    }

    [Property(Arbitrary = [typeof(JournalEntryPropertyTests)], MaxTest = 500)]
    public bool Draft_exists_if_and_only_if_debits_equal_credits(PostingLine[] lines)
    {
        var debits = lines.Where(l => l.Direction == Direction.Debit).Sum(l => l.AmountMinor);
        var credits = lines.Where(l => l.Direction == Direction.Credit).Sum(l => l.AmountMinor);
        var expectedValid = lines.Length >= 2 && debits > 0 && credits > 0 && debits == credits;

        return JournalEntryDraft.Create("prop", Day, "TRY", null, lines).IsSuccess == expectedValid;
    }

    [Property(Arbitrary = [typeof(JournalEntryPropertyTests)], MaxTest = 500)]
    public bool Accepted_drafts_net_to_zero_across_accounts(PostingLine[] lines)
    {
        var draft = JournalEntryDraft.Create("prop", Day, "TRY", null, lines);
        return !draft.IsSuccess || draft.Value.NetDeltas().Values.Sum() == 0;
    }

    [Property(Arbitrary = [typeof(JournalEntryPropertyTests)], MaxTest = 300)]
    public bool Fingerprint_ignores_formatting_but_not_content(PostingLine[] lines)
    {
        var draft = JournalEntryDraft.Create("prop", Day, "TRY", "REF-1", lines);
        if (!draft.IsSuccess)
        {
            return true;
        }

        // Same request with lower-case codes and padded text: canonicalisation must give the same hash.
        var reformatted = JournalEntryDraft.Create(
            " prop ", Day, "try", " REF-1 ", [.. lines.Select(l => l with { AccountCode = l.AccountCode.ToLowerInvariant() })]).Value;

        // Same request with one more kuruş on both sides of the first line pair: a different payload.
        var first = lines[0];
        var changed = JournalEntryDraft.Create(
            "prop", Day, "TRY", "REF-1",
            [.. lines, new PostingLine(first.AccountCode, Direction.Debit, 1), new PostingLine(first.AccountCode, Direction.Credit, 1)]).Value;

        var hash = RequestFingerprint.Compute(draft.Value);
        return hash.AsSpan().SequenceEqual(RequestFingerprint.Compute(reformatted))
            && !hash.AsSpan().SequenceEqual(RequestFingerprint.Compute(changed));
    }
}
