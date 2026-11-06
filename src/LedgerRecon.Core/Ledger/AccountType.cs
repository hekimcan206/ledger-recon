namespace LedgerRecon.Core.Ledger;

/// <summary>The five classic account classes. The class decides which side is the account's normal balance.</summary>
public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

public enum Direction
{
    Debit,
    Credit,
}

public static class AccountTypes
{
    /// <summary>
    /// Assets and expenses grow with debits; liabilities, equity and revenue grow with credits.
    /// Balances are stored signed (debits positive), so a credit-normal account has a negative stored balance.
    /// </summary>
    public static bool IsDebitNormal(this AccountType type) => type is AccountType.Asset or AccountType.Expense;

    /// <summary>The stored (debit-positive) balance expressed on the account's normal side.</summary>
    public static long NormalBalance(this AccountType type, long signedBalanceMinor) =>
        type.IsDebitNormal() ? signedBalanceMinor : -signedBalanceMinor;

    public static string ToDbValue(this AccountType type) => type switch
    {
        AccountType.Asset => "asset",
        AccountType.Liability => "liability",
        AccountType.Equity => "equity",
        AccountType.Revenue => "revenue",
        AccountType.Expense => "expense",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static AccountType FromDbValue(string value) => value switch
    {
        "asset" => AccountType.Asset,
        "liability" => AccountType.Liability,
        "equity" => AccountType.Equity,
        "revenue" => AccountType.Revenue,
        "expense" => AccountType.Expense,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown account type."),
    };

    public static bool TryParse(string? value, out AccountType type) =>
        Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);
}
