using LedgerRecon.Api.Infrastructure;
using LedgerRecon.Api.Ledger;
using LedgerRecon.Core.Ledger;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LedgerRecon.Api.Features;

internal sealed record CreateAccountRequest(string? Code, string? Name, string? Type, string? Currency, bool PreventOverdraft);

/// <param name="Balance">Signed balance, debits positive (the stored value).</param>
/// <param name="NormalBalance">Balance on the account's normal side: a funded customer (liability) account is positive here.</param>
internal sealed record AccountResponse(
    string Code, string Name, string Type, string Currency, bool PreventOverdraft, decimal Balance, decimal NormalBalance, long Version)
{
    public static AccountResponse From(AccountView a) => new(
        a.Code, a.Name, a.Type.ToDbValue(), a.Currency, a.PreventOverdraft, Money.ToMajor(a.BalanceMinor), Money.ToMajor(a.NormalBalanceMinor), a.Version);
}

internal sealed record CurrencyTotals(string Currency, decimal DebitBalances, decimal CreditBalances, decimal Net, int Accounts);

/// <param name="Balanced">True when every currency nets to zero and no stored balance drifts from its postings.</param>
internal sealed record TrialBalanceResponse(bool Balanced, int DriftingAccounts, IReadOnlyList<CurrencyTotals> Currencies);

internal static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder group)
    {
        var accounts = group.MapGroup("/accounts").WithTags("Accounts");

        accounts.MapPost("/", CreateAsync)
            .WithSummary("Open an account")
            .WithDescription("Idempotent by code: an identical definition returns the existing account (200); a different one is a 409.");

        accounts.MapGet("/{code}", GetAsync).WithName("GetAccount").WithSummary("Get an account and its balance");
        accounts.MapGet("/", ListAsync).WithSummary("List accounts, optionally by code prefix");

        group.MapGet("/ledger/trial-balance", TrialBalanceAsync)
            .WithTags("Ledger")
            .WithSummary("Trial balance per currency and balance-drift check");

        return group;
    }

    private static async Task<Results<Created<AccountResponse>, Ok<AccountResponse>, ProblemHttpResult>> CreateAsync(
        CreateAccountRequest request, AccountStore store, CancellationToken cancellationToken)
    {
        var code = JournalEntryDraft.NormaliseAccountCode(request.Code);
        if (code is null)
        {
            return Problems.Validation("account.code_invalid", "Account codes are 2-40 characters: A-Z, 0-9, '_', '-', '.'.", "code");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Problems.Validation("account.name_invalid", "Name is required and at most 100 characters.", "name");
        }

        if (!AccountTypes.TryParse(request.Type, out var type))
        {
            return Problems.Validation("account.type_invalid", "Type must be asset, liability, equity, revenue or expense.", "type");
        }

        var currency = request.Currency?.Trim().ToUpperInvariant();
        if (!Money.IsSupportedCurrency(currency))
        {
            return Problems.Validation("account.currency_unsupported", $"Currency must be one of {string.Join(", ", Money.Currencies)}.", "currency");
        }

        var result = await store.CreateAsync(code, name, type, currency!, request.PreventOverdraft, cancellationToken);
        if (!result.IsSuccess)
        {
            return Problems.From(result.Error);
        }

        var response = AccountResponse.From(result.Value.Account);
        return result.Value.Created
            ? TypedResults.Created($"/api/v1/accounts/{response.Code}", response)
            : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<AccountResponse>, NotFound>> GetAsync(string code, AccountStore store, CancellationToken cancellationToken)
    {
        var normalised = JournalEntryDraft.NormaliseAccountCode(code);
        var account = normalised is null ? null : await store.GetAsync(normalised, cancellationToken);
        return account is null ? TypedResults.NotFound() : TypedResults.Ok(AccountResponse.From(account));
    }

    private static async Task<Ok<AccountResponse[]>> ListAsync(AccountStore store, string? prefix, int? limit, CancellationToken cancellationToken)
    {
        var accounts = await store.ListAsync(prefix?.Trim().ToUpperInvariant(), Math.Clamp(limit ?? 100, 1, 500), cancellationToken);
        return TypedResults.Ok(accounts.Select(AccountResponse.From).ToArray());
    }

    private static async Task<Ok<TrialBalanceResponse>> TrialBalanceAsync(AccountStore store, CancellationToken cancellationToken)
    {
        var (currencies, drifting) = await store.TrialBalanceAsync(cancellationToken);
        var totals = currencies
            .Select(c => new CurrencyTotals(c.Currency, Money.ToMajor(c.DebitBalancesMinor), Money.ToMajor(c.CreditBalancesMinor), Money.ToMajor(c.NetMinor), c.Accounts))
            .ToArray();
        return TypedResults.Ok(new TrialBalanceResponse(drifting == 0 && totals.All(t => t.Net == 0), drifting, totals));
    }
}
