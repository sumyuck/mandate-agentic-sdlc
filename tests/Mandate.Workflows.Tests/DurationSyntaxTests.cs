namespace Mandate.Workflows.Tests;

public sealed class DurationSyntaxTests
{
    [Theory]
    [InlineData("45s", 45)]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("1.5m", 90)]
    [InlineData(" 2m ", 120)]
    public void Compact_durations_parse(string source, double expectedSeconds) =>
        DurationSyntax.Parse(source).TotalSeconds.ShouldBe(expectedSeconds);

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("m")]
    [InlineData("5d")]
    [InlineData("-5m")]
    [InlineData("0s")]
    [InlineData("00:05:00")]
    [InlineData("five minutes")]
    public void Ambiguous_or_unsupported_forms_are_rejected(string source)
    {
        // A bare number would be ambiguous about units, and a zero or negative timeout would
        // mean an attempt that can never end.
        DurationSyntax.TryParse(source, out _).ShouldBeFalse();
    }

    [Fact]
    public void Rejection_names_the_accepted_units() =>
        Should.Throw<FormatException>(() => DurationSyntax.Parse("5d"))
            .Message.ShouldContain("'s' seconds, 'm' minutes or 'h' hours");

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    [InlineData(90, "90s")]
    public void Durations_round_trip_through_the_compact_form(double seconds, string expected)
    {
        TimeSpan duration = TimeSpan.FromSeconds(seconds);

        DurationSyntax.Format(duration).ShouldBe(expected);
        DurationSyntax.Parse(DurationSyntax.Format(duration)).ShouldBe(duration);
    }
}
