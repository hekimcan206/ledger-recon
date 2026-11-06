using LedgerRecon.Core.Reconciliation;

namespace LedgerRecon.UnitTests.Reconciliation;

/// <summary>
/// Approval tests over the committed sample files: parsing + matching the file against its ledger fixture must
/// reproduce the approved difference report byte for byte. Unlike the property tests, these inputs are fixed text
/// files, so they also catch parser changes (column handling, sign conventions, truncation) that the generator
/// would silently follow. Regenerate with tools/LedgerRecon.FileGen and review the diff.
/// </summary>
public sealed class GoldenReportTests
{
    private static readonly DateOnly BusinessDate = new(2026, 11, 5);

    public static TheoryData<string, ReconSource> Samples() => new()
    {
        { "card-settlement-2026-11-05.csv", ReconSource.Card },
        { "eft-fast-2026-11-05.csv", ReconSource.Eft },
        { "correspondent-usd-2026-11-05.sta", ReconSource.Correspondent },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Report_matches_the_approved_baseline(string fileName, ReconSource source)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "samples");
        var name = Path.GetFileNameWithoutExtension(fileName);
        var file = ReconFiles.Parse(source, File.ReadAllText(Path.Combine(dir, fileName)));
        var ledger = ReconFiles.ReadLedgerItems(File.ReadAllText(Path.Combine(dir, name + ".ledger.csv")));

        var report = ReconFiles.WriteReport(ReconciliationEngine.Reconcile(file.Value, ledger, MatchPolicy.For(source), BusinessDate).Items);

        var approved = File.ReadAllText(Path.Combine(dir, name + ".report.csv")).Replace("\r\n", "\n", StringComparison.Ordinal);
        report.Should().Be(approved);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Baseline_contains_every_difference_kind_the_source_can_produce(string fileName, ReconSource source)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "samples");
        var report = File.ReadAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(fileName) + ".report.csv"));

        string[] expected = source switch
        {
            ReconSource.Correspondent => ["Matched", "MatchedWithinTolerance", "MatchedByAmountAndDate", "AmountMismatch", "DateMismatch", "MissingInLedger", "MissingInFile", "DuplicateInFile"],
            _ => ["Matched", "MatchedByAmountAndDate", "AmountMismatch", "DateMismatch", "MissingInLedger", "MissingInFile", "DuplicateInFile", "Unparseable"],
        };

        foreach (var kind in expected)
        {
            report.Should().Contain("\n" + kind + ",", $"the {source} sample should exercise {kind}");
        }
    }
}
