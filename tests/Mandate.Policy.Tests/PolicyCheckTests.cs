using Mandate.Core.Artifacts;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Policy.Checks;
using Mandate.Policy.Tests.Support;

namespace Mandate.Policy.Tests;

/// <summary>
/// Each rule, satisfied and violated.
/// </summary>
/// <remarks>
/// The recurring property: absent evidence is a violation, never a pass. A control that held
/// because nobody produced the data it reads would appear in the audit trail as a control
/// that held.
/// </remarks>
public sealed class PolicyCheckTests
{
    private static PolicyRule Rule(string check, PolicySeverity severity = PolicySeverity.Blocking) =>
        new("TEST-001", PolicyCategory.Compliance, severity, "Statement.", "Rationale.", check, "");

    private static async Task<PolicyVerdict> JudgeAsync(
        IPolicyCheck check,
        FakeRun run,
        WorkflowGraph? graph = null,
        Core.Execution.IRunWorkspace? workspace = null,
        PolicySeverity severity = PolicySeverity.Blocking)
    {
        using ScratchWorkspace scratch = new();

        return await check.EvaluateAsync(
            new PolicyCheckContext(
                Rule(check.Kind, severity),
                run,
                graph ?? GraphFixtures.WithNodes(GraphFixtures.Node("a")),
                workspace ?? scratch),
            CancellationToken.None);
    }

    // ---- traceability ----

    [Fact]
    public async Task A_change_descending_from_a_requirement_is_traceable()
    {
        FakeRun run = new FakeRun().WithArtifact(ArtifactKind.RequirementSpec, "spec.md");
        run.WithArtifact(ArtifactKind.SourcePatch, "patch.diff", "implement", run.HashOf("spec.md"));

        (await JudgeAsync(new ChangeTracesToRequirementCheck(), run)).Satisfied.ShouldBeTrue();
    }

    [Fact]
    public async Task A_change_descending_from_nothing_is_a_violation()
    {
        // The first question asked of any change is why it exists.
        FakeRun run = new FakeRun()
            .WithArtifact(ArtifactKind.RequirementSpec, "spec.md")
            .WithArtifact(ArtifactKind.SourcePatch, "orphan.diff", "implement");

        PolicyVerdict verdict = await JudgeAsync(new ChangeTracesToRequirementCheck(), run);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("orphan.diff");
    }

    [Fact]
    public async Task A_run_that_changed_no_source_has_nothing_to_trace()
    {
        (await JudgeAsync(new ChangeTracesToRequirementCheck(), new FakeRun()))
            .Satisfied.ShouldBeTrue();
    }

    // ---- decisions ----

    [Fact]
    public async Task A_design_without_a_decision_record_is_a_violation()
    {
        FakeRun run = new FakeRun().WithArtifact(ArtifactKind.DesignDoc, "design.md");

        PolicyVerdict verdict = await JudgeAsync(new DesignHasDecisionRecordCheck(), run);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("nobody can argue with");
    }

    [Fact]
    public async Task A_design_with_a_decision_record_is_satisfied()
    {
        FakeRun run = new FakeRun()
            .WithArtifact(ArtifactKind.DesignDoc, "design.md")
            .WithArtifact(ArtifactKind.ArchitectureDecisionRecord, "adr-001.md");

        (await JudgeAsync(new DesignHasDecisionRecordCheck(), run)).Satisfied.ShouldBeTrue();
    }

    // ---- autonomy ----

    [Fact]
    public async Task A_fully_autonomous_stage_is_a_violation()
    {
        WorkflowGraph graph = GraphFixtures.WithNodes(
            GraphFixtures.Node("a", AutonomyLevel.FullyAutonomous));

        PolicyVerdict verdict = await JudgeAsync(
            new NoFullyAutonomousStageCheck(), new FakeRun(), graph);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("a");
    }

    [Fact]
    public async Task A_bounded_lifecycle_satisfies_the_autonomy_rule()
    {
        (await JudgeAsync(new NoFullyAutonomousStageCheck(), new FakeRun()))
            .Satisfied.ShouldBeTrue();
    }

    // ---- approvals ----

    [Fact]
    public async Task An_outstanding_approval_on_a_stage_that_ran_is_a_violation()
    {
        WorkflowGraph graph = GraphFixtures.WithNodes(GraphFixtures.Node(
            "a", approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]));

        FakeRun run = new FakeRun().WithNode("a", NodeState.AwaitingApproval, "a-agent");

        PolicyVerdict verdict = await JudgeAsync(new RequiredApprovalsHeldCheck(), run, graph);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("tech-lead");
    }

    [Fact]
    public async Task An_approval_on_a_stage_that_never_ran_is_not_demanded()
    {
        // A stage skipped on this path never needed its approval; demanding one would block
        // runs that took a legitimate branch.
        WorkflowGraph graph = GraphFixtures.WithNodes(GraphFixtures.Node(
            "a", approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]));

        FakeRun run = new FakeRun().WithNode("a", NodeState.Skipped);

        (await JudgeAsync(new RequiredApprovalsHeldCheck(), run, graph)).Satisfied.ShouldBeTrue();
    }

    [Fact]
    public async Task A_held_approval_satisfies_the_rule()
    {
        WorkflowGraph graph = GraphFixtures.WithNodes(GraphFixtures.Node(
            "a", approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]));

        FakeRun run = new FakeRun()
            .WithNode("a", NodeState.Succeeded, "a-agent")
            .WithApproval("tech-lead", Actor.Human("alex"));

        (await JudgeAsync(new RequiredApprovalsHeldCheck(), run, graph)).Satisfied.ShouldBeTrue();
    }

    // ---- segregation of duties ----

    [Fact]
    public async Task An_approval_by_the_run_initiator_is_a_violation()
    {
        WorkflowGraph graph = GraphFixtures.WithNodes(GraphFixtures.Node(
            "a", approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]));

        FakeRun run = new FakeRun()
            .WithNode("a", NodeState.Succeeded, "a-agent")
            .WithApproval("tech-lead", Actor.Human("muskan"))
            .WithFact(WorkflowContextKeys.InitiatedBy, "human:muskan");

        PolicyVerdict verdict = await JudgeAsync(new SegregationOfDutiesUpheldCheck(), run, graph);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("also requested the run");
    }

    [Fact]
    public async Task An_approval_by_an_independent_party_is_satisfied()
    {
        WorkflowGraph graph = GraphFixtures.WithNodes(GraphFixtures.Node(
            "a", approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]));

        FakeRun run = new FakeRun()
            .WithNode("a", NodeState.Succeeded, "a-agent")
            .WithApproval("tech-lead", Actor.Human("alex"))
            .WithFact(WorkflowContextKeys.InitiatedBy, "human:muskan");

        (await JudgeAsync(new SegregationOfDutiesUpheldCheck(), run, graph))
            .Satisfied.ShouldBeTrue();
    }

    // ---- tests ----

    [Fact]
    public async Task A_source_change_with_no_recorded_test_run_is_a_violation()
    {
        FakeRun run = new FakeRun().WithArtifact(ArtifactKind.SourcePatch, "patch.diff");

        PolicyVerdict verdict = await JudgeAsync(new TestsExecutedCheck(), run);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("untested change");
    }

    [Fact]
    public async Task A_recorded_failing_test_run_is_a_violation()
    {
        FakeRun run = new FakeRun()
            .WithArtifact(ArtifactKind.SourcePatch, "patch.diff")
            .WithArtifact(ArtifactKind.TestReport, "tests.json")
            .WithFact("test.failures", "3");

        (await JudgeAsync(new TestsExecutedCheck(), run)).Satisfied.ShouldBeFalse();
    }

    [Fact]
    public async Task A_recorded_passing_test_run_is_satisfied()
    {
        FakeRun run = new FakeRun()
            .WithArtifact(ArtifactKind.SourcePatch, "patch.diff")
            .WithArtifact(ArtifactKind.TestReport, "tests.json")
            .WithFact("test.failures", "0");

        (await JudgeAsync(new TestsExecutedCheck(), run)).Satisfied.ShouldBeTrue();
    }

    // ---- the workspace ----

    [Fact]
    public async Task An_unclean_tree_is_a_violation()
    {
        using ScratchWorkspace workspace = new(clean: false);

        PolicyVerdict verdict = await JudgeAsync(
            new WorkspaceIsCleanCheck(), new FakeRun(), workspace: workspace);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("not what any stage produced");
    }

    [Fact]
    public async Task A_clean_tree_is_satisfied()
    {
        using ScratchWorkspace workspace = new(clean: true);

        (await JudgeAsync(new WorkspaceIsCleanCheck(), new FakeRun(), workspace: workspace))
            .Satisfied.ShouldBeTrue();
    }

    // ---- secrets ----

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow==\n-----END RSA PRIVATE KEY-----")]
    [InlineData("const id = \"AKIAIOSFODNN7EXAMPLE\";")]
    [InlineData("api_key = \"sk_live_9f8a7b6c5d4e3f2a1b0c\"")]
    [InlineData("Server=db;Database=app;User=sa;Password=hunter2horse;")]
    public async Task Credential_material_in_the_tree_is_found(string content)
    {
        using ScratchWorkspace workspace = new();
        workspace.Write("src/Config.cs", content);

        PolicyVerdict verdict = await JudgeAsync(
            new NoSecretsInWorkspaceCheck(), new FakeRun(), workspace: workspace);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("src/Config.cs");
    }

    [Fact]
    public async Task Ordinary_source_does_not_trip_the_scanner()
    {
        using ScratchWorkspace workspace = new();
        workspace.Write("src/Shortener.cs", "public sealed class Shortener { }");

        (await JudgeAsync(new NoSecretsInWorkspaceCheck(), new FakeRun(), workspace: workspace))
            .Satisfied.ShouldBeTrue();
    }

    [Fact]
    public async Task A_scan_that_cannot_look_reports_a_violation_rather_than_nothing_found()
    {
        // "Nothing checked" and "nothing found" must never look the same.
        PolicyVerdict verdict = await new NoSecretsInWorkspaceCheck().EvaluateAsync(
            new PolicyCheckContext(
                Rule("no-secrets-in-workspace"),
                new FakeRun(),
                GraphFixtures.WithNodes(GraphFixtures.Node("a")),
                new AbsentWorkspaceStub()),
            CancellationToken.None);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Explanation.ShouldContain("cannot be asserted");
    }

    // ---- waivers ----

    [Fact]
    public async Task A_waiver_attaches_to_the_violation_rather_than_hiding_it()
    {
        // The rule is still evaluated and still reported; it simply stops blocking.
        FakeRun run = new FakeRun()
            .WithArtifact(ArtifactKind.DesignDoc, "design.md")
            .WithWaiver("TEST-001", by: "alex", reason: "Design predates the ADR requirement.");

        PolicyVerdict verdict = await JudgeAsync(new DesignHasDecisionRecordCheck(), run);

        verdict.Satisfied.ShouldBeFalse();
        verdict.IsWaived.ShouldBeTrue();
        verdict.Blocks.ShouldBeFalse();
        verdict.Waiver!.GrantedBy.ShouldBe(Actor.Human("alex"));
        verdict.Waiver.Reason.ShouldContain("predates");
    }

    [Fact]
    public async Task An_advisory_violation_does_not_block_and_needs_no_waiver()
    {
        FakeRun run = new FakeRun().WithArtifact(ArtifactKind.DesignDoc, "design.md");

        PolicyVerdict verdict = await JudgeAsync(
            new DesignHasDecisionRecordCheck(), run, severity: PolicySeverity.Advisory);

        verdict.Satisfied.ShouldBeFalse();
        verdict.Blocks.ShouldBeFalse();
        verdict.IsWaived.ShouldBeFalse();
    }

    private sealed class AbsentWorkspaceStub : Core.Execution.IRunWorkspace
    {
        public string Root => string.Empty;

        public Task<Core.Execution.WorkspaceCommit?> CommitAsync(
            NodeId nodeId, int attempt,
            IReadOnlyCollection<Core.Execution.WorkspaceFile> files,
            string message, CancellationToken cancellationToken) =>
            Task.FromResult<Core.Execution.WorkspaceCommit?>(null);

        public Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<Core.Execution.WorkspaceStatus> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Core.Execution.WorkspaceStatus(string.Empty, true, 0));

        public Task<System.Collections.Immutable.ImmutableArray<Core.Execution.WorkspaceCommit>>
            CommitsForAsync(NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(System.Collections.Immutable.ImmutableArray<Core.Execution.WorkspaceCommit>.Empty);
    }
}
