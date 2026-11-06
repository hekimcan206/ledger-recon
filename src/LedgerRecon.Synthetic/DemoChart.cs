using System.Globalization;
using LedgerRecon.Core.Ledger;
using LedgerRecon.Core.Reconciliation;

namespace LedgerRecon.Synthetic;

public sealed record AccountSpec(string Code, string Name, AccountType Type, string Currency, bool PreventOverdraft);

/// <summary>
/// A small bank-like chart of accounts. Clearing accounts (card clearing, central-bank settlement, USD nostro)
/// are only touched by channel transactions, so everything posted to them is something an external file
/// should eventually report. A prefix keeps parallel test runs apart in one database.
/// </summary>
public sealed class DemoChart(string prefix = "")
{
    public const int TryCustomers = 12;
    public const int UsdCustomers = 6;

    public string Prefix { get; } = prefix;

    public string CardClearing => Prefix + "CARD_CLEARING";

    public string SalesRevenue => Prefix + "SALES_REVENUE";

    public string SalesReturns => Prefix + "SALES_RETURNS";

    public string EftSettlement => Prefix + "CB_SETTLEMENT_TRY";

    public string NostroUsd => Prefix + "NOSTRO_USD";

    public string VaultTry => Prefix + "VAULT_TRY";

    public string VaultUsd => Prefix + "VAULT_USD";

    public string TryCustomer(int i) => Prefix + "CUST_TRY_" + i.ToString("D4", CultureInfo.InvariantCulture);

    public string UsdCustomer(int i) => Prefix + "CUST_USD_" + i.ToString("D4", CultureInfo.InvariantCulture);

    public string ClearingAccount(ReconSource source) => source switch
    {
        ReconSource.Card => CardClearing,
        ReconSource.Eft => EftSettlement,
        ReconSource.Correspondent => NostroUsd,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public IReadOnlyList<AccountSpec> Accounts()
    {
        var accounts = new List<AccountSpec>
        {
            new(CardClearing, "Card acquirer clearing", AccountType.Asset, "TRY", false),
            new(SalesRevenue, "Merchant sales", AccountType.Revenue, "TRY", false),
            new(SalesReturns, "Sales returns", AccountType.Expense, "TRY", false),
            new(EftSettlement, "Central bank settlement account (EFT/FAST)", AccountType.Asset, "TRY", false),
            new(NostroUsd, "USD nostro at correspondent bank", AccountType.Asset, "USD", false),
            new(VaultTry, "Cash vault TRY", AccountType.Asset, "TRY", false),
            new(VaultUsd, "Cash vault USD", AccountType.Asset, "USD", false),
        };

        for (var i = 1; i <= TryCustomers; i++)
        {
            accounts.Add(new AccountSpec(TryCustomer(i), $"Customer TRY {i}", AccountType.Liability, "TRY", true));
        }

        for (var i = 1; i <= UsdCustomers; i++)
        {
            accounts.Add(new AccountSpec(UsdCustomer(i), $"Customer USD {i}", AccountType.Liability, "USD", true));
        }

        return accounts;
    }
}
