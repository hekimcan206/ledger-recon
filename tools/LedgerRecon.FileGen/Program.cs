using System.Globalization;
using LedgerRecon.Core.Reconciliation;
using LedgerRecon.Synthetic;

// Usage: dotnet run --project tools/LedgerRecon.FileGen -- [--seed 20261105] [--date 2026-11-05] [--count 80] [--out samples]
// For each source it writes the external file, the ledger items it is reconciled against (<name>.ledger.csv) and
// the engine's report (<name>.report.csv). The committed reports are the approved baseline of the golden tests:
// regenerate after an intended behaviour change and review the diff like code.
var seed = 20261105;
var date = new DateOnly(2026, 11, 5);
var count = 80;
var output = "samples";

for (var i = 0; i < args.Length - 1; i += 2)
{
    switch (args[i])
    {
        case "--seed": seed = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--date": date = DateOnly.ParseExact(args[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
        case "--count": count = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--out": output = args[i + 1]; break;
        default: throw new ArgumentException($"Unknown option {args[i]}");
    }
}

Directory.CreateDirectory(output);
var scenario = ScenarioGenerator.Generate(seed, date, count);
foreach (var source in scenario.Sources)
{
    var name = Path.GetFileNameWithoutExtension(source.FileName);
    var file = ReconFiles.Parse(source.Source, source.FileContent).Value;
    var result = ReconciliationEngine.Reconcile(file, source.LedgerItems, MatchPolicy.For(source.Source), date);

    File.WriteAllText(Path.Combine(output, source.FileName), source.FileContent);
    File.WriteAllText(Path.Combine(output, name + ".ledger.csv"), ReconFiles.WriteLedgerItems(source.LedgerItems));
    File.WriteAllText(Path.Combine(output, name + ".report.csv"), ReconFiles.WriteReport(result.Items));

    var summary = string.Join(", ", result.Summary().Where(k => k.Value > 0).Select(k => $"{k.Key}={k.Value}"));
    Console.WriteLine($"{source.FileName}: {file.Records.Count} records, {source.LedgerItems.Count} ledger items -> {summary}");
}
