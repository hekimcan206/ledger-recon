using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LedgerRecon.Core.Reconciliation;
using LedgerRecon.Synthetic;

namespace LedgerRecon.UnitTests.Reconciliation;

/// <summary>
/// The generator records every defect it injects. For any seed and any defect mix, the engine must report
/// exactly those defects: nothing missed (recall) and nothing invented (precision).
/// </summary>
public sealed class ReconciliationPropertyTests
{
    public sealed record ReconCase(int Seed, int Transactions, DefectRates Rates, int DayOffset);

    public static Arbitrary<ReconCase> Cases() =>
        (from seed in Gen.Choose(1, int.MaxValue - 1)
         from count in Gen.Choose(1, 80)
         from rates in RatesGen
         from day in Gen.Choose(0, 400)
         select new ReconCase(seed, count, rates, day)).ToArbitrary();

    private static Gen<DefectRates> RatesGen =>
        from drop in Gen.Choose(0, 15)
        from amount in Gen.Choose(0, 15)
        from tolerance in Gen.Choose(0, 15)
        from duplicate in Gen.Choose(0, 15)
        from wrongRef in Gen.Choose(0, 15)
        from late in Gen.Choose(0, 15)
        from extra in Gen.Choose(0, 20)
        from unparseable in Gen.Choose(0, 3)
        select new DefectRates(drop / 100.0, amount / 100.0, tolerance / 100.0, duplicate / 100.0, wrongRef / 100.0, late / 100.0, extra / 100.0, unparseable);

    private static (string Kind, string? Reference)[] Normalise(IEnumerable<(DifferenceKind Kind, string? Reference)> items) =>
        [.. items.Select(i => (i.Kind.ToString(), i.Reference)).OrderBy(i => i.Item1, StringComparer.Ordinal).ThenBy(i => i.Reference, StringComparer.Ordinal)];

    [Property(Arbitrary = [typeof(ReconciliationPropertyTests)], MaxTest = 200)]
    public void Engine_reports_exactly_the_injected_defects(ReconCase c)
    {
        var scenario = ScenarioGenerator.Generate(c.Seed, new DateOnly(2026, 1, 1).AddDays(c.DayOffset), c.Transactions, c.Rates);
        foreach (var source in scenario.Sources)
        {
            var file = ReconFiles.Parse(source.Source, source.FileContent);
            file.IsSuccess.Should().BeTrue(file.Error?.Message);

            var result = ReconciliationEngine.Reconcile(file.Value, source.LedgerItems, MatchPolicy.For(source.Source), scenario.BusinessDate);

            var actual = Normalise(result.Items.Select(i => (i.Kind, i.Ledger?.Reference ?? i.Record?.Reference)));
            var expected = Normalise(source.Expected.Select(e => (e.Kind, e.Reference)));
            actual.Should().Equal(expected, $"source {source.Source}, seed {c.Seed}");
        }
    }

    [Property(Arbitrary = [typeof(ReconciliationPropertyTests)], MaxTest = 200)]
    public bool Every_record_and_every_ledger_item_appears_exactly_once(ReconCase c)
    {
        var scenario = ScenarioGenerator.Generate(c.Seed, new DateOnly(2026, 1, 1).AddDays(c.DayOffset), c.Transactions, c.Rates);
        return scenario.Sources.All(source =>
        {
            var file = ReconFiles.Parse(source.Source, source.FileContent).Value;
            var result = ReconciliationEngine.Reconcile(file, source.LedgerItems, MatchPolicy.For(source.Source), scenario.BusinessDate);
            var lines = result.Items.Where(i => i.LineNumber is not null).Select(i => i.LineNumber!.Value).ToArray();
            var postings = result.Items.Where(i => i.Ledger is not null).Select(i => i.Ledger!.PostingId).ToArray();
            return lines.Length == lines.Distinct().Count()
                && lines.Length == file.Records.Count + file.Issues.Count
                && postings.Order().SequenceEqual(source.LedgerItems.Select(l => l.PostingId).Order());
        });
    }

    [Fact]
    public void Generator_is_deterministic_for_a_seed()
    {
        var a = ScenarioGenerator.Generate(42, new DateOnly(2026, 11, 5));
        var b = ScenarioGenerator.Generate(42, new DateOnly(2026, 11, 5));
        a.Sources.Select(s => s.FileContent).Should().Equal(b.Sources.Select(s => s.FileContent));
        a.AllEntries.Select(e => e.IdempotencyKey).Should().Equal(b.AllEntries.Select(e => e.IdempotencyKey));
    }

    [Fact]
    public void Clean_files_match_completely()
    {
        var scenario = ScenarioGenerator.Generate(7, new DateOnly(2026, 11, 5), 50, DefectRates.None);
        foreach (var source in scenario.Sources)
        {
            var file = ReconFiles.Parse(source.Source, source.FileContent).Value;
            var result = ReconciliationEngine.Reconcile(file, source.LedgerItems, MatchPolicy.For(source.Source), scenario.BusinessDate);
            result.Items.Should().OnlyContain(i => i.Kind == DifferenceKind.Matched);
        }
    }
}
