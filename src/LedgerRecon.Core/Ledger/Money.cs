using System.Globalization;

namespace LedgerRecon.Core.Ledger;

/// <summary>
/// Amounts are stored as integer minor units (kuruş, cents). Floating point never touches money, and integer
/// sums in the database are exact. All supported currencies have two decimals (ISO 4217).
/// </summary>
public static class Money
{
    public const int MinorUnitsPerMajor = 100;

    /// <summary>Upper bound for one posting line: 10 trillion major units. Keeps every sum far from <see cref="long.MaxValue"/>.</summary>
    public const long MaxLineAmountMinor = 1_000_000_000_000_000;

    private static readonly HashSet<string> SupportedCurrencies = new(StringComparer.Ordinal) { "TRY", "USD", "EUR" };

    public static IReadOnlyCollection<string> Currencies => SupportedCurrencies;

    public static bool IsSupportedCurrency(string? currency) => currency is not null && SupportedCurrencies.Contains(currency);

    /// <summary>Converts a decimal amount to minor units; fails when it has more than two decimals or is out of range.</summary>
    public static bool TryToMinor(decimal amount, out long minor)
    {
        minor = 0;
        if (decimal.Round(amount, 2) != amount)
        {
            return false;
        }

        var scaled = amount * MinorUnitsPerMajor;
        if (scaled > MaxLineAmountMinor || scaled < -MaxLineAmountMinor)
        {
            return false;
        }

        minor = (long)scaled;
        return true;
    }

    public static decimal ToMajor(long minor) => minor / (decimal)MinorUnitsPerMajor;

    /// <summary>Invariant "1234.50" formatting for files and reports.</summary>
    public static string Format(long minor) => ToMajor(minor).ToString("0.00", CultureInfo.InvariantCulture);
}
