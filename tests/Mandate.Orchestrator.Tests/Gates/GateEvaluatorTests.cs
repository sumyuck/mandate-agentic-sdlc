using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Gates;
using Mandate.Orchestrator.Tests.Support;

namespace Mandate.Orchestrator.Tests.Gates;

/// <summary>
/// Every evaluator is evidence-based and fails closed.
/// </summary>
/// <remarks>
/// The property being pinned throughout: absent or unreadable evidence is a failure, never a
/// pass. A gate that passed for want of data would appear in the audit log as a satisfied
/// check, which is worse than having no gate at all.
/// </remarks>
public sealed class GateEvaluatorTests
{
    private static async Task<GateConditionVerdict> JudgeAsync(
        IGateEvaluator evaluator,
        string expression,
        (string Key, string Value)[]? context = null,
        ArtifactKind[]? artifacts = null,
        (string Role, Actor Approver)[]? approvals = null,
        Actor? producer = null,
        ApprovalRequirement[]? required = null)
    {
        GateCondition condition = new(evaluator.Kind, expression, "Fixture condition.");

        WorkflowNode node = WorkflowFixtures.Node(
            "subject", approvals: required, exitGate: [condition]);

        FakeRunView run = new(context, artifacts, approvals, producer);

        return await evaluator.EvaluateAsync(
            new GateEvaluation(node, GatePosition.Exit, condition, run), CancellationToken.None);
    }

    // ---- artifact-exists ----

    [Fact]
    public async Task Artifact_exists_passes_when_the_kind_was_produced()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new ArtifactExistsGateEvaluator(), "design-doc", artifacts: [ArtifactKind.DesignDoc]);

        verdict.Passed.ShouldBeTrue();
        verdict.Explanation.ShouldContain("design-doc");
    }

    [Fact]
    public async Task Artifact_exists_fails_when_nothing_of_that_kind_exists()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new ArtifactExistsGateEvaluator(), "design-doc", artifacts: [ArtifactKind.TestReport]);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_artifact_kind_fails_closed()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new ArtifactExistsGateEvaluator(), "vibe-check", artifacts: [ArtifactKind.DesignDoc]);

        verdict.Passed.ShouldBeFalse();
        verdict.Explanation.ShouldContain("Fails closed");
    }

    [Fact]
    public async Task Any_artifact_exists_accepts_either_alternative()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new AnyArtifactExistsGateEvaluator(),
            "clarification-response, assumption",
            artifacts: [ArtifactKind.Assumption]);

        verdict.Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task Any_artifact_exists_fails_when_none_was_produced()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new AnyArtifactExistsGateEvaluator(),
            "clarification-response, assumption",
            artifacts: [ArtifactKind.DesignDoc]);

        verdict.Passed.ShouldBeFalse();
    }

    // ---- approval-held ----

    [Fact]
    public async Task Approval_held_fails_when_no_approval_was_recorded()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new ApprovalHeldGateEvaluator(), "tech-lead");

        verdict.Passed.ShouldBeFalse();
        verdict.Explanation.ShouldContain("No approval recorded");
    }

    [Fact]
    public async Task Approval_held_passes_when_a_different_person_approved()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new ApprovalHeldGateEvaluator(),
            "tech-lead",
            approvals: [("tech-lead", Actor.Human("lead"))],
            producer: Actor.Agent("architect"),
            required: [new ApprovalRequirement("tech-lead", "High impact.", true)]);

        verdict.Passed.ShouldBeTrue();
        verdict.Explanation.ShouldContain("human:lead");
    }

    [Fact]
    public async Task Approval_held_refuses_an_approver_who_produced_the_work()
    {
        // Segregation of duties, enforced by comparing recorded actors rather than by trust.
        GateConditionVerdict verdict = await JudgeAsync(
            new ApprovalHeldGateEvaluator(),
            "tech-lead",
            approvals: [("tech-lead", Actor.Agent("architect"))],
            producer: Actor.Agent("architect"),
            required: [new ApprovalRequirement("tech-lead", "High impact.", true)]);

        verdict.Passed.ShouldBeFalse();
        verdict.Explanation.ShouldContain("cannot also approve");
    }

    [Fact]
    public async Task Approval_held_allows_self_approval_only_where_segregation_was_waived()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            new ApprovalHeldGateEvaluator(),
            "tech-lead",
            approvals: [("tech-lead", Actor.Agent("architect"))],
            producer: Actor.Agent("architect"),
            required: [new ApprovalRequirement("tech-lead", "Low impact.", SegregationOfDuties: false)]);

        verdict.Passed.ShouldBeTrue();
    }

    // ---- evidence-backed conditions ----

    [Fact]
    public async Task Tests_pass_reads_the_recorded_failure_count()
    {
        IGateEvaluator evaluator = Evaluator("tests-pass");

        (await JudgeAsync(evaluator, "", context: [("test.failures", "0")])).Passed.ShouldBeTrue();
        (await JudgeAsync(evaluator, "", context: [("test.failures", "3")])).Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task Tests_pass_fails_closed_when_no_test_run_was_recorded()
    {
        // The important case: an agent asserting its code works is not evidence.
        GateConditionVerdict verdict = await JudgeAsync(Evaluator("tests-pass"), "");

        verdict.Passed.ShouldBeFalse();
        verdict.Explanation.ShouldContain("No evidence");
        verdict.Explanation.ShouldContain("worse than no check");
    }

    [Fact]
    public async Task Coverage_applies_the_threshold_in_the_condition()
    {
        IGateEvaluator evaluator = Evaluator("coverage-at-least");

        (await JudgeAsync(evaluator, "0.75", context: [("test.coverage", "0.86")]))
            .Passed.ShouldBeTrue();
        (await JudgeAsync(evaluator, "0.75", context: [("test.coverage", "0.20")]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_non_numeric_threshold_or_reading_fails_closed()
    {
        IGateEvaluator evaluator = Evaluator("coverage-at-least");

        (await JudgeAsync(evaluator, "most", context: [("test.coverage", "0.9")]))
            .Passed.ShouldBeFalse();
        (await JudgeAsync(evaluator, "0.75", context: [("test.coverage", "lots")]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task Ambiguity_applies_a_ceiling_rather_than_a_floor()
    {
        IGateEvaluator evaluator = Evaluator("ambiguity-below");

        (await JudgeAsync(evaluator, "0.3", context: [("requirements.ambiguity-score", "0.1")]))
            .Passed.ShouldBeTrue();
        (await JudgeAsync(evaluator, "0.3", context: [("requirements.ambiguity-score", "0.8")]))
            .Passed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("low", true)]
    [InlineData("medium", true)]
    [InlineData("high", false)]
    [InlineData("critical", false)]
    public async Task Findings_are_compared_against_a_severity_ceiling(string highest, bool expected)
    {
        GateConditionVerdict verdict = await JudgeAsync(
            Evaluator("no-findings-above"),
            "high",
            context: [("review.highest-severity", highest)]);

        verdict.Passed.ShouldBe(expected);
    }

    [Fact]
    public async Task An_unrecognised_severity_fails_closed_and_lists_the_ladder()
    {
        GateConditionVerdict verdict = await JudgeAsync(
            Evaluator("no-findings-above"), "spicy", context: [("review.highest-severity", "low")]);

        verdict.Passed.ShouldBeFalse();
        verdict.Explanation.ShouldContain("critical");
    }

    [Fact]
    public async Task Policy_clean_reads_the_pack_named_in_the_condition()
    {
        // One evaluator covers every pack, so adding a pack needs no engine change.
        IGateEvaluator evaluator = Evaluator("policy-clean");

        (await JudgeAsync(evaluator, "change-control",
            context: [("policy.change-control-clean", "true")])).Passed.ShouldBeTrue();

        (await JudgeAsync(evaluator, "change-control",
            context: [("policy.change-control-clean", "false")])).Passed.ShouldBeFalse();

        (await JudgeAsync(evaluator, "security",
            context: [("policy.change-control-clean", "true")])).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Every_built_in_evaluator_describes_what_it_judges()
    {
        foreach (IGateEvaluator evaluator in BuiltInGateEvaluators.All)
        {
            evaluator.Kind.ShouldNotBeNullOrWhiteSpace();
            evaluator.Describes.ShouldNotBeNullOrWhiteSpace();
        }
    }

    private static IGateEvaluator Evaluator(string kind) =>
        BuiltInGateEvaluators.CreateRegistry().Resolve(kind)
        ?? throw new InvalidOperationException($"No evaluator for '{kind}'.");

    private sealed class FakeRunView : IRunView
    {
        public FakeRunView(
            (string Key, string Value)[]? context,
            ArtifactKind[]? artifacts,
            (string Role, Actor Approver)[]? approvals,
            Actor? producer)
        {
            RunContext accumulated = RunContext.Empty;

            foreach ((string key, string value) in context ?? [])
            {
                accumulated = accumulated.Contribute(ContextFact.Create(
                    key, value, NodeId.Parse("fixture"), Actor.Agent("fixture"),
                    DateTimeOffset.UnixEpoch));
            }

            Context = accumulated;

            Artifacts =
            [
                .. (artifacts ?? []).Select((kind, index) => Artifact.FromContent(
                    kind,
                    $"fixture-{index}.txt",
                    "text/plain",
                    System.Text.Encoding.UTF8.GetBytes($"fixture {index}"),
                    NodeId.Parse("fixture"),
                    Actor.Agent("fixture"),
                    DateTimeOffset.UnixEpoch)),
            ];

            HeldApprovals = (approvals ?? [])
                .ToImmutableDictionary(
                    entry => entry.Role, entry => entry.Approver, StringComparer.OrdinalIgnoreCase);

            _producer = producer;
        }

        private readonly Actor? _producer;

        public RunId RunId { get; } = RunId.New(DateTimeOffset.UnixEpoch, "fixture");

        public RunStatus Status => RunStatus.Running;

        public RunContext Context { get; }

        public ImmutableArray<Artifact> Artifacts { get; }

        public ImmutableDictionary<string, Actor> HeldApprovals { get; }

        public NodeState StateOf(NodeId nodeId) => NodeState.Running;

        public Actor? ProducerOf(NodeId nodeId) => _producer;
    }
}
