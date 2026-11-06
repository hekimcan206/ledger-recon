using LedgerRecon.Api.Features;
using LedgerRecon.Api.Persistence;

namespace LedgerRecon.UnitTests.Api;

public sealed class ApiUnitTests
{
    [Theory]
    [InlineData("abc-123", "abc-123")]
    [InlineData("\"8e03978e-40d5-43e8-bc93-6894a57f9324\"", "8e03978e-40d5-43e8-bc93-6894a57f9324")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", null)]
    [InlineData("has space", null)]
    [InlineData("tab\there", null)]
    [InlineData("ğüş", null)]
    [InlineData(null, null)]
    public void Idempotency_keys_are_bare_or_structured_field_strings_of_visible_ascii(string? raw, string? expected) =>
        JournalEntryEndpoints.NormaliseKey(raw).Should().Be(expected);

    [Fact]
    public void Idempotency_keys_are_limited_to_128_characters()
    {
        JournalEntryEndpoints.NormaliseKey(new string('k', 128)).Should().NotBeNull();
        JournalEntryEndpoints.NormaliseKey(new string('k', 129)).Should().BeNull();
    }

    [Fact]
    public void Migrations_are_embedded_and_numbered_without_gaps()
    {
        var migrations = MigrationRunner.Load();
        migrations.Select(m => m.Version).Should().Equal(Enumerable.Range(1, migrations.Count));
        migrations.Should().OnlyContain(m => m.Sha256.Length == 64 && !m.Sql.Contains('\r', StringComparison.Ordinal));
    }
}
