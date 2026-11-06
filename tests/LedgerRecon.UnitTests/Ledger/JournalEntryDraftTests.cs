using LedgerRecon.Core;
using LedgerRecon.Core.Ledger;

namespace LedgerRecon.UnitTests.Ledger;

public sealed class JournalEntryDraftTests
{
    private static readonly DateOnly Day = new(2026, 11, 5);

    private static Result<JournalEntryDraft> Create(params PostingLine[] lines) =>
        JournalEntryDraft.Create("Card sale", Day, "TRY", "RRN-1", lines);

    [Fact]
    public void Balanced_entry_is_accepted_and_normalised()
    {
        var result = JournalEntryDraft.Create(
            "  Card sale  ", Day, "try", " 123456789012 ",
            [new PostingLine("card_clearing", Direction.Debit, 15_000), new PostingLine("SALES_REVENUE", Direction.Credit, 15_000)]);

        result.IsSuccess.Should().BeTrue();
        var draft = result.Value;
        draft.Description.Should().Be("Card sale");
        draft.Currency.Should().Be("TRY");
        draft.ExternalRef.Should().Be("123456789012");
        draft.Lines[0].AccountCode.Should().Be("CARD_CLEARING");
        draft.TotalMinor.Should().Be(15_000);
        draft.AccountCodes.Should().Equal("CARD_CLEARING", "SALES_REVENUE");
    }

    [Fact]
    public void Unbalanced_entry_is_rejected_with_both_totals()
    {
        var result = Create(new PostingLine("A1", Direction.Debit, 10_000), new PostingLine("B1", Direction.Credit, 9_999));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("entry.unbalanced");
        result.Error.Message.Should().Contain("100.00").And.Contain("99.99");
    }

    [Fact]
    public void One_sided_entry_is_rejected()
    {
        var result = Create(new PostingLine("A1", Direction.Debit, 100), new PostingLine("B1", Direction.Debit, 100));
        result.Error!.Code.Should().Be("entry.one_sided");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(Money.MaxLineAmountMinor + 1)]
    public void Non_positive_or_huge_amounts_are_rejected(long amount)
    {
        var result = Create(new PostingLine("A1", Direction.Debit, amount), new PostingLine("B1", Direction.Credit, amount));
        result.Error!.Code.Should().Be("entry.amount_invalid");
        result.Error.Field.Should().Be("lines[0].amount");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("has space")]
    [InlineData("_LEADING")]
    [InlineData("")]
    public void Invalid_account_codes_are_rejected(string code)
    {
        var result = Create(new PostingLine(code, Direction.Debit, 1), new PostingLine("B1", Direction.Credit, 1));
        result.Error!.Code.Should().Be("entry.account_code_invalid");
    }

    [Fact]
    public void Single_line_entry_is_rejected()
    {
        JournalEntryDraft.Create("x", Day, "TRY", null, [new PostingLine("A1", Direction.Debit, 1)])
            .Error!.Code.Should().Be("entry.line_count_invalid");
    }

    [Theory]
    [InlineData("GBP")]
    [InlineData(null)]
    public void Unsupported_currency_is_rejected(string? currency)
    {
        JournalEntryDraft.Create("x", Day, currency, null, [new PostingLine("A1", Direction.Debit, 1), new PostingLine("B1", Direction.Credit, 1)])
            .Error!.Code.Should().Be("entry.currency_unsupported");
    }

    [Theory]
    [InlineData("ref with space")]
    [InlineData("-leading")]
    public void Invalid_external_reference_is_rejected(string reference)
    {
        JournalEntryDraft.Create("x", Day, "TRY", reference, [new PostingLine("A1", Direction.Debit, 1), new PostingLine("B1", Direction.Credit, 1)])
            .Error!.Code.Should().Be("entry.external_ref_invalid");
    }

    [Fact]
    public void Net_deltas_combine_lines_on_the_same_account()
    {
        var draft = Create(
            new PostingLine("A1", Direction.Debit, 700),
            new PostingLine("A1", Direction.Credit, 200),
            new PostingLine("B1", Direction.Credit, 500)).Value;

        draft.NetDeltas().Should().BeEquivalentTo(new Dictionary<string, long> { ["A1"] = 500, ["B1"] = -500 });
    }

    [Theory]
    [InlineData("10.00", true, 1000)]
    [InlineData("10.5", true, 1050)]
    [InlineData("0.01", true, 1)]
    [InlineData("10.001", false, 0)]
    public void Money_converts_two_decimal_amounts_only(string amount, bool ok, long minor)
    {
        Money.TryToMinor(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), out var result).Should().Be(ok);
        result.Should().Be(minor);
    }

    [Fact]
    public void Normal_balance_follows_the_account_class()
    {
        AccountType.Asset.NormalBalance(500).Should().Be(500);
        AccountType.Liability.NormalBalance(-500).Should().Be(500);
        AccountType.Revenue.IsDebitNormal().Should().BeFalse();
        AccountType.Expense.IsDebitNormal().Should().BeTrue();
    }
}
