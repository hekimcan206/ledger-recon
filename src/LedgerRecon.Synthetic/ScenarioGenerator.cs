using System.Globalization;
using System.Text;
using Bogus;
using LedgerRecon.Core.Ledger;
using LedgerRecon.Core.Reconciliation;
using LedgerRecon.Core.Reconciliation.Parsing;

namespace LedgerRecon.Synthetic;

/// <summary>How often each defect is injected into a generated file (per ledger transaction, at most one each).</summary>
public sealed record DefectRates(
    double Drop = 0.06,
    double AmountMismatch = 0.05,
    double WithinTolerance = 0.08,
    double Duplicate = 0.04,
    double WrongReference = 0.05,
    double Late = 0.04,
    double Extra = 0.05,
    int UnparseableLines = 1)
{
    public static DefectRates None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record GeneratedEntry(string IdempotencyKey, JournalEntryDraft Draft);

/// <summary>What the engine must report: a kind and the reference it applies to (ledger reference when a ledger side exists).</summary>
public sealed record ExpectedItem(DifferenceKind Kind, string? Reference);

public sealed record SourceScenario(
    ReconSource Source,
    string ClearingAccount,
    string FileName,
    string FileContent,
    IReadOnlyList<GeneratedEntry> Entries,
    IReadOnlyList<LedgerItem> LedgerItems,
    IReadOnlyList<ExpectedItem> Expected);

public sealed record Scenario(DateOnly BusinessDate, DemoChart Chart, IReadOnlyList<GeneratedEntry> FundingEntries, IReadOnlyList<SourceScenario> Sources)
{
    public IEnumerable<GeneratedEntry> AllEntries => FundingEntries.Concat(Sources.SelectMany(s => s.Entries));
}

/// <summary>
/// Generates one business day of synthetic activity: journal entries for card sales/refunds, incoming and outgoing
/// EFT/FAST transfers and USD wires, plus the three external files that should report them, with defects injected
/// at known positions. Because the generator records what it injected, a test can check that the engine finds
/// every defect and nothing else. Seeded: the same seed always gives byte-identical output.
/// All names, IBANs, card numbers and references are synthetic.
/// </summary>
public static class ScenarioGenerator
{
    private enum Defect
    {
        None,
        Drop,
        AmountMismatch,
        WithinTolerance,
        Duplicate,
        WrongReference,
        Late,
    }

    private sealed record Tx(string Reference, DateOnly ValueDate, long SignedMinor, int Customer);

    private sealed record FileLine(string? Reference, DateOnly Date, long SignedMinor);

    public static Scenario Generate(
        int seed,
        DateOnly businessDate,
        int transactionsPerSource = 40,
        DefectRates? rates = null,
        string keyPrefix = "demo",
        DemoChart? chart = null)
    {
        rates ??= new DefectRates();
        chart ??= new DemoChart();
        var funding = Funding(chart, businessDate, keyPrefix);
        var sources = new[] { ReconSource.Card, ReconSource.Eft, ReconSource.Correspondent }
            .Select((source, index) => GenerateSource(source, seed * 31 + index, businessDate, transactionsPerSource, rates, keyPrefix, chart))
            .ToArray();
        return new Scenario(businessDate, chart, funding, sources);
    }

    private static List<GeneratedEntry> Funding(DemoChart chart, DateOnly businessDate, string keyPrefix)
    {
        var date = businessDate.AddDays(-30);
        var entries = new List<GeneratedEntry>();
        for (var i = 1; i <= DemoChart.TryCustomers; i++)
        {
            entries.Add(Entry($"{keyPrefix}-fund-{chart.TryCustomer(i)}", "Opening cash deposit", date, "TRY", null,
                chart.VaultTry, chart.TryCustomer(i), 10_000_000_00));
        }

        for (var i = 1; i <= DemoChart.UsdCustomers; i++)
        {
            entries.Add(Entry($"{keyPrefix}-fund-{chart.UsdCustomer(i)}", "Opening cash deposit", date, "USD", null,
                chart.VaultUsd, chart.UsdCustomer(i), 20_000_000_00));
        }

        return entries;
    }

    private static SourceScenario GenerateSource(
        ReconSource source, int seed, DateOnly d, int count, DefectRates rates, string keyPrefix, DemoChart chart)
    {
        var rng = new Random(seed);
        var faker = new Faker("tr") { Random = new Randomizer(seed) };
        var policy = MatchPolicy.For(source);
        var usedAmounts = new HashSet<long>();
        var usedRefs = new HashSet<string>(StringComparer.Ordinal);
        var currency = source == ReconSource.Correspondent ? "USD" : "TRY";

        var (minAmount, maxAmount, inflowShare) = source switch
        {
            ReconSource.Card => (10_00L, 5_000_00L, 0.9),
            ReconSource.Eft => (50_00L, 250_000_00L, 0.6),
            _ => (500_00L, 500_000_00L, 0.55),
        };

        long UniqueAmount()
        {
            long value;
            do
            {
                value = rng.NextInt64(minAmount, maxAmount);
            }
            while (!usedAmounts.Add(value));
            return value;
        }

        string UniqueRef()
        {
            string value;
            do
            {
                value = source switch
                {
                    ReconSource.Card => rng.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture),
                    ReconSource.Eft => (rng.Next(2) == 0 ? "FAST" : "EFT") + d.ToString("yyMMdd", CultureInfo.InvariantCulture)
                        + rng.Next(0, 99_999_999).ToString("D8", CultureInfo.InvariantCulture),
                    // 20 characters; MT940 field 61 keeps only the first 16, which stay unique.
                    _ => "W" + d.ToString("yyMMdd", CultureInfo.InvariantCulture) + rng.Next(0, 999_999_999).ToString("D9", CultureInfo.InvariantCulture) + "-USD",
                };
            }
            while (!usedRefs.Add(value));
            return value;
        }

        // 1. Ledger transactions.
        var txs = new List<Tx>();
        for (var i = 0; i < count; i++)
        {
            var amount = UniqueAmount();
            var signed = rng.NextDouble() < inflowShare ? amount : -amount;
            var customer = 1 + rng.Next(source == ReconSource.Correspondent ? DemoChart.UsdCustomers : DemoChart.TryCustomers);
            txs.Add(new Tx(UniqueRef(), d.AddDays(-rng.Next(0, 5)), signed, customer));
        }

        var entries = txs.Select(t => ToEntry(source, t, chart, currency, keyPrefix)).ToList();
        var ledger = txs.Select((t, i) => new LedgerItem(i + 1, DeterministicGuid(rng), t.Reference, t.ValueDate, t.SignedMinor, currency)).ToList();

        // 2. What the external file reports, with defects.
        var lines = new List<FileLine>();
        var expected = new List<ExpectedItem>();
        foreach (var t in txs)
        {
            var defect = Pick(rng, rates, policy);
            var fileRef = source == ReconSource.Correspondent ? t.Reference[..16] : t.Reference;
            switch (defect)
            {
                case Defect.Drop:
                    expected.Add(new ExpectedItem(
                        t.ValueDate < d.AddDays(-policy.DateWindowDays) ? DifferenceKind.MissingInFile : DifferenceKind.Pending, t.Reference));
                    break;
                case Defect.AmountMismatch:
                    var delta = policy.AmountToleranceMinor + rng.NextInt64(1, 200_00);
                    lines.Add(new FileLine(fileRef, t.ValueDate, t.SignedMinor + (Math.Sign(t.SignedMinor) * delta)));
                    expected.Add(new ExpectedItem(DifferenceKind.AmountMismatch, t.Reference));
                    break;
                case Defect.WithinTolerance:
                    // The correspondent deducted its charges from the wire.
                    var charges = rng.NextInt64(1, policy.AmountToleranceMinor + 1);
                    lines.Add(new FileLine(fileRef, t.ValueDate, t.SignedMinor - (Math.Sign(t.SignedMinor) * charges)));
                    expected.Add(new ExpectedItem(DifferenceKind.MatchedWithinTolerance, t.Reference));
                    break;
                case Defect.Duplicate:
                    lines.Add(new FileLine(fileRef, t.ValueDate, t.SignedMinor));
                    lines.Add(new FileLine(fileRef, t.ValueDate, t.SignedMinor));
                    expected.Add(new ExpectedItem(DifferenceKind.Matched, t.Reference));
                    expected.Add(new ExpectedItem(DifferenceKind.DuplicateInFile, fileRef));
                    break;
                case Defect.WrongReference:
                    lines.Add(new FileLine(source == ReconSource.Correspondent ? null : UniqueRef(), t.ValueDate, t.SignedMinor));
                    expected.Add(new ExpectedItem(DifferenceKind.MatchedByAmountAndDate, t.Reference));
                    break;
                case Defect.Late:
                    lines.Add(new FileLine(fileRef, t.ValueDate.AddDays(-(policy.DateWindowDays + 1)), t.SignedMinor));
                    expected.Add(new ExpectedItem(DifferenceKind.DateMismatch, t.Reference));
                    break;
                default:
                    lines.Add(new FileLine(fileRef, t.ValueDate, t.SignedMinor));
                    expected.Add(new ExpectedItem(DifferenceKind.Matched, t.Reference));
                    break;
            }
        }

        var extras = (int)Math.Round(count * rates.Extra, MidpointRounding.AwayFromZero);
        for (var i = 0; i < extras; i++)
        {
            var reference = UniqueRef();
            var fileRef = source == ReconSource.Correspondent ? reference[..16] : reference;
            var amount = UniqueAmount();
            lines.Add(new FileLine(fileRef, d.AddDays(-rng.Next(0, 3)), rng.NextDouble() < inflowShare ? amount : -amount));
            expected.Add(new ExpectedItem(DifferenceKind.MissingInLedger, fileRef));
        }

        // Shuffle so defects are not clustered at the end of the file (Fisher-Yates with the seeded generator).
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }

        var unparseable = source == ReconSource.Correspondent ? 0 : rates.UnparseableLines;
        for (var i = 0; i < unparseable; i++)
        {
            expected.Add(new ExpectedItem(DifferenceKind.Unparseable, null));
        }

        var content = source switch
        {
            ReconSource.Card => CardFile(lines, d, rng, unparseable),
            ReconSource.Eft => EftFile(lines, rng, faker, unparseable),
            _ => Mt940File(lines, d, faker),
        };

        var fileName = source switch
        {
            ReconSource.Card => $"card-settlement-{d:yyyy-MM-dd}.csv",
            ReconSource.Eft => $"eft-fast-{d:yyyy-MM-dd}.csv",
            _ => $"correspondent-usd-{d:yyyy-MM-dd}.sta",
        };

        return new SourceScenario(source, chart.ClearingAccount(source), fileName, content, entries, ledger, expected);
    }

    private static Defect Pick(Random rng, DefectRates rates, MatchPolicy policy)
    {
        var r = rng.NextDouble();
        (Defect Defect, double Rate)[] table =
        [
            (Defect.Drop, rates.Drop),
            (Defect.AmountMismatch, rates.AmountMismatch),
            (Defect.WithinTolerance, policy.AmountToleranceMinor > 0 ? rates.WithinTolerance : 0),
            (Defect.Duplicate, rates.Duplicate),
            (Defect.WrongReference, policy.FallbackOnAmountAndDate ? rates.WrongReference : 0),
            (Defect.Late, rates.Late),
        ];

        foreach (var (defect, rate) in table)
        {
            if (r < rate)
            {
                return defect;
            }

            r -= rate;
        }

        return Defect.None;
    }

    private static GeneratedEntry ToEntry(ReconSource source, Tx t, DemoChart chart, string currency, string keyPrefix)
    {
        var amount = Math.Abs(t.SignedMinor);
        var inflow = t.SignedMinor > 0;
        var key = $"{keyPrefix}-{source.ToString().ToLowerInvariant()}-{t.Reference}";
        return source switch
        {
            ReconSource.Card => inflow
                ? Entry(key, "Card sale", t.ValueDate, currency, t.Reference, chart.CardClearing, chart.SalesRevenue, amount)
                : Entry(key, "Card refund", t.ValueDate, currency, t.Reference, chart.SalesReturns, chart.CardClearing, amount),
            ReconSource.Eft => inflow
                ? Entry(key, "Incoming transfer", t.ValueDate, currency, t.Reference, chart.EftSettlement, chart.TryCustomer(t.Customer), amount)
                : Entry(key, "Outgoing transfer", t.ValueDate, currency, t.Reference, chart.TryCustomer(t.Customer), chart.EftSettlement, amount),
            _ => inflow
                ? Entry(key, "Incoming USD wire", t.ValueDate, currency, t.Reference, chart.NostroUsd, chart.UsdCustomer(t.Customer), amount)
                : Entry(key, "Outgoing USD wire", t.ValueDate, currency, t.Reference, chart.UsdCustomer(t.Customer), chart.NostroUsd, amount),
        };
    }

    private static GeneratedEntry Entry(
        string key, string description, DateOnly valueDate, string currency, string? externalRef, string debit, string credit, long amount)
    {
        var draft = JournalEntryDraft.Create(
            description, valueDate, currency, externalRef,
            [new PostingLine(debit, Direction.Debit, amount), new PostingLine(credit, Direction.Credit, amount)]);
        return new GeneratedEntry(key, draft.Value);
    }

    private static Guid DeterministicGuid(Random rng)
    {
        Span<byte> bytes = stackalloc byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static string CardFile(List<FileLine> lines, DateOnly d, Random rng, int unparseable)
    {
        var sb = new StringBuilder().Append(CardSettlementParser.Header).Append('\n');
        string[] bins = ["545616", "454360", "979200", "402277"];
        foreach (var l in lines)
        {
            var gross = Math.Abs(l.SignedMinor);
            var fee = l.SignedMinor > 0 ? (long)Math.Round(gross * 0.0179m, MidpointRounding.AwayFromZero) : 0;
            sb.Append(CultureInfo.InvariantCulture, $"{d:yyyy-MM-dd},{l.Date:yyyy-MM-dd},T{rng.Next(1_000_000, 9_999_999)},{l.Reference},")
                .Append(CultureInfo.InvariantCulture, $"{rng.Next(100_000, 999_999)},{bins[rng.Next(bins.Length)]}******{rng.Next(0, 9_999):D4},")
                .Append(l.SignedMinor > 0 ? "SALE" : "REFUND").Append(',')
                .Append(Money.Format(gross)).Append(',').Append(Money.Format(fee)).Append(',').Append(Money.Format(gross - fee)).Append(",TRY\n");
        }

        for (var i = 0; i < unparseable; i++)
        {
            // A processor bug leaked a full card number: the parser must reject the line without storing it.
            sb.Append(CultureInfo.InvariantCulture, $"{d:yyyy-MM-dd},{d:yyyy-MM-dd},T{rng.Next(1_000_000, 9_999_999)},")
                .Append(CultureInfo.InvariantCulture, $"{rng.NextInt64(100_000_000_000, 999_999_999_999)},{rng.Next(100_000, 999_999)},")
                .Append(CultureInfo.InvariantCulture, $"9792{rng.NextInt64(100_000_000_000, 999_999_999_999)},SALE,100.00,1.79,98.21,TRY\n");
        }

        return sb.ToString();
    }

    private static string EftFile(List<FileLine> lines, Random rng, Faker faker, int unparseable)
    {
        string[] purposes = ["KIRA ODEMESI", "FATURA", "MAAS", "TICARI ODEME", "BORC KAPAMA", "AIDAT"];
        var sb = new StringBuilder().Append(EftTransferParser.Header).Append('\n');
        foreach (var l in lines)
        {
            var channel = l.Reference?.StartsWith("FAST", StringComparison.Ordinal) == true ? "FAST" : "EFT";
            sb.Append(CultureInfo.InvariantCulture, $"{l.Date:yyyy-MM-dd},{channel},{l.Reference},{(l.SignedMinor > 0 ? "IN" : "OUT")},")
                .Append(Money.Format(Math.Abs(l.SignedMinor))).Append(",TRY,")
                .Append(CultureInfo.InvariantCulture, $"TR{rng.Next(10, 99)}******************{rng.Next(0, 9_999):D4},")
                .Append(Csv(faker.Name.FullName())).Append(',').Append(purposes[rng.Next(purposes.Length)]).Append('\n');
        }

        for (var i = 0; i < unparseable; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{DateOnly.FromDayNumber(0):yyyy-MM-dd},EFT,EFTBROKEN{i},SIDEWAYS,10.00,TRY,TR00******************0000,X,BROKEN\n");
        }

        return sb.ToString();
    }

    private static string Mt940File(List<FileLine> lines, DateOnly d, Faker faker)
    {
        const long opening = 2_500_000_00;
        var closing = opening + lines.Sum(l => l.SignedMinor);
        var sb = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $":20:STMT{d:yyMMdd}\n")
            .Append(":25:CORRUS33XXX/0012345678\n")
            .Append(CultureInfo.InvariantCulture, $":28C:{d.DayOfYear:D5}/001\n")
            .Append(CultureInfo.InvariantCulture, $":60F:C{d.AddDays(-1):yyMMdd}USD{Mt940Amount(opening)}\n");
        var bankRef = 0;
        foreach (var l in lines)
        {
            bankRef++;
            sb.Append(CultureInfo.InvariantCulture, $":61:{l.Date:yyMMdd}{l.Date:MMdd}{(l.SignedMinor > 0 ? "C" : "D")}{Mt940Amount(Math.Abs(l.SignedMinor))}NTRF")
                .Append(l.Reference ?? "NONREF")
                .Append(CultureInfo.InvariantCulture, $"//CB{d:yyMMdd}{bankRef:D6}\n")
                .Append(":86:/ORDP/").Append(faker.Company.CompanyName().ToUpperInvariant().Replace('/', ' ')).Append("/REMI/INVOICE SETTLEMENT\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $":62F:{(closing >= 0 ? "C" : "D")}{d:yyMMdd}USD{Mt940Amount(Math.Abs(closing))}\n");
        return sb.ToString();
    }

    private static string Mt940Amount(long minor) => Money.Format(minor).Replace('.', ',');

    private static string Csv(string value) => value.Contains(',', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
