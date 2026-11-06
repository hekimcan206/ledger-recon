using LedgerRecon.Core.Reconciliation;
using LedgerRecon.Core.Reconciliation.Parsing;

namespace LedgerRecon.UnitTests.Reconciliation;

public sealed class ParserTests
{
    private const string CardHeader = CardSettlementParser.Header + "\n";
    private const string EftHeader = EftTransferParser.Header + "\n";

    [Fact]
    public void Card_sale_and_refund_become_signed_gross_amounts()
    {
        var file = CardSettlementParser.Parse(CardHeader
            + "2026-11-05,2026-11-04,T1234567,123456789012,654321,545616******1234,SALE,150.00,2.69,147.31,TRY\n"
            + "2026-11-05,2026-11-05,T1234567,210987654321,111111,545616******9876,REFUND,20.50,0.00,20.50,TRY\n");

        file.IsSuccess.Should().BeTrue();
        file.Value.Issues.Should().BeEmpty();
        file.Value.Records.Should().HaveCount(2);
        file.Value.Records[0].Should().BeEquivalentTo(new { LineNumber = 2, Reference = "123456789012", Date = new DateOnly(2026, 11, 4), AmountMinor = 15_000L, Currency = "TRY" });
        file.Value.Records[1].AmountMinor.Should().Be(-2_050);
    }

    [Fact]
    public void Card_line_with_full_card_number_is_rejected_without_echoing_it()
    {
        var file = CardSettlementParser.Parse(CardHeader
            + "2026-11-05,2026-11-05,T1234567,123456789012,654321,5456161234561234,SALE,10.00,0.18,9.82,TRY\n").Value;

        file.Records.Should().BeEmpty();
        file.Issues.Should().ContainSingle();
        file.Issues[0].LineNumber.Should().Be(2);
        file.Issues[0].Message.Should().Contain("masked").And.NotContain("5456161234561234");
    }

    [Theory]
    [InlineData("2026-11-05,2026-11-05,T1,123456789012,1,545616******1234,SALE,10.00,0.18,9.00,TRY", "net_amount")]
    [InlineData("2026-11-05,2026-11-05,T1,12345,1,545616******1234,SALE,10.00,0.18,9.82,TRY", "rrn")]
    [InlineData("2026-11-05,05.11.2026,T1,123456789012,1,545616******1234,SALE,10.00,0.18,9.82,TRY", "txn_date")]
    [InlineData("2026-11-05,2026-11-05,T1,123456789012,1,545616******1234,VOID,10.00,0.18,9.82,TRY", "txn_type")]
    [InlineData("2026-11-05,2026-11-05,T1,123456789012,1,545616******1234,SALE,10.001,0.18,9.821,TRY", "decimals")]
    [InlineData("2026-11-05,2026-11-05,T1", "11 fields")]
    public void Card_line_errors_are_reported_per_line(string line, string expected)
    {
        var file = CardSettlementParser.Parse(CardHeader + line + "\n").Value;
        file.Records.Should().BeEmpty();
        file.Issues.Should().ContainSingle().Which.Message.Should().Contain(expected);
    }

    [Fact]
    public void Wrong_header_rejects_the_whole_file()
    {
        CardSettlementParser.Parse("a,b,c\n1,2,3\n").Error!.Code.Should().Be("file.header_invalid");
        EftTransferParser.Parse("").Error!.Code.Should().Be("file.empty");
    }

    [Fact]
    public void Eft_in_and_out_have_opposite_signs_and_drop_personal_data()
    {
        var file = EftTransferParser.Parse(EftHeader
            + "2026-11-05,FAST,FAST2611050001,IN,1250.75,TRY,TR12******************3456,\"Yılmaz, Ayşe\",KIRA\n"
            + "2026-11-05,EFT,EFT2611050002,OUT,99.00,TRY,TR34******************7890,Demir Ltd,FATURA\n").Value;

        file.Issues.Should().BeEmpty();
        file.Records.Select(r => r.AmountMinor).Should().Equal(125_075, -9_900);
        file.Records[0].Narrative.Should().Be("FAST KIRA");
        file.Records.Should().AllSatisfy(r => (r.Narrative ?? string.Empty).Should().NotContain("Ayşe").And.NotContain("TR12"));
    }

    [Fact]
    public void Eft_invalid_direction_is_a_line_issue()
    {
        var file = EftTransferParser.Parse(EftHeader + "2026-11-05,EFT,X1,SIDEWAYS,10.00,TRY,TR00,X,Y\n").Value;
        file.Issues.Should().ContainSingle().Which.Message.Should().Contain("direction");
    }

    private const string Statement = """
        :20:STMT261105
        :25:CORRUS33XXX/0012345678
        :28C:00309/001
        :60F:C261104USD1000,00
        :61:2611051105C250,50NTRFW261105000000123//CB261105000001
        :86:/ORDP/ACME TRADING/REMI/INVOICE 1
        :61:2611041104D100,00NTRFNONREF//CB261105000002
        :61:2611051105RC10,00NTRFW261105000000999//CB261105000003
        :62F:C261105USD1140,50
        """;

    [Fact]
    public void Mt940_lines_are_signed_from_the_nostro_point_of_view()
    {
        var file = Mt940Parser.Parse(Statement).Value;

        file.Records.Should().HaveCount(3);
        file.Records[0].Should().BeEquivalentTo(new
        {
            Reference = "W261105000000123",
            Date = new DateOnly(2026, 11, 5),
            AmountMinor = 25_050L,
            Currency = "USD",
            Narrative = "/ORDP/ACME TRADING/REMI/INVOICE 1",
        });
        file.Records[1].Reference.Should().BeNull("NONREF means the bank had no customer reference");
        file.Records[1].AmountMinor.Should().Be(-10_000);
        file.Records[2].AmountMinor.Should().Be(-1_000, "a reversal of a credit takes money out again");
    }

    [Fact]
    public void Mt940_that_does_not_add_up_is_rejected_as_a_whole()
    {
        var tampered = Statement.Replace(":62F:C261105USD1140,50", ":62F:C261105USD1140,51", StringComparison.Ordinal);
        Mt940Parser.Parse(tampered).Error!.Code.Should().Be("file.statement_does_not_balance");
    }

    [Theory]
    [InlineData(":61:2611051105X250,50NTRFREF", "file.statement_line_invalid")]
    [InlineData(":20:SECOND", "file.multiple_statements")]
    public void Mt940_structural_errors_reject_the_file(string extraLine, string code)
    {
        var broken = Statement.Replace(":62F:", extraLine + "\n:62F:", StringComparison.Ordinal);
        Mt940Parser.Parse(broken).Error!.Code.Should().Be(code);
    }

    [Fact]
    public void Mt940_without_balances_is_incomplete()
    {
        Mt940Parser.Parse(":20:X\n:25:Y\n").Error!.Code.Should().Be("file.statement_incomplete");
    }

    [Fact]
    public void Source_names_parse_case_insensitively()
    {
        ReconFiles.TryParseSource("CARD", out var card).Should().BeTrue();
        card.Should().Be(ReconSource.Card);
        ReconFiles.TryParseSource("swift", out _).Should().BeFalse();
        ReconFiles.TryParseSource("7", out _).Should().BeFalse();
    }
}
