using System.Net.Http.Json;
using LedgerRecon.Api.Ledger;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LedgerRecon.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real application in memory. Only configuration changes (connection string, isolation strategy);
/// no service is replaced, so the tests exercise the production composition root.
/// </summary>
public sealed class ApiFactory(string connectionString, string isolationStrategy = nameof(IsolationStrategy.RowLocks)) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Ledger:IsolationStrategy", isolationStrategy);
        builder.UseSetting("OpenApi:Enabled", "true");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }
}

/// <summary>Small helpers for building requests; tests stay readable and each one owns a unique code prefix.</summary>
public static class Ledger
{
    public static string NewPrefix() => "T" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() + "_";

    public static async Task OpenAsync(this HttpClient client, string code, string type, string currency = "TRY", bool preventOverdraft = false)
    {
        var response = await client.PostAsJsonAsync("/api/v1/accounts", new { code, name = code, type, currency, preventOverdraft });
        response.EnsureSuccessStatusCode();
    }

    public static object Transfer(string debit, string credit, decimal amount, string currency = "TRY", string? externalRef = null) => new
    {
        description = "transfer",
        valueDate = "2026-11-05",
        currency,
        externalRef,
        lines = new object[]
        {
            new { accountCode = debit, direction = "debit", amount },
            new { accountCode = credit, direction = "credit", amount },
        },
    };

    public static HttpRequestMessage PostEntry(string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/journal-entries") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    public static async Task<decimal> BalanceAsync(this HttpClient client, string code)
    {
        var account = await client.GetFromJsonAsync<AccountDto>($"/api/v1/accounts/{code}");
        return account!.Balance;
    }

    public sealed record AccountDto(string Code, decimal Balance, decimal NormalBalance, long Version);

    public sealed record EntryDto(Guid Id, string IdempotencyKey, string? ExternalRef);

    public sealed record ProblemDto(int Status, string? Code, string? Detail);
}
