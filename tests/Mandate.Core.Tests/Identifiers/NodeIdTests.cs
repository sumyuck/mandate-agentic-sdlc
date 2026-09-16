using Mandate.Core.Identifiers;

namespace Mandate.Core.Tests.Identifiers;

public sealed class NodeIdTests
{
    [Theory]
    [InlineData("requirements")]
    [InlineData("release-readiness")]
    [InlineData("impact-analysis")]
    [InlineData("test2")]
    public void Lowercase_slugs_are_accepted(string candidate) =>
        NodeId.Parse(candidate).Value.ShouldBe(candidate);

    [Theory]
    [InlineData("")]
    [InlineData("Requirements")]
    [InlineData("release readiness")]
    [InlineData("release_readiness")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("../escape")]
    public void Anything_that_would_be_unsafe_in_a_path_or_tag_is_rejected(string candidate)
    {
        // Node ids become directory names, git tags and log fields, so they are constrained
        // once here instead of being sanitised by every consumer.
        NodeId.TryParse(candidate, out _).ShouldBeFalse();
    }

    [Fact]
    public void Rejection_explains_the_expected_shape()
    {
        FormatException error = Should.Throw<FormatException>(() => NodeId.Parse("Release Readiness"));

        error.Message.ShouldContain("lowercase slug");
        error.Message.ShouldContain("release-readiness");
    }
}
