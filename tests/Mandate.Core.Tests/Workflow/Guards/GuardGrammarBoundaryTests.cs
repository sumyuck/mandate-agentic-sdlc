using Mandate.Core.Workflow.Guards;

namespace Mandate.Core.Tests.Workflow.Guards;

/// <summary>
/// The grammar's value is as much in what it cannot express as in what it can. A workflow
/// file is configuration under change control; if guards could reach code, the file would be
/// a code-execution surface. These tests pin the boundary.
/// </summary>
public sealed class GuardGrammarBoundaryTests
{
    /// <summary>Asserts the source fails to parse and returns the diagnostic.</summary>
    private static string ParseError(string source)
    {
        GuardExpression.TryParse(source, out GuardExpression? parsed, out string? error)
            .ShouldBeFalse($"'{source}' must not parse.");

        parsed.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
        return error!;
    }

    [Theory]
    // Function and method calls.
    [InlineData("System.Environment.Exit(0)")]
    [InlineData("eval('rm -rf /')")]
    [InlineData("run.scenario.StartsWith('brown')")]
    [InlineData("File.ReadAllText('/etc/passwd') == 'x'")]
    // Arithmetic and assignment.
    [InlineData("test.coverage + 1 > 2")]
    [InlineData("test.coverage = 0.9")]
    [InlineData("run.risk == 'high'; test.failures == 0")]
    // Indexing, interpolation, lambdas.
    [InlineData("run.scenario[0] == 'b'")]
    [InlineData("$'{run.scenario}' == 'brownfield'")]
    [InlineData("run.scenario => true")]
    // Shell and template injection shapes.
    [InlineData("run.scenario == `whoami`")]
    [InlineData("run.scenario == ${HOME}")]
    [InlineData("{{ run.scenario }} == 'brownfield'")]
    public void Anything_that_could_reach_code_is_not_expressible(string hostile)
    {
        GuardExpression.TryParse(hostile, out GuardExpression? parsed, out string? error)
            .ShouldBeFalse($"'{hostile}' must not parse.");

        parsed.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_guard_is_rejected(string source) =>
        GuardExpression.TryParse(source, out _, out _).ShouldBeFalse();

    [Fact]
    public void A_bare_key_is_not_a_condition()
    {
        // Ambiguous between "is present" and "is true"; the error says which to write.
        ParseError("release.approved").ShouldContain("release.approved == true");
    }

    [Fact]
    public void Comparing_two_literals_is_rejected_as_meaningless()
    {
        ParseError("'a' == 'a'").ShouldContain("must start with a context key");
    }

    [Fact]
    public void A_single_equals_sign_is_diagnosed_rather_than_merely_rejected()
    {
        // The most likely typo in hand-written YAML.
        ParseError("run.scenario = 'brownfield'").ShouldContain("Use '==' for equality");
    }

    [Fact]
    public void An_unterminated_string_is_diagnosed()
    {
        ParseError("run.scenario == 'brownfield").ShouldContain("Unterminated string");
    }

    [Fact]
    public void Unbalanced_parentheses_are_diagnosed()
    {
        ParseError("(run.scenario == 'brownfield'").ShouldContain("Expected ')'");
    }

    [Fact]
    public void An_empty_membership_list_is_rejected() =>
        GuardExpression.TryParse("run.scenario in []", out _, out _).ShouldBeFalse();

    [Fact]
    public void A_membership_list_may_not_contain_keys()
    {
        // Keeps the accepted set readable in the workflow file itself.
        ParseError("run.scenario in [run.risk]").ShouldContain("only literals");
    }

    [Fact]
    public void Trailing_input_after_a_complete_expression_is_rejected()
    {
        ParseError("run.scenario == 'brownfield' 'extra'").ShouldContain("after the end of the expression");
    }

    [Fact]
    public void A_malformed_context_key_is_rejected_at_parse_time()
    {
        ParseError("Run.Scenario == 'brownfield'").ShouldContain("not a valid context key");
    }

    [Fact]
    public void Errors_point_at_the_offending_position()
    {
        ParseError("run.scenario == ").ShouldContain("^");
    }
}
