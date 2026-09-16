using Mandate.Core.Identifiers;

namespace Mandate.Core.Tests.Identifiers;

public sealed class Sha256HashTests
{
    [Fact]
    public void Matches_the_published_digest_for_a_known_input()
    {
        // NIST test vector. Guards against an accidental change of algorithm or encoding
        // that would silently invalidate every previously recorded hash.
        Sha256Hash.OfUtf8("abc").Hex
            .ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Is_deterministic_for_equal_content() =>
        Sha256Hash.OfUtf8("requirements").ShouldBe(Sha256Hash.OfUtf8("requirements"));

    [Fact]
    public void Differs_for_a_single_byte_change() =>
        Sha256Hash.OfUtf8("requirements").ShouldNotBe(Sha256Hash.OfUtf8("requirementt"));

    [Fact]
    public void Genesis_is_a_reserved_all_zero_digest_no_content_can_produce()
    {
        Sha256Hash.Genesis.Hex.ShouldBe(new string('0', 64));
        Sha256Hash.OfUtf8(string.Empty).ShouldNotBe(Sha256Hash.Genesis);
    }

    [Fact]
    public void Round_trips_through_parse()
    {
        Sha256Hash hash = Sha256Hash.OfUtf8("abc");

        Sha256Hash.Parse(hash.Hex).ShouldBe(hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015adx")]
    public void Malformed_or_uppercase_digests_are_rejected(string candidate)
    {
        // One canonical textual form only: mixed casing would let the same digest compare
        // unequal and break content addressing.
        Sha256Hash.TryParse(candidate, out _).ShouldBeFalse();
    }

    [Fact]
    public void Abbreviated_form_is_eight_characters_for_report_tables() =>
        Sha256Hash.OfUtf8("abc").Abbreviated.ShouldBe("ba7816bf");
}
