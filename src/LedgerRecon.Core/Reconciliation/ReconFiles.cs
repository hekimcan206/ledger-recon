using System.Globalization;
using System.Text;
using LedgerRecon.Core.Ledger;
using LedgerRecon.Core.Reconciliation.Parsing;

namespace LedgerRecon.Core.Reconciliation;

public static class ReconFiles
{
    public static Result<ParsedFile> Parse(ReconSource source, string content) => source switch
    {
        ReconSource.Card => CardSettlementParser.Parse(content),
        ReconSource.Eft => EftTransferParser.Parse(content),
        ReconSource.Correspondent => Mt940Parser.Parse(content),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public static bool TryParseSource(string? value, out ReconSource source) =>
        Enum.TryParse(value, ignoreCase: true, out source) && Enum.IsDefined(source);

    public const string ReportHeader =
        "kind,line,file_reference,file_date,file_amount,posting_id,entry_id,ledger_reference,ledger_value_date,ledger_amount,difference,currency,detail";

    /// <summary>Difference report as CSV, one row per item, in the engine's deterministic order.</summary>
    public static string WriteReport(IEnumerable<ReconItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder().Append(ReportHeader).Append('\n');
        foreach (var i in items)
        {
            sb.Append(i.Kind).Append(',')
                .Append(i.LineNumber?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv.Escape(i.Record?.Reference)).Append(',')
                .Append(i.Record?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(i.Record is null ? null : Money.Format(i.Record.AmountMinor)).Append(',')
                .Append(i.Ledger?.PostingId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.Ledger?.EntryId.ToString()).Append(',')
                .Append(Csv.Escape(i.Ledger?.Reference)).Append(',')
                .Append(i.Ledger?.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(i.Ledger is null ? null : Money.Format(i.Ledger.AmountMinor)).Append(',')
                .Append(Money.Format(i.DifferenceMinor)).Append(',')
                .Append(i.Record?.Currency ?? i.Ledger?.Currency).Append(',')
                .Append(Csv.Escape(i.Detail)).Append('\n');
        }

        return sb.ToString();
    }

    public const string LedgerFixtureHeader = "posting_id,entry_id,reference,value_date,amount,currency";

    /// <summary>Ledger items as CSV: used for golden-test fixtures and the sample files.</summary>
    public static string WriteLedgerItems(IEnumerable<LedgerItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder().Append(LedgerFixtureHeader).Append('\n');
        foreach (var l in items)
        {
            sb.Append(l.PostingId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(l.EntryId).Append(',')
                .Append(Csv.Escape(l.Reference)).Append(',')
                .Append(l.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(Money.Format(l.AmountMinor)).Append(',')
                .Append(l.Currency).Append('\n');
        }

        return sb.ToString();
    }

    public static IReadOnlyList<LedgerItem> ReadLedgerItems(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var items = new List<LedgerItem>();
        foreach (var (number, line) in Csv.Lines(content).Skip(1))
        {
            var f = Csv.Split(line) ?? throw new FormatException($"Ledger fixture line {number} is malformed.");
            if (f.Length != 6 || !Csv.TryDate(f[3], out var date) || !Csv.TryAmount(f[4], out var amount))
            {
                throw new FormatException($"Ledger fixture line {number} is malformed.");
            }

            items.Add(new LedgerItem(
                long.Parse(f[0], CultureInfo.InvariantCulture),
                Guid.Parse(f[1]),
                f[2].Length == 0 ? null : f[2],
                date,
                amount,
                f[5]));
        }

        return items;
    }
}
