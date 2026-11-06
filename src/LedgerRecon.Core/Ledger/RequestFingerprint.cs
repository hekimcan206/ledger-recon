using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LedgerRecon.Core.Ledger;

/// <summary>
/// SHA-256 over a canonical form of a validated entry. It is stored next to the idempotency key, so a replay
/// can be told apart from a key reused for a different request (which must fail, not silently succeed).
/// Canonicalisation happens after validation: "10.0" and "10.00", or "acc-1" and "ACC-1", hash the same.
/// Line order is part of the request, so reordering lines counts as a different payload.
/// </summary>
public static class RequestFingerprint
{
    private const int Version = 1;

    public static byte[] Compute(JournalEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var canonical = new CanonicalEntry(
            Version,
            draft.Description,
            draft.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            draft.Currency,
            draft.ExternalRef,
            [.. draft.Lines.Select(l => new CanonicalLine(l.AccountCode, l.Direction == Direction.Debit ? "D" : "C", l.AmountMinor))]);

        // JSON gives unambiguous field boundaries ("a|b"+"c" cannot collide with "a"+"b|c"), and the property
        // order is fixed by the record declaration, so the bytes are deterministic.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalJsonContext.Default.CanonicalEntry);
        return SHA256.HashData(bytes);
    }

    internal sealed record CanonicalEntry(int V, string Description, string ValueDate, string Currency, string? ExternalRef, CanonicalLine[] Lines);

    internal sealed record CanonicalLine(string Account, string Direction, long Amount);
}

[JsonSerializable(typeof(RequestFingerprint.CanonicalEntry))]
internal sealed partial class CanonicalJsonContext : JsonSerializerContext;
