using Mandate.Core.Identifiers;

namespace Mandate.Core.Tests.Identifiers;

public sealed class RunIdTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 16, 14, 25, 0, TimeSpan.Zero);

    [Fact]
    public void New_produces_a_sortable_canonical_form()
    {
        RunId id = RunId.New(Instant, "A1B2C3");

        id.Value.ShouldBe("run_20260916T142500Z_a1b2c3");
    }

    [Fact]
    public void Ids_sort_chronologically_as_plain_strings()
    {
        // Relied upon by run directories and database keys: no separate timestamp column needed.
        RunId earlier = RunId.New(Instant, "zzzzzz");
        RunId later = RunId.New(Instant.AddSeconds(1), "aaaaaa");

        string.CompareOrdinal(earlier.Value, later.Value).ShouldBeLessThan(0);
    }

    [Fact]
    public void Local_times_are_normalised_to_utc()
    {
        RunId fromOffset = RunId.New(new DateTimeOffset(2026, 9, 16, 9, 25, 0, TimeSpan.FromHours(-5)), "abc123");

        fromOffset.Value.ShouldBe("run_20260916T142500Z_abc123");
    }

    [Fact]
    public void Round_trips_through_parse()
    {
        RunId id = RunId.New(Instant, "abc123");

        RunId.Parse(id.Value).ShouldBe(id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("run_notatimestamp_abc")]
    [InlineData("run_20260916T142500Z")]
    [InlineData("run_20260916T142500Z_")]
    [InlineData("run_20260916T142500Z_a_b")]
    public void Malformed_values_are_rejected(string candidate)
    {
        RunId.TryParse(candidate, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => RunId.Parse(candidate));
    }

    [Fact]
    public void Default_value_is_recognisable_as_unassigned()
    {
        default(RunId).IsEmpty.ShouldBeTrue();
        RunId.New(Instant, "abc123").IsEmpty.ShouldBeFalse();
    }
}
