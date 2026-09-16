using Mandate.Core.Workflow.Guards;

namespace Mandate.Core.Tests.Workflow.Guards;

public sealed class GuardExpressionTests
{
    private static readonly DictionaryGuardValueResolver Context = new(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.scenario"] = "brownfield",
            ["run.risk"] = "high",
            ["test.coverage"] = "0.86",
            ["test.failures"] = "0",
            ["requirements.ambiguity-score"] = "0.2",
            ["release.approved"] = "true",
        });

    private static bool Eval(string source) =>
        GuardExpression.Parse(source).Evaluate(Context).Value;

    [Theory]
    [InlineData("run.scenario == 'brownfield'", true)]
    [InlineData("run.scenario == 'greenfield'", false)]
    [InlineData("run.scenario != 'greenfield'", true)]
    [InlineData("release.approved == true", true)]
    [InlineData("test.failures == 0", true)]
    public void Equality_compares_context_values(string source, bool expected) =>
        Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("test.coverage >= 0.8", true)]
    [InlineData("test.coverage > 0.9", false)]
    [InlineData("test.failures < 1", true)]
    [InlineData("requirements.ambiguity-score <= 0.2", true)]
    public void Order_comparisons_work_on_numbers(string source, bool expected) =>
        Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("run.scenario in ['brownfield', 'ambiguous']", true)]
    [InlineData("run.scenario in ['greenfield']", false)]
    [InlineData("run.scenario not in ['greenfield']", true)]
    public void Membership_tests_work(string source, bool expected) =>
        Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("run.scenario == 'brownfield' and test.coverage >= 0.8", true)]
    [InlineData("run.scenario == 'brownfield' and test.coverage >= 0.9", false)]
    [InlineData("run.scenario == 'greenfield' or run.risk == 'high'", true)]
    [InlineData("not run.scenario == 'greenfield'", true)]
    [InlineData("(run.scenario == 'greenfield' or run.risk == 'high') and test.failures == 0", true)]
    public void Connectives_and_grouping_work(string source, bool expected) =>
        Eval(source).ShouldBe(expected);

    [Fact]
    public void And_binds_more_tightly_than_or()
    {
        // 'a or (b and c)' - if precedence were reversed this would be false.
        Eval("run.risk == 'high' or run.scenario == 'greenfield' and test.failures == 99")
            .ShouldBeTrue();
    }

    [Fact]
    public void Keys_can_be_compared_to_each_other()
    {
        DictionaryGuardValueResolver resolver = new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.value"] = "5",
                ["b.value"] = "5",
            });

        GuardExpression.Parse("a.value == b.value").Evaluate(resolver).Value.ShouldBeTrue();
    }

    [Fact]
    public void Referenced_keys_are_collected_at_parse_time()
    {
        // Lets workflow validation catch a mistyped key at load, rather than as a
        // mysteriously skipped node mid-run.
        GuardExpression.Parse(
                "run.scenario in ['brownfield'] and (test.coverage >= 0.8 or run.risk == 'low')")
            .ReferencedKeys
            .ShouldBe(["run.scenario", "test.coverage", "run.risk"], ignoreOrder: true);
    }

    [Fact]
    public void Evaluation_explains_itself_for_the_audit_log()
    {
        GuardEvaluation evaluation =
            GuardExpression.Parse("run.scenario == 'brownfield'").Evaluate(Context);

        evaluation.Value.ShouldBeTrue();
        evaluation.Explanation.ShouldBe("run.scenario ('brownfield') == 'brownfield' → true");
    }

    [Fact]
    public void A_missing_key_fails_closed_rather_than_evaluating_to_false()
    {
        // A stage skipped because of a typo would be a governance failure, so an
        // unresolvable key is an error and not a quiet false.
        GuardEvaluationException error = Should.Throw<GuardEvaluationException>(
            () => Eval("run.scenarioo == 'brownfield'"));

        error.Key.ShouldBe("run.scenarioo");
        error.Message.ShouldContain("fail closed");
    }

    [Fact]
    public void Ordering_text_is_refused_rather_than_silently_comparing_strings()
    {
        Should.Throw<GuardEvaluationException>(() => Eval("run.scenario > 'a'"))
            .Message.ShouldContain("requires numbers");
    }
}
