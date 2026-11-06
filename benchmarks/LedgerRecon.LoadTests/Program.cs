using System.Globalization;
using System.Net.Http.Json;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

// Usage (against `docker compose up`):
//   dotnet run -c Release --project benchmarks/LedgerRecon.LoadTests -- --scenario spread --copies 32 --duration 30
// Scenarios:
//   spread  transfers between random pairs of 200 accounts: low lock contention, measures the posting path itself
//   hot     every transfer credits one shared account: all requests queue on one row lock (or collide under SSI)
//   replay  every request repeats an already-posted key: measures the idempotent replay path
// Closed model: `copies` virtual users each send the next request as soon as the previous one is answered, so the
// measured RPS is the throughput the service sustains at that concurrency.
var url = "http://localhost:58180";
var scenarioName = "spread";
var copies = 32;
var duration = 30;
var label = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);

for (var i = 0; i < args.Length - 1; i += 2)
{
    switch (args[i])
    {
        case "--url": url = args[i + 1]; break;
        case "--scenario": scenarioName = args[i + 1]; break;
        case "--copies": copies = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--duration": duration = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--label": label = args[i + 1]; break;
        default: throw new ArgumentException($"Unknown option {args[i]}");
    }
}

const int Accounts = 200;
const int ReplayPool = 1_000;
var prefix = $"LT{label}_";
using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };

string Code(int i) => $"{prefix}A{i:D3}";
var hot = prefix + "HOT";

async Task SetupAsync()
{
    for (var i = 0; i < Accounts; i++)
    {
        (await http.PostAsJsonAsync("/api/v1/accounts", new { code = Code(i), name = Code(i), type = "asset", currency = "TRY" })).EnsureSuccessStatusCode();
    }

    (await http.PostAsJsonAsync("/api/v1/accounts", new { code = hot, name = hot, type = "asset", currency = "TRY" })).EnsureSuccessStatusCode();

    if (scenarioName == "replay")
    {
        for (var i = 0; i < ReplayPool; i++)
        {
            (await http.SendAsync(Request($"{prefix}replay-{i}", ReplayBody(i)))).EnsureSuccessStatusCode();
        }
    }
}

object Body(string debit, string credit, long cents) => new
{
    description = "load test transfer",
    valueDate = "2026-11-05",
    currency = "TRY",
    lines = new object[]
    {
        new { accountCode = debit, direction = "debit", amount = cents / 100m },
        new { accountCode = credit, direction = "credit", amount = cents / 100m },
    },
};

object ReplayBody(int i) => Body(Code(i % Accounts), Code((i + 1) % Accounts), 1_000 + i);

HttpRequestMessage Request(string key, object body)
{
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/journal-entries") { Content = JsonContent.Create(body) };
    request.Headers.Add("Idempotency-Key", key);
    return request;
}

await SetupAsync();

var scenario = Scenario.Create(scenarioName, async context =>
    {
        HttpRequestMessage request;
        var rnd = Random.Shared;
        switch (scenarioName)
        {
            case "hot":
                request = Request($"{prefix}{Guid.NewGuid():N}", Body(hot, Code(rnd.Next(Accounts)), rnd.Next(1, 100_000)));
                break;
            case "replay":
                var i = rnd.Next(ReplayPool);
                request = Request($"{prefix}replay-{i}", ReplayBody(i));
                break;
            default:
                var a = rnd.Next(Accounts);
                var b = (a + 1 + rnd.Next(Accounts - 1)) % Accounts;
                request = Request($"{prefix}{Guid.NewGuid():N}", Body(Code(a), Code(b), rnd.Next(1, 100_000)));
                break;
        }

        using var response = await http.SendAsync(request);
        var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        return response.StatusCode == System.Net.HttpStatusCode.Created ? Response.Ok(statusCode: status) : Response.Fail(statusCode: status);
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(Simulation.KeepConstant(copies, TimeSpan.FromSeconds(duration)));

var result = NBomberRunner
    .RegisterScenarios(scenario)
    .WithReportFolder(Path.Combine("benchmark-results", $"{scenarioName}-{label}"))
    .WithReportFormats(ReportFormat.Md, ReportFormat.Csv)
    .Run();

var s = result.ScenarioStats[0];
var ok = s.Ok;
Console.WriteLine();
Console.WriteLine("| scenario | label | copies | ok | fail | RPS | p50 ms | p95 ms | p99 ms | max ms |");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"| {scenarioName} | {label} | {copies} | {ok.Request.Count} | {s.Fail.Request.Count} | {ok.Request.RPS:0} | {ok.Latency.Percent50:0.0} | {ok.Latency.Percent95:0.0} | {ok.Latency.Percent99:0.0} | {ok.Latency.MaxMs:0.0} |"));
foreach (var code in s.Fail.StatusCodes)
{
    Console.WriteLine($"  failures: HTTP {code.StatusCode} x {code.Count}");
}
