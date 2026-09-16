using System.Collections.Immutable;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;
using Mandate.Workflows.Tests.Support;

namespace Mandate.Workflows.Tests;

/// <summary>
/// Guards the lifecycle this system actually ships, <c>workflows/sdlc.v1.yaml</c>.
/// </summary>
/// <remarks>
/// The workflow is configuration, so nothing in the compiler stops someone weakening it —
/// dropping an approval, flattening the parallel section, raising an autonomy level. These
/// tests are the change control on that file: each one states a property of the lifecycle
/// that must not regress silently.
/// </remarks>
public sealed class ShippedWorkflowTests
{
    private static readonly WorkflowGraph Graph =
        WorkflowYamlLoader.LoadGraph(Repository.ShippedWorkflow);

    private static NodeId Id(string value) => NodeId.Parse(value);

    [Fact]
    public void It_loads_and_is_executable() => Graph.Definition.Identity.ShouldBe("sdlc@v1");

    [Fact]
    public void It_has_no_validation_warnings()
    {
        // Every stage gates its own output. A warning here means some stage's result is
        // accepted unchecked.
        Graph.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void It_covers_every_lifecycle_stage_the_brief_names()
    {
        IEnumerable<SdlcStage> covered = Graph.Nodes.Select(node => node.Stage).Distinct();

        covered.ShouldBe(
            [
                SdlcStage.Intake, SdlcStage.Requirements, SdlcStage.ImpactAnalysis,
                SdlcStage.Architecture, SdlcStage.Implementation, SdlcStage.Testing,
                SdlcStage.CodeReview, SdlcStage.SecurityScan, SdlcStage.Documentation,
                SdlcStage.ReleaseReadiness,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Work_fans_out_four_ways_and_synchronises_before_any_release_decision()
    {
        ImmutableArray<WorkflowEdge> fanOut = Graph.ForwardDependentsOf(Id("implement"));

        fanOut.Select(edge => edge.To.Value)
            .ShouldBe(["test", "code-review", "security-scan", "documentation"], ignoreOrder: true);

        Graph.ForwardDependenciesOf(Id("release-readiness"))
            .Select(edge => edge.From.Value)
            .ShouldBe(["test", "code-review", "security-scan", "documentation"], ignoreOrder: true);

        Graph.Node(Id("release-readiness")).Join.ShouldBe(JoinPolicy.All);
    }

    [Fact]
    public void The_design_stage_joins_on_any_because_its_two_inbound_paths_are_exclusive()
    {
        // Greenfield arrives from requirements directly, brownfield by way of impact
        // analysis. An 'all' join would wait forever for the path the guard excluded.
        Graph.Node(Id("architecture")).Join.ShouldBe(JoinPolicy.Any);
    }

    [Fact]
    public void Impact_analysis_runs_only_when_there_is_existing_code()
    {
        WorkflowEdge edge = Graph.ForwardDependenciesOf(Id("impact-analysis")).ShouldHaveSingleItem();

        edge.From.ShouldBe(Id("requirements"));
        edge.Guard.ShouldBe("run.has-existing-code == true");
    }

    [Fact]
    public void Ambiguous_requirements_divert_to_a_human_and_return()
    {
        WorkflowEdge toClarification =
            Graph.ForwardDependenciesOf(Id("clarification")).ShouldHaveSingleItem();

        toClarification.Guard.ShouldBe("requirements.ambiguity-score > 0.3");

        Graph.LoopBacksFrom(Id("clarification"))
            .Select(edge => edge.To.Value)
            .ShouldBe(["requirements"]);
    }

    [Fact]
    public void Failing_tests_return_to_implementation_rather_than_failing_the_run()
    {
        Graph.LoopBacksFrom(Id("test")).Select(edge => edge.To.Value).ShouldBe(["implement"]);
    }

    [Fact]
    public void Every_loop_back_states_which_outcome_sends_control_back()
    {
        // A loop-back that fired on success where failure was meant would redo finished work
        // every time it worked, which looks like progress rather than a defect.
        foreach (WorkflowEdge loop in Graph.Definition.Edges.Where(
                     edge => edge.Kind == EdgeKind.LoopBack))
        {
            loop.On.ShouldNotBe(
                LoopBackTrigger.Unknown, $"'{loop.From}' -> '{loop.To}' has no trigger.");
        }
    }

    [Fact]
    public void The_clarification_returns_on_success_and_the_test_returns_on_failure()
    {
        // The two loop-backs exist for opposite reasons: a clarification returns when the
        // answer is ready, a test returns when the code it was checking does not hold.
        Graph.LoopBacksFrom(Id("clarification")).ShouldAllBe(
            edge => edge.On == LoopBackTrigger.OnSuccess);

        Graph.LoopBacksFrom(Id("test")).ShouldAllBe(
            edge => edge.On == LoopBackTrigger.OnFailure);
    }

    [Fact]
    public void Every_loop_back_targets_a_node_with_a_bounded_retry_budget()
    {
        // This is what stops a declared loop from being an unbounded one.
        foreach (WorkflowEdge loop in Graph.Definition.Edges.Where(edge => edge.Kind == EdgeKind.LoopBack))
        {
            WorkflowNode target = Graph.Node(loop.To);

            target.Retry.MaxAttempts.ShouldBeGreaterThan(1,
                $"loop-back into '{target.Id}' must be bounded by a retry budget.");
        }
    }

    [Fact]
    public void The_two_irreversible_decisions_require_a_named_human()
    {
        // The design everything is built on, and the release itself.
        Graph.Node(Id("architecture")).Approvals
            .Select(approval => approval.Role).ShouldContain("tech-lead");

        Graph.Node(Id("release-readiness")).Approvals
            .Select(approval => approval.Role).ShouldContain("release-approver");
    }

    [Fact]
    public void The_irreversible_decisions_enforce_segregation_of_duties()
    {
        // The person who asks for the work is not the person who signs it off.
        foreach (string gated in new[] { "architecture", "release-readiness" })
        {
            Graph.Node(Id(gated)).Approvals
                .ShouldAllBe(approval => approval.SegregationOfDuties);
        }
    }

    [Fact]
    public void The_clarification_is_deliberately_not_segregated()
    {
        // The question is being put back to the person who asked for the work, so they are
        // precisely the right person to answer it. Requiring someone else would make the
        // stage unanswerable, which is a worse failure than the one segregation prevents.
        Graph.Node(Id("clarification")).Approvals
            .ShouldAllBe(approval => !approval.SegregationOfDuties);
    }

    [Fact]
    public void Every_approval_states_why_it_is_needed()
    {
        foreach (WorkflowNode node in Graph.Nodes.Where(node => node.RequiresApproval))
        {
            foreach (ApprovalRequirement approval in node.Approvals)
            {
                approval.Reason.ShouldNotBeNullOrWhiteSpace(
                    $"'{node.Id}' asks for a signature without saying why.");
            }
        }
    }

    [Fact]
    public void The_release_decision_carries_the_lowest_autonomy_in_the_lifecycle()
    {
        Graph.Node(Id("release-readiness")).Autonomy.ShouldBe(AutonomyLevel.ProposeOnly);
    }

    [Fact]
    public void No_stage_is_fully_autonomous()
    {
        // L3 is representable in the model and deliberately unassigned; saying so explicitly
        // is more honest than omitting the level and implying it was never considered.
        Graph.Nodes.ShouldNotContain(node => node.Autonomy == AutonomyLevel.FullyAutonomous);
    }

    [Fact]
    public void Review_is_performed_by_a_different_agent_than_implementation()
    {
        // Segregation of duties is only enforceable if the actors genuinely differ.
        Graph.Node(Id("code-review")).Agent
            .ShouldNotBe(Graph.Node(Id("implement")).Agent);
    }

    [Fact]
    public void The_release_gate_depends_on_executed_results_not_on_claims()
    {
        IEnumerable<string> entryKinds =
            Graph.Node(Id("release-readiness")).EntryGate.Select(gate => gate.Kind);

        entryKinds.ShouldContain("tests-pass");
        entryKinds.ShouldContain("no-secrets-committed");
        entryKinds.ShouldContain("policy-clean");
    }

    [Fact]
    public void The_test_stage_requires_coverage_as_well_as_passing_tests()
    {
        // Passing tests that exercise nothing are not evidence.
        IEnumerable<string> exitKinds = Graph.Node(Id("test")).ExitGate.Select(gate => gate.Kind);

        exitKinds.ShouldContain("tests-pass");
        exitKinds.ShouldContain("coverage-at-least");
    }

    [Fact]
    public void Implementation_cannot_begin_before_the_design_is_approved()
    {
        Graph.Node(Id("implement")).EntryGate
            .ShouldContain(gate => gate.Kind == "approval-held" && gate.Expression == "tech-lead");
    }

    [Fact]
    public void Every_stage_that_writes_to_the_workspace_can_be_undone()
    {
        foreach (WorkflowNode node in Graph.Nodes.Where(node =>
                     node.Retry.OnExhaustion == FallbackStrategy.Compensate))
        {
            node.IsCompensable.ShouldBeTrue(
                $"'{node.Id}' falls back to compensation but declares no compensating action.");
        }

        Graph.Node(Id("implement")).Compensation.ShouldBe("revert-node-commit");
    }

    [Fact]
    public void Every_stage_has_a_positive_timeout_and_a_described_purpose()
    {
        foreach (WorkflowNode node in Graph.Nodes)
        {
            node.Timeout.ShouldBeGreaterThan(TimeSpan.Zero);
            node.Description.ShouldNotBeNullOrWhiteSpace($"'{node.Id}' has no description.");
        }
    }

    [Fact]
    public void Every_agent_stage_names_the_model_that_will_run_it()
    {
        // Model provenance is part of the audit evidence, so it cannot be left implicit.
        foreach (WorkflowNode node in Graph.Nodes)
        {
            node.Model.ShouldNotBeNullOrWhiteSpace($"'{node.Id}' does not resolve a model.");
        }
    }

    [Fact]
    public void Capability_is_matched_to_the_risk_of_the_stage()
    {
        // Judgment-heavy stages get the stronger model; mechanical ones do not.
        foreach (string judgment in new[] { "requirements", "impact-analysis", "architecture", "implement" })
        {
            Graph.Node(Id(judgment)).Model.ShouldBe("claude-sonnet-5");
        }

        foreach (string mechanical in new[] { "test", "security-scan", "documentation" })
        {
            Graph.Node(Id(mechanical)).Model.ShouldBe("claude-haiku-4-5");
        }
    }

    [Fact]
    public void The_lifecycle_starts_at_intake_and_ends_at_the_release_decision()
    {
        Graph.EntryNodes.Select(node => node.Value).ShouldBe(["intake"]);
        Graph.TerminalNodes.Select(node => node.Value)
            .ShouldBe(["clarification", "release-readiness"], ignoreOrder: true);
    }

    [Fact]
    public void Every_evidence_backed_gate_has_a_stage_that_produces_its_evidence()
    {
        // Gates fail closed, so a gate reading a context key no stage declares would always
        // fail - for want of data rather than because anything was wrong. That is correct
        // behaviour and a useless check, so the workflow must not contain one.
        Dictionary<string, string> evidenceFor = new(StringComparer.Ordinal)
        {
            ["tests-pass"] = "test.failures",
            ["coverage-at-least"] = "test.coverage",
            ["no-findings-above"] = "review.highest-severity",
            ["no-secrets-committed"] = "security.secrets-found",
            ["workspace-builds"] = "implementation.builds",
            ["ambiguity-below"] = "requirements.ambiguity-score",
        };

        HashSet<string> produced =
        [
            .. Graph.Nodes.SelectMany(node => node.ProducesContext),
        ];

        List<string> missing = [];

        foreach (WorkflowNode node in Graph.Nodes)
        {
            foreach (GateCondition condition in node.EntryGate.Concat(node.ExitGate))
            {
                if (evidenceFor.TryGetValue(condition.Kind, out string? key)
                    && !produced.Contains(key))
                {
                    missing.Add($"'{node.Id}' gate '{condition.Kind}' reads '{key}'");
                }
            }
        }

        missing.ShouldBeEmpty(
            "these gates read evidence no stage declares it produces: "
            + string.Join("; ", missing));
    }
}
