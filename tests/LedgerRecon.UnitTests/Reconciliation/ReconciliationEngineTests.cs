using LedgerRecon.Core.Reconciliation;

namespace LedgerRecon.UnitTests.Reconciliation;

public sealed class ReconciliationEngineTests
{
    private static readonly DateOnly Day = new(2026, 11, 5);
    private static readonly MatchPolicy Exact = new(AmountToleranceMinor: 0, DateWindowDays: 1);

    private static StatementRecord Rec(int line, string? reference, long amount, int daysAgo = 0, string currency = "TRY") =>
        new(line, reference, Day.AddDays(-daysAgo), amount, currency);

    private static LedgerItem Led(long id, string? reference, long amount, int daysAgo = 0, string currency = "TRY") =>
        new(id, Guid.NewGuid(), reference, Day.AddDays(-daysAgo), amount, currency);

    private static ReconResult Run(MatchPolicy policy, IReadOnlyList<StatementRecord> records, IReadOnlyList<LedgerItem> ledger, params ParseIssue[] issues) =>
        ReconciliationEngine.Reconcile(new ParsedFile(ReconSource.Card, records, issues), ledger, policy, Day);

    [Fact]
    public void Exact_reference_amount_and_date_is_a_match()
    {
        var result = Run(Exact, [Rec(2, "R1", 1_000)], [Led(1, "R1", 1_000)]);
        result.Items.Should().ContainSingle().Which.Kind.Should().Be(DifferenceKind.Matched);
        result.Differences.Should().BeEmpty();
    }

    [Fact]
    public void Amount_difference_beyond_tolerance_is_a_mismatch_with_signed_difference()
    {
        var item = Run(Exact, [Rec(2, "R1", 1_050)], [Led(1, "R1", 1_000)]).Items.Single();
        item.Kind.Should().Be(DifferenceKind.AmountMismatch);
        item.DifferenceMinor.Should().Be(50);
    }

    [Fact]
    public void Amount_difference_within_tolerance_is_matched_but_flagged()
    {
        var policy = Exact with { AmountToleranceMinor = 2_500 };
        var item = Run(policy, [Rec(2, "R1", 97_500)], [Led(1, "R1", 100_000)]).Items.Single();
        item.Kind.Should().Be(DifferenceKind.MatchedWithinTolerance);
        item.DifferenceMinor.Should().Be(-2_500);
        item.Kind.IsDifference().Should().BeFalse();
    }

    [Fact]
    public void Dates_outside_the_window_are_a_date_mismatch()
    {
        Run(Exact, [Rec(2, "R1", 1_000, daysAgo: 0)], [Led(1, "R1", 1_000, daysAgo: 3)])
            .Items.Single().Kind.Should().Be(DifferenceKind.DateMismatch);
    }

    [Fact]
    public void Currency_mismatch_is_reported_before_amounts_are_compared()
    {
        Run(Exact, [Rec(2, "R1", 1_000, currency: "USD")], [Led(1, "R1", 1_000)])
            .Items.Single().Kind.Should().Be(DifferenceKind.CurrencyMismatch);
    }

    [Fact]
    public void Reference_repeated_in_the_file_is_a_duplicate_after_the_first_match()
    {
        var result = Run(Exact, [Rec(2, "R1", 1_000), Rec(3, "R1", 1_000)], [Led(1, "R1", 1_000)]);
        result.Items.Select(i => i.Kind).Should().Equal(DifferenceKind.Matched, DifferenceKind.DuplicateInFile);
        result.Items[1].Detail.Should().Contain("line 2");
    }

    [Fact]
    public void Reference_repeated_in_the_file_without_any_ledger_item_is_missing_once_then_duplicate()
    {
        var result = Run(Exact with { FallbackOnAmountAndDate = false }, [Rec(2, "R9", 1_000), Rec(3, "R9", 1_000)], []);
        result.Items.Select(i => i.Kind).Should().Equal(DifferenceKind.MissingInLedger, DifferenceKind.DuplicateInFile);
    }

    [Fact]
    public void Two_ledger_items_with_one_reference_pair_by_closest_amount()
    {
        var result = Run(Exact, [Rec(2, "R1", 2_000), Rec(3, "R1", 1_000)], [Led(1, "R1", 1_000), Led(2, "R1", 2_000)]);
        result.Items.Should().OnlyContain(i => i.Kind == DifferenceKind.Matched);
        result.Items.Single(i => i.Record!.LineNumber == 2).Ledger!.PostingId.Should().Be(2);
    }

    [Fact]
    public void Wrong_reference_falls_back_to_amount_and_date_and_needs_review()
    {
        var item = Run(Exact, [Rec(2, "GARBLED", 4_321, daysAgo: 1)], [Led(7, "R1", 4_321)]).Items.Single();
        item.Kind.Should().Be(DifferenceKind.MatchedByAmountAndDate);
        item.Ledger!.PostingId.Should().Be(7);
        item.Detail.Should().Contain("GARBLED").And.Contain("R1");
    }

    [Fact]
    public void Fallback_prefers_the_closest_date()
    {
        var item = Run(Exact, [Rec(2, null, 500)], [Led(1, "A", 500, daysAgo: 1), Led(2, "B", 500, daysAgo: 0)]).Items
            .Single(i => i.Kind == DifferenceKind.MatchedByAmountAndDate);
        item.Ledger!.PostingId.Should().Be(2);
    }

    [Fact]
    public void Fallback_can_be_disabled()
    {
        var result = Run(Exact with { FallbackOnAmountAndDate = false }, [Rec(2, "X", 500)], [Led(1, "R1", 500, daysAgo: 5)]);
        result.Items.Select(i => i.Kind).Should().Equal(DifferenceKind.MissingInLedger, DifferenceKind.MissingInFile);
    }

    [Fact]
    public void Unreported_ledger_items_are_pending_inside_the_window_and_missing_after_it()
    {
        var result = Run(Exact, [], [Led(1, "NEW", 100, daysAgo: 1), Led(2, "OLD", 200, daysAgo: 2)]);
        result.Items.Single(i => i.Ledger!.PostingId == 1).Kind.Should().Be(DifferenceKind.Pending);
        var missing = result.Items.Single(i => i.Ledger!.PostingId == 2);
        missing.Kind.Should().Be(DifferenceKind.MissingInFile);
        missing.DifferenceMinor.Should().Be(-200);
    }

    [Fact]
    public void References_are_compared_on_a_prefix_when_the_format_truncates_them()
    {
        var policy = Exact with { ReferenceLength = 16 };
        Run(policy, [Rec(2, "W261105000000123", 1_000)], [Led(1, "w261105000000123-USD", 1_000)])
            .Items.Single().Kind.Should().Be(DifferenceKind.Matched);
    }

    [Fact]
    public void Parse_issues_become_unparseable_items_in_line_order()
    {
        var result = Run(Exact, [Rec(5, "R1", 1)], [Led(1, "R1", 1)], new ParseIssue(3, "bad"));
        result.Items.Select(i => (i.Kind, i.LineNumber)).Should().Equal((DifferenceKind.Unparseable, 3), (DifferenceKind.Matched, 5));
    }

    [Fact]
    public void Nonref_and_blank_references_have_no_key()
    {
        ReconciliationEngine.Key("NONREF", Exact).Should().BeNull();
        ReconciliationEngine.Key("  ", Exact).Should().BeNull();
        ReconciliationEngine.Key(" ab ", Exact).Should().Be("AB");
    }

    [Fact]
    public void Report_csv_is_stable_and_escapes_fields()
    {
        var csv = ReconFiles.WriteReport(Run(Exact, [Rec(2, "R,1", 1_050)], [Led(1, "R,1", 1_000)]).Items);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be(ReconFiles.ReportHeader);
        lines[1].Should().StartWith("AmountMismatch,2,\"R,1\",2026-11-05,10.50,1,").And.EndWith(",0.50,TRY,\"File 10.50, ledger 10.00.\"");
    }

    [Fact]
    public void Ledger_fixture_round_trips()
    {
        LedgerItem[] items = [Led(1, "R1", -1_234, daysAgo: 2), Led(2, null, 5)];
        ReconFiles.ReadLedgerItems(ReconFiles.WriteLedgerItems(items)).Should().BeEquivalentTo(items);
    }
}
