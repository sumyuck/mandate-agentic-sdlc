using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Policy.Checks;
using Mandate.Policy.Tests.Support;

namespace Mandate.Policy.Tests;

public sealed class PolicyEngineTests
{
    private static PolicyPack Pack(params PolicyRule[] rules) =>
        new("fixture", "v1", "Engine fixture.", [.. rules]);

    private static PolicyRule Rule(
        string id, string check, PolicySeverity severity = PolicySeverity.Blocking) =>
        new(id, PolicyCategory.Compliance, severity, "Statement.", "Rationale.", check, "");

    private static PolicyEngine Engine(PolicyPack pack, params IPolicyCheck[] checks) =>
        new([pack], new PolicyCheckRegistry(checks));

    private static async Task<PolicyEvaluation> EvaluateAsync(PolicyEngine engine, FakeRun run)
    {
        using ScratchWorkspace workspace = new();

        return await engine.EvaluateAsync(
            "fixture", run, GraphFixtures.WithNodes(GraphFixtures.Node("a")),
            workspace, CancellationToken.None);
    }

    [Fact]
    public async Task A_clean_pack_reports_clean()
    {
        PolicyEngine engine = Engine(
            Pack(Rule("R1", "no-fully-autonomous-stage")), new NoFullyAutonomousStageCheck());

        PolicyEvaluation evaluation = await EvaluateAsync(engine, new FakeRun());

        evaluation.IsClean.ShouldBeTrue();
        evaluation.Summary.ShouldContain("all 1 rule(s) satisfied");
    }

    [Fact]
    public async Task A_rule_whose_check_is_not_registered_is_a_violation_not_a_skip()
    {
        // A control nobody can evaluate is not a control that passed.
        PolicyEngine engine = Engine(Pack(Rule("R1", "reads-the-runes")));

        PolicyEvaluation evaluation = await EvaluateAsync(engine, new FakeRun());

        evaluation.IsClean.ShouldBeFalse();
        evaluation.Verdicts[0].Explanation.ShouldContain("not a control that passed");
    }

    [Fact]
    public void Unevaluable_rules_are_reported_before_a_run_needs_them()
    {
        PolicyEngine engine = Engine(Pack(Rule("R1", "reads-the-runes")));

        ImmutableArray<string> problems = engine.FindUnmetRequirements();

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("reads-the-runes");
    }

    [Fact]
    public async Task An_unknown_pack_is_refused_with_the_names_that_are_loaded()
    {
        PolicyEngine engine = Engine(
            Pack(Rule("R1", "no-fully-autonomous-stage")), new NoFullyAutonomousStageCheck());

        using ScratchWorkspace workspace = new();

        KeyNotFoundException error = await Should.ThrowAsync<KeyNotFoundException>(
            () => engine.EvaluateAsync(
                "nonexistent", new FakeRun(),
                GraphFixtures.WithNodes(GraphFixtures.Node("a")),
                workspace, CancellationToken.None));

        error.Message.ShouldContain("fixture");
    }

    [Fact]
    public async Task A_waived_violation_stops_blocking_but_is_still_reported()
    {
        PolicyEngine engine = Engine(
            Pack(Rule("R1", "design-has-decision-record")), new DesignHasDecisionRecordCheck());

        FakeRun run = new FakeRun()
            .WithArtifact(Core.Artifacts.ArtifactKind.DesignDoc, "design.md")
            .WithWaiver("R1", by: "alex", reason: "Predates the requirement.");

        PolicyEvaluation evaluation = await EvaluateAsync(engine, run);

        evaluation.IsClean.ShouldBeTrue("a waived violation does not block.");
        evaluation.Waived.ShouldHaveSingleItem();
        evaluation.Verdicts[0].Satisfied.ShouldBeFalse("the rule is still violated.");
        evaluation.Summary.ShouldContain("1 waived");
    }

    [Fact]
    public async Task An_advisory_violation_is_reported_without_blocking()
    {
        PolicyEngine engine = Engine(
            Pack(Rule("R1", "design-has-decision-record", PolicySeverity.Advisory)),
            new DesignHasDecisionRecordCheck());

        FakeRun run = new FakeRun()
            .WithArtifact(Core.Artifacts.ArtifactKind.DesignDoc, "design.md");

        PolicyEvaluation evaluation = await EvaluateAsync(engine, run);

        evaluation.IsClean.ShouldBeTrue();
        evaluation.Advisory.ShouldHaveSingleItem();
        evaluation.Summary.ShouldContain("1 advisory");
    }

    [Fact]
    public void Two_checks_claiming_the_same_kind_are_refused() =>
        Should.Throw<ArgumentException>(() => new PolicyCheckRegistry(
            [new NoFullyAutonomousStageCheck(), new NoFullyAutonomousStageCheck()]));

    [Fact]
    public void Every_check_the_shipped_packs_name_is_registered()
    {
        // The packs and the checks are maintained separately; this is what stops them
        // drifting apart into a pack that reports violations for want of an evaluator.
        PolicyEngine engine = new(
            PolicyPackLoader.LoadDirectory(
                Path.Combine(RepositoryRoot.Path, "workflows", "policies")),
            PolicyCheckRegistry.BuiltIn((_, _) =>
                Task.FromResult(new AuditVerification(
                    RunId.New(DateTimeOffset.UnixEpoch, "test01"), 0, []))));

        engine.FindUnmetRequirements().ShouldBeEmpty();
    }
}
