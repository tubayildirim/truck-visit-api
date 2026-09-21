using System.Globalization;
using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Domain.Tests;

/// <summary>
/// Covers the acceptance criterion "ensure unit numbers and license plates are capitalized and
/// contain no whitespaces", and the culture trap hiding underneath it.
/// </summary>
public sealed class NormalizedCodeTests
{
    [Theory]
    [InlineData("mscu1234567", "MSCU1234567")]
    [InlineData("  MSCU1234567  ", "MSCU1234567")]
    [InlineData("mscu 123 4567", "MSCU1234567")]
    [InlineData("\tmscu\n1234567\r", "MSCU1234567")]
    public void UnitNumber_is_capitalised_and_stripped_of_all_whitespace(string raw, string expected) =>
        Assert.Equal(expected, UnitNumber.Create(raw).Value);

    [Theory]
    [InlineData("34 abc 123", "34ABC123")]
    [InlineData("34-abc-123", "34-ABC-123")]
    public void LicensePlate_is_capitalised_and_stripped_of_all_whitespace(string raw, string expected) =>
        Assert.Equal(expected, LicensePlate.Create(raw).Value);

    /// <summary>
    /// The reason <see cref="NormalizedCode"/> uses ToUpperInvariant and not ToUpper.
    /// </summary>
    /// <remarks>
    /// In a Turkish locale the default upper-casing maps 'i' to 'İ' (U+0130, capital I with dot),
    /// not to 'I'. A server running under tr-TR with a naive implementation would store a plate
    /// differently from an identical server running under en-GB: the same truck would produce two
    /// unmatchable records, and an audit trail that cannot be joined back together. This test
    /// fails the moment someone "simplifies" the normaliser to ToUpper().
    /// </remarks>
    [Fact]
    public void Normalisation_is_culture_independent_even_under_a_Turkish_locale()
    {
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            var plate = LicensePlate.Create("34 ibb 123");

            // Not "34İBB123", which is what ToUpper() would produce here.
            Assert.Equal("34IBB123", plate.Value);
            Assert.DoesNotContain('İ', plate.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Terminal_codes_normalise_the_same_way_because_they_gate_access()
    {
        // If these produced different values, authorization would compare a token claim against a
        // stored value that can never match.
        Assert.Equal(TerminalCode.Create(" dover ").Value, TerminalCode.Create("DOVER").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Blank_values_are_rejected(string? raw)
    {
        var exception = Assert.Throws<DomainValidationException>(() => UnitNumber.Create(raw));
        Assert.Equal("unitNumber", exception.Field);
    }

    [Fact]
    public void Values_longer_than_the_limit_are_rejected() =>
        Assert.Throws<DomainValidationException>(
            () => UnitNumber.Create(new string('A', UnitNumber.MaxLength + 1)));

    [Fact]
    public void Length_is_measured_after_whitespace_is_removed()
    {
        // 20 significant characters plus padding is still a valid 20-character code.
        var padded = "   " + new string('A', UnitNumber.MaxLength) + "   ";

        Assert.Equal(UnitNumber.MaxLength, UnitNumber.Create(padded).Value.Length);
    }

    [Theory]
    [InlineData("MSCU/1234567")]
    [InlineData("MSCU_1234567")]
    [InlineData("MSCU;DROP")]
    [InlineData("ÜNİTE1")]
    public void Values_outside_the_permitted_character_set_are_rejected(string raw) =>
        Assert.Throws<DomainValidationException>(() => UnitNumber.Create(raw));

    [Fact]
    public void Codes_with_the_same_text_are_equal()
    {
        Assert.Equal(UnitNumber.Create("abc123"), UnitNumber.Create("ABC 123"));
        Assert.Equal(UnitNumber.Create("abc123").GetHashCode(), UnitNumber.Create("ABC123").GetHashCode());
    }

    [Fact]
    public void Different_code_types_are_never_equal_even_with_identical_text()
    {
        // A unit number that happens to read like a plate is still not a plate. Without this,
        // a mis-wired mapping could compare the two and quietly "match".
        Assert.NotEqual<object>(UnitNumber.Create("ABC123"), LicensePlate.Create("ABC123"));
    }
}
