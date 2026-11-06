using LedgerRecon.Core;
using LedgerRecon.Core.Ledger;
using Npgsql;

namespace LedgerRecon.Api.Ledger;

internal sealed record AccountView(
    long Id, string Code, string Name, AccountType Type, string Currency, bool PreventOverdraft, long BalanceMinor, long Version)
{
    /// <summary>Balance on the account's normal side (a funded customer liability is positive here).</summary>
    public long NormalBalanceMinor => Type.NormalBalance(BalanceMinor);
}

internal sealed record CurrencyTrialBalance(string Currency, long DebitBalancesMinor, long CreditBalancesMinor, long NetMinor, int Accounts);

internal sealed class AccountStore(NpgsqlDataSource dataSource)
{
    private const string Columns = "id, code, name, type, currency, prevent_overdraft, balance_minor, version";

    /// <summary>
    /// Creates the account, or returns the existing one when an identical definition already exists, so seeding
    /// and client retries are naturally idempotent. A different definition under the same code is a conflict.
    /// </summary>
    public async Task<Result<(AccountView Account, bool Created)>> CreateAsync(
        string code, string name, AccountType type, string currency, bool preventOverdraft, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            INSERT INTO ledger.accounts (code, name, type, currency, prevent_overdraft)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (code) DO NOTHING
            RETURNING {Columns}
            """);
        cmd.Parameters.Add(new() { Value = code });
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = type.ToDbValue() });
        cmd.Parameters.Add(new() { Value = currency });
        cmd.Parameters.Add(new() { Value = preventOverdraft });

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                return Result.Success((Read(reader), true));
            }
        }

        var existing = await GetAsync(code, cancellationToken);
        if (existing is not null && existing.Name == name && existing.Type == type && existing.Currency == currency
            && existing.PreventOverdraft == preventOverdraft)
        {
            return Result.Success((existing, false));
        }

        return LedgerError.Conflict("account.code_taken", $"Account '{code}' already exists with a different definition.");
    }

    public async Task<AccountView?> GetAsync(string code, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM ledger.accounts WHERE code = $1");
        cmd.Parameters.Add(new() { Value = code });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<AccountView>> ListAsync(string? prefix, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {Columns} FROM ledger.accounts
             WHERE $1::text IS NULL OR starts_with(code, $1::text)
             ORDER BY code LIMIT $2
            """);
        cmd.Parameters.Add(new() { Value = (object?)prefix ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = limit });
        var list = new List<AccountView>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }

        return list;
    }

    /// <summary>Per currency, the signed balances of all accounts must sum to zero; anything else means money appeared or vanished.</summary>
    public async Task<(IReadOnlyList<CurrencyTrialBalance> Currencies, int DriftingAccounts)> TrialBalanceAsync(CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new("""
            SELECT currency,
                   coalesce(sum(balance_minor) FILTER (WHERE balance_minor > 0), 0)::bigint,
                   coalesce(-sum(balance_minor) FILTER (WHERE balance_minor < 0), 0)::bigint,
                   coalesce(sum(balance_minor), 0)::bigint,
                   count(*)::int
              FROM ledger.accounts GROUP BY currency ORDER BY currency
            """));
        batch.BatchCommands.Add(new("SELECT count(*)::int FROM ledger.account_balance_drift"));

        var currencies = new List<CurrencyTrialBalance>();
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            currencies.Add(new CurrencyTrialBalance(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)));
        }

        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (currencies, reader.GetInt32(0));
    }

    private static AccountView Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), AccountTypes.FromDbValue(r.GetString(3)), r.GetString(4), r.GetBoolean(5), r.GetInt64(6), r.GetInt64(7));
}
