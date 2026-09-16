using System.Collections.Immutable;
using Mandate.Core.Identifiers;
using Mandate.Core.Tests.Support;
using Mandate.Core.Workflow;

namespace Mandate.Core.Tests.Workflow;

public sealed class WorkflowGraphTests
{
    private static NodeId Id(string value) => NodeId.Parse(value);

    [Fact]
    public void A_wellformed_lifecycle_builds()
    {
        WorkflowGraph graph = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        graph.Definition.Identity.ShouldBe("sdlc@v1");
        graph.TopologicalOrder.Length.ShouldBe(6);
    }

    [Fact]
    public void Dependencies_precede_dependents_in_the_topological_order()
    {
        ImmutableArray<NodeId> order = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn()).TopologicalOrder;

        order.IndexOf(Id("requirements")).ShouldBeLessThan(order.IndexOf(Id("implement")));
        order.IndexOf(Id("implement")).ShouldBeLessThan(order.IndexOf(Id("test")));
        order.IndexOf(Id("test")).ShouldBeLessThan(order.IndexOf(Id("release")));
    }

    [Fact]
    public void The_order_is_deterministic_so_replays_produce_identical_evidence()
    {
        WorkflowGraph first = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());
        WorkflowGraph second = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        second.TopologicalOrder.ShouldBe(first.TopologicalOrder);
    }

    [Fact]
    public void The_lifecycle_fans_out_and_synchronises_rather_than_running_as_a_chain()
    {
        // Directly asserts the brief's requirement for parallel paths with synchronisation.
        ImmutableArray<ImmutableArray<NodeId>> stages =
            WorkflowGraph.Build(WorkflowFactory.FanOutFanIn()).ParallelStages();

        stages.Length.ShouldBe(4);
        stages[0].Select(node => node.Value).ShouldBe(["requirements"]);
        stages[1].Select(node => node.Value).ShouldBe(["implement"]);
        stages[2].Select(node => node.Value).ShouldBe(["docs", "review", "test"]);
        stages[3].Select(node => node.Value).ShouldBe(["release"]);
    }

    [Fact]
    public void The_join_node_waits_on_every_parallel_path()
    {
        WorkflowGraph graph = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        graph.ForwardDependenciesOf(Id("release"))
            .Select(edge => edge.From.Value)
            .ShouldBe(["test", "review", "docs"], ignoreOrder: true);

        graph.Node(Id("release")).Join.ShouldBe(JoinPolicy.All);
    }

    [Fact]
    public void Entry_and_terminal_nodes_are_identified()
    {
        WorkflowGraph graph = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        graph.EntryNodes.Select(node => node.Value).ShouldBe(["requirements"]);
        graph.TerminalNodes.Select(node => node.Value).ShouldBe(["release"]);
    }

    [Fact]
    public void A_loop_back_edge_does_not_make_a_node_depend_on_its_own_dependent()
    {
        WorkflowGraph graph = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        graph.ForwardDependenciesOf(Id("implement")).Select(edge => edge.From.Value)
            .ShouldBe(["requirements"]);
        graph.LoopBacksFrom(Id("test")).Select(edge => edge.To.Value).ShouldBe(["implement"]);
    }

    [Fact]
    public void Transitive_dependents_give_the_re_plan_invalidation_set()
    {
        WorkflowGraph graph = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        graph.TransitiveDependentsOf(Id("implement"))
            .Select(node => node.Value)
            .ShouldBe(["test", "review", "docs", "release"], ignoreOrder: true);

        graph.TransitiveDependentsOf(Id("release")).ShouldBeEmpty();
    }

    [Fact]
    public void An_accidental_cycle_in_the_forward_graph_is_rejected()
    {
        WorkflowDefinition circular = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("b")],
            [WorkflowEdge.Forward(Id("a"), Id("b")), WorkflowEdge.Forward(Id("b"), Id("a"))]);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(circular));

        error.Errors.ShouldContain(issue => issue.Code == "WF030");
        error.Message.ShouldContain("loop-back");
    }

    [Fact]
    public void A_deliberate_loop_back_is_permitted_where_a_forward_cycle_is_not()
    {
        // This is the distinction that lets the lifecycle be non-linear without being able to
        // loop forever: loops are declared, and bounded by the target's retry budget.
        WorkflowDefinition looping = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("b")],
            [WorkflowEdge.Forward(Id("a"), Id("b")), WorkflowEdge.LoopBack(Id("b"), Id("a"))]);

        Should.NotThrow(() => WorkflowGraph.Build(looping));
    }

    [Fact]
    public void A_node_wired_into_no_forward_path_is_rejected()
    {
        // A loop-back alone does not put a node in the flow: it would run at the start of
        // every run and then jump backwards, which is never what was meant.
        WorkflowDefinition orphaned = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("b"), WorkflowFactory.Node("stranded")],
            [WorkflowEdge.Forward(Id("a"), Id("b")), WorkflowEdge.LoopBack(Id("stranded"), Id("a"))]);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(orphaned));

        error.Errors.ShouldContain(issue => issue.Code == "WF032" && issue.NodeId == Id("stranded"));
        error.Message.ShouldContain("loop-back alone does not connect");
    }

    [Fact]
    public void A_file_declaring_two_unrelated_lifecycles_is_rejected()
    {
        WorkflowDefinition split = WorkflowFactory.Definition(
            [
                WorkflowFactory.Node("a"), WorkflowFactory.Node("b"),
                WorkflowFactory.Node("x"), WorkflowFactory.Node("y"),
            ],
            [WorkflowEdge.Forward(Id("a"), Id("b")), WorkflowEdge.Forward(Id("x"), Id("y"))]);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(split));

        error.Errors.ShouldContain(issue => issue.Code == "WF033");
        error.Message.ShouldContain("converge on a single release decision");
    }

    [Fact]
    public void A_single_node_lifecycle_is_not_treated_as_disconnected()
    {
        Should.NotThrow(() => WorkflowGraph.Build(
            WorkflowFactory.Definition([WorkflowFactory.Node("only")], [])));
    }

    [Fact]
    public void A_workflow_with_nowhere_to_start_is_rejected()
    {
        WorkflowDefinition noEntry = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a")],
            [WorkflowEdge.Forward(Id("a"), Id("a"))]);

        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(noEntry))
            .Errors.ShouldContain(issue => issue.Code == "WF022" || issue.Code == "WF031");
    }

    [Theory]
    [InlineData("WF020")]
    [InlineData("WF021")]
    public void Edges_referencing_undeclared_nodes_are_rejected(string expectedCode)
    {
        WorkflowDefinition dangling = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a")],
            [
                WorkflowEdge.Forward(Id("a"), Id("missing-target")),
                WorkflowEdge.Forward(Id("missing-source"), Id("a")),
            ]);

        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(dangling))
            .Errors.ShouldContain(issue => issue.Code == expectedCode);
    }

    [Fact]
    public void A_duplicated_node_is_rejected()
    {
        WorkflowDefinition duplicated = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("a")],
            []);

        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(duplicated))
            .Errors.ShouldContain(issue => issue.Code == "WF004");
    }

    [Fact]
    public void A_node_without_a_declared_autonomy_level_is_rejected()
    {
        // Agents may not execute outside a stated boundary, so the omission has to be fatal.
        WorkflowDefinition unbounded = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a", autonomy: AutonomyLevel.Unknown)],
            []);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(unbounded));

        error.Errors.ShouldContain(issue => issue.Code == "WF007");
        error.Message.ShouldContain("outside a stated boundary");
    }

    [Fact]
    public void A_node_that_falls_back_to_compensation_must_declare_how_to_compensate()
    {
        WorkflowDefinition uncompensable = WorkflowFactory.Definition(
            [
                WorkflowFactory.Node(
                    "a",
                    retry: RetryPolicy.Default with { OnExhaustion = FallbackStrategy.Compensate },
                    compensation: null),
            ],
            []);

        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(uncompensable))
            .Errors.ShouldContain(issue => issue.Code == "WF009");
    }

    [Fact]
    public void A_node_with_no_timeout_is_rejected_because_an_attempt_could_hang()
    {
        WorkflowDefinition untimed = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a", timeout: TimeSpan.Zero)],
            []);

        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(untimed))
            .Errors.ShouldContain(issue => issue.Code == "WF010");
    }

    [Fact]
    public void A_quorum_join_without_a_size_is_rejected()
    {
        WorkflowDefinition vague = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a", join: JoinPolicy.Quorum, quorumSize: 0)],
            []);

        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(vague))
            .Errors.ShouldContain(issue => issue.Code == "WF011");
    }

    [Fact]
    public void A_fully_autonomous_node_that_also_requires_approval_is_a_contradiction()
    {
        WorkflowDefinition contradictory = WorkflowFactory.Definition(
            [
                WorkflowFactory.Node(
                    "a",
                    autonomy: AutonomyLevel.FullyAutonomous,
                    approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]),
            ],
            []);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(contradictory));

        error.Errors.ShouldContain(issue => issue.Code == "WF012");
        error.Message.ShouldContain("guessing which would misstate");
    }

    [Fact]
    public void A_missing_exit_gate_warns_but_does_not_block()
    {
        WorkflowDefinition ungated = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a", exitGate: [])],
            []);

        WorkflowGraph graph = WorkflowGraph.Build(ungated);

        graph.Warnings.ShouldContain(issue => issue.Code == "WF101");
    }

    [Fact]
    public void A_duplicated_edge_warns_but_does_not_block()
    {
        WorkflowDefinition duplicated = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("b")],
            [WorkflowEdge.Forward(Id("a"), Id("b")), WorkflowEdge.Forward(Id("a"), Id("b"))]);

        WorkflowGraph.Build(duplicated).Warnings.ShouldContain(issue => issue.Code == "WF102");
    }

    [Fact]
    public void An_empty_workflow_is_rejected()
    {
        Should.Throw<WorkflowValidationException>(
                () => WorkflowGraph.Build(WorkflowFactory.Definition([], [])))
            .Errors.ShouldContain(issue => issue.Code == "WF003");
    }

    [Fact]
    public void An_unversioned_workflow_is_rejected_because_runs_record_their_version()
    {
        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(
                WorkflowFactory.Definition([WorkflowFactory.Node("a")], [], version: " ")))
            .Errors.ShouldContain(issue => issue.Code == "WF002");
    }

    [Fact]
    public void Validation_reports_every_problem_so_one_pass_fixes_the_file()
    {
        WorkflowDefinition broken = WorkflowFactory.Definition(
            [
                WorkflowFactory.Node("a", autonomy: AutonomyLevel.Unknown, timeout: TimeSpan.Zero),
                WorkflowFactory.Node("a"),
            ],
            [WorkflowEdge.Forward(Id("a"), Id("nowhere"))],
            version: string.Empty);

        ImmutableArray<WorkflowIssue> issues = WorkflowGraph.Validate(broken);

        issues.Select(issue => issue.Code).Distinct().Count().ShouldBeGreaterThan(3);
    }

    [Fact]
    public void Looking_up_an_unknown_node_fails_loudly()
    {
        WorkflowGraph graph = WorkflowGraph.Build(WorkflowFactory.FanOutFanIn());

        graph.Contains(Id("nope")).ShouldBeFalse();
        Should.Throw<KeyNotFoundException>(() => graph.Node(Id("nope")));
    }

    [Fact]
    public void A_guarded_edge_with_a_valid_expression_is_accepted()
    {
        WorkflowDefinition guarded = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("impact-analysis")],
            [WorkflowEdge.Guarded(Id("a"), Id("impact-analysis"), "run.scenario == 'brownfield'")]);

        Should.NotThrow(() => WorkflowGraph.Build(guarded));
    }

    [Fact]
    public void A_guard_that_does_not_parse_is_rejected_at_load()
    {
        WorkflowDefinition broken = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a"), WorkflowFactory.Node("b")],
            [WorkflowEdge.Guarded(Id("a"), Id("b"), "run.scenario = 'brownfield'")]);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(broken));

        error.Errors.ShouldContain(issue => issue.Code == "WF024");
        error.Message.ShouldContain("Use '==' for equality");
    }

    [Fact]
    public void A_guard_reading_a_context_key_nothing_produces_is_rejected_at_load()
    {
        // Completes the fail-closed story: because guards refuse to evaluate an absent key,
        // the mistake has to be caught before a run exists rather than halting one midway.
        WorkflowDefinition mistyped = WorkflowFactory.Definition(
            [
                WorkflowFactory.Node("a", producesContext: ["requirements.scope"]),
                WorkflowFactory.Node("b"),
            ],
            [WorkflowEdge.Guarded(Id("a"), Id("b"), "requirements.scoop == 'wide'")]);

        WorkflowValidationException error =
            Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(mistyped));

        error.Errors.ShouldContain(issue => issue.Code == "WF034");
        error.Message.ShouldContain("requirements.scoop");
    }

    [Fact]
    public void Guards_may_read_the_run_level_facts_the_engine_supplies()
    {
        WorkflowDefinition guarded = WorkflowFactory.Definition(
            [WorkflowFactory.Node("a", producesContext: []), WorkflowFactory.Node("b")],
            [WorkflowEdge.Guarded(Id("a"), Id("b"), "run.has-existing-code == true")]);

        Should.NotThrow(() => WorkflowGraph.Build(guarded));
    }

    [Fact]
    public void A_guard_may_read_a_key_produced_by_any_node_not_only_its_source()
    {
        WorkflowDefinition guarded = WorkflowFactory.Definition(
            [
                WorkflowFactory.Node("a", producesContext: []),
                WorkflowFactory.Node("b", producesContext: ["test.coverage"]),
                WorkflowFactory.Node("c", producesContext: []),
            ],
            [
                WorkflowEdge.Forward(Id("a"), Id("b")),
                WorkflowEdge.Guarded(Id("b"), Id("c"), "test.coverage >= 0.8"),
            ]);

        Should.NotThrow(() => WorkflowGraph.Build(guarded));
    }
}
