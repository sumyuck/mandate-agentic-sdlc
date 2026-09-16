using System.Collections.Immutable;
using Mandate.Agents.Scripted;
using Mandate.Core.Artifacts;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Tests.Support;

namespace Mandate.Orchestrator.Tests.Execution;

/// <summary>
/// Re-planning: invalidating work whose premise changed, and only that work.
/// </summary>
public sealed class ReplanTests
{
    private static NodeId Id(string value) => NodeId.Parse(value);

    /// <summary>answer -(loop-back on success)-> interpret -> build.</summary>
    /// <param name="answerContributes">
    /// Whether the looping-back stage produces anything the stage it returns to can see. When
    /// it produces nothing, the re-run has identical inputs and must produce identical output.
    /// </param>
    private static WorkflowDefinition WithLoopBack(
        LoopBackTrigger trigger = LoopBackTrigger.OnSuccess,
        bool answerContributes = true) =>
        WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("interpret", producesContext: ["interpret.done"]),
                answerContributes
                    ? WorkflowFixtures.Node("answer", producesContext: ["answer.given"])
                    : WorkflowFixtures.Node(
                        "answer", exitGate: [], produces: [], producesContext: []),
                WorkflowFixtures.Node("build"),
            ],
            [
                WorkflowEdge.Forward(Id("interpret"), Id("answer")),
                WorkflowEdge.Forward(Id("answer"), Id("build")),
                WorkflowEdge.LoopBack(Id("answer"), Id("interpret"), trigger),
            ]);

    private static ImmutableArray<ReplanPayload> ReplansIn(EngineHarness harness) =>
        [
            .. harness.EventsOfKind(RunEventKind.ReplanPerformed)
                .Select(@event => @event.Payload<ReplanPayload>()),
        ];

    // ---- loop-backs ----

    [Fact]
    public async Task A_loop_back_on_success_returns_control_when_the_stage_succeeds()
    {
        EngineHarness harness = EngineHarness.For(WithLoopBack());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);

        ImmutableArray<ReplanPayload> replans = ReplansIn(harness);

        replans.ShouldNotBeEmpty();
        replans[0].Trigger.ShouldBe("answer");
        replans[0].Invalidated.ShouldContain("interpret");
    }

    [Fact]
    public async Task A_loop_back_on_failure_does_not_fire_when_the_stage_succeeds()
    {
        // The distinction the trigger exists for: a loop-back that fired on success where
        // failure was meant would redo finished work every time it worked.
        EngineHarness harness = EngineHarness.For(WithLoopBack(LoopBackTrigger.OnFailure));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);
        ReplansIn(harness).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_loop_back_on_failure_redoes_the_upstream_stage_when_the_check_fails()
    {
        // The stage did its job and reported that the work it was checking does not hold, so
        // that work is redone rather than this stage being retried again.
        EngineHarness harness = EngineHarness.For(
            WithLoopBack(LoopBackTrigger.OnFailure),
            behaviours: new Dictionary<string, ScriptedBehaviour>(StringComparer.Ordinal)
            {
                ["answer-agent"] = ScriptedBehaviour.AlwaysFails("the change does not hold"),
            });

        await harness.RunAsync();

        ImmutableArray<ReplanPayload> replans = ReplansIn(harness);

        replans.ShouldNotBeEmpty();
        replans[0].Invalidated.ShouldContain("interpret");
        replans[0].Reason.ShouldContain("does not hold");
    }

    [Fact]
    public async Task Re_planning_is_bounded_so_a_declared_loop_cannot_become_an_unbounded_one()
    {
        // A lifecycle that re-plans without limit is not adaptive, it is stuck — and from the
        // outside the failure looks like progress.
        EngineHarness harness = EngineHarness.For(
            WithLoopBack(),
            options: new EngineOptions(MaxConcurrency: 1, MaxReplans: 2));

        RunOutcome outcome = await harness.RunAsync();

        ReplansIn(harness).Length.ShouldBeLessThanOrEqualTo(2);
        outcome.State.ReplanCount.ShouldBeLessThanOrEqualTo(2);
    }

    // ---- incremental invalidation ----

    [Fact]
    public async Task A_re_run_that_changes_nothing_leaves_downstream_work_alone()
    {
        // The whole point of "incremental". The upstream stage is redone, produces
        // byte-identical output, and nothing built on it is disturbed.
        EngineHarness harness = EngineHarness.For(WithLoopBack(answerContributes: false));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);

        // 'interpret' was redone by the loop-back, but 'build' was never invalidated: the
        // re-run saw the same inputs and produced the same bytes, so nothing built on it
        // needed redoing.
        harness.EventsOfKind(RunEventKind.NodeInvalidated)
            .Select(@event => @event.NodeId!.Value.Value)
            .ShouldNotContain("build");
    }

    [Fact]
    public async Task A_re_run_that_changes_its_output_invalidates_what_was_built_on_it()
    {
        EngineHarness harness = EngineHarness.For(WithLoopBack(answerContributes: true));

        await harness.RunAsync();

        ImmutableArray<ReplanPayload> replans = ReplansIn(harness);

        // The loop-back re-plan, then the cascade once the re-run produced different output.
        replans.Length.ShouldBeGreaterThanOrEqualTo(2);
        replans.Any(replan => replan.Invalidated.Contains("build")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_re_plan_records_what_it_left_alone_as_well_as_what_it_undid()
    {
        EngineHarness harness = EngineHarness.For(WithLoopBack());

        await harness.RunAsync();

        ReplanPayload first = ReplansIn(harness)[0];

        first.Invalidated.ShouldNotBeEmpty();
        first.Unaffected.ShouldNotBeEmpty();
        first.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- governance ----

    [Fact]
    public async Task Invalidating_an_approved_stage_withdraws_its_approval()
    {
        // A signature was given for work that is now being redone. Carrying it forward would
        // put a human's name against output they never saw.
        WorkflowDefinition gated = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("interpret", producesContext: ["interpret.done"]),
                WorkflowFixtures.Node(
                    "design",
                    exitGate:
                    [
                        new GateCondition("artifact-exists", "source-patch", "Produced."),
                        new GateCondition("approval-held", "tech-lead", "Signed off."),
                    ],
                    approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]),
            ],
            [WorkflowEdge.Forward(Id("interpret"), Id("design"))]);

        EngineHarness harness = EngineHarness.For(gated);

        await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: true);
        RunOutcome approved = await harness.ResumeAsync();

        EngineHarness.StateOf(approved, "design").ShouldBe(NodeState.Succeeded);
        approved.State.HeldApprovals.ShouldContainKey("tech-lead");

        await harness.AmendAsync("interpret", "muskan", "The requirement changed.");
        RunOutcome amended = await harness.ResumeAsync();

        amended.State.HeldApprovals.ShouldNotContainKey("tech-lead");
        EngineHarness.StateOf(amended, "design").ShouldBe(NodeState.AwaitingApproval);
    }

    [Fact]
    public async Task A_withdrawn_approval_is_recorded_against_the_node_that_lost_it()
    {
        WorkflowDefinition gated = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("interpret", producesContext: ["interpret.done"]),
                WorkflowFixtures.Node(
                    "design",
                    exitGate:
                    [
                        new GateCondition("artifact-exists", "source-patch", "Produced."),
                        new GateCondition("approval-held", "tech-lead", "Signed off."),
                    ],
                    approvals: [new ApprovalRequirement("tech-lead", "High impact.", true)]),
            ],
            [WorkflowEdge.Forward(Id("interpret"), Id("design"))]);

        EngineHarness harness = EngineHarness.For(gated);

        await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: true);
        await harness.ResumeAsync();
        await harness.AmendAsync("interpret", "muskan", "The requirement changed.");
        await harness.ResumeAsync();

        NodeInvalidatedPayload payload = harness
            .EventsOfKind(RunEventKind.NodeInvalidated)
            .Where(@event => @event.NodeId == Id("design"))
            .Select(@event => @event.Payload<NodeInvalidatedPayload>())
            .Last();

        payload.RevokedApprovals.ShouldContain("tech-lead");
    }

    // ---- amendments ----

    [Fact]
    public async Task An_amendment_invalidates_only_the_stage_it_names()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.Definition(
                [
                    WorkflowFixtures.Node("interpret", producesContext: ["interpret.done"]),
                    WorkflowFixtures.Node("build"),
                ],
                [WorkflowEdge.Forward(Id("interpret"), Id("build"))]));

        await harness.RunAsync();
        await harness.AmendAsync("interpret", "muskan", "Scope changed.");
        await harness.ResumeAsync();

        ReplanPayload first = ReplansIn(harness)[0];

        first.Invalidated.ShouldBe(["interpret"]);
        first.Unaffected.ShouldContain("build");
    }

    [Fact]
    public async Task An_amendment_is_visible_to_the_stage_being_redone()
    {
        // Redoing a stage while withholding the reason it is being redone would produce the
        // same output and make the exercise pointless.
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.Definition([WorkflowFixtures.Node("interpret")], []));

        await harness.RunAsync();
        await harness.AmendAsync("interpret", "muskan", "Expiry means a TTL.");
        RunOutcome resumed = await harness.ResumeAsync();

        resumed.State.Context.Latest(WorkflowContextKeys.Amendment)!.Value
            .ShouldBe("Expiry means a TTL.");
    }

    [Fact]
    public async Task Applying_an_amendment_twice_does_not_redo_the_work_twice()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.Definition([WorkflowFixtures.Node("interpret")], []));

        await harness.RunAsync();
        await harness.AmendAsync("interpret", "muskan", "Scope changed.");
        await harness.ResumeAsync();
        await harness.ResumeAsync();

        // Only one human-initiated re-plan, however many times the run is resumed. Engine
        // cascades that follow from it are a separate matter and are counted separately.
        harness.EventsOfKind(RunEventKind.ReplanPerformed)
            .Count(@event => @event.Actor.Kind == ActorKind.Human)
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_chain_stays_intact_across_re_planning()
    {
        EngineHarness harness = EngineHarness.For(WithLoopBack());

        RunOutcome outcome = await harness.RunAsync();

        AuditChain.Verify(outcome.RunId, harness.Journal.Events).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task A_re_planned_run_can_still_be_rebuilt_from_its_log()
    {
        EngineHarness harness = EngineHarness.For(WithLoopBack());

        RunOutcome outcome = await harness.RunAsync();
        RunState rebuilt = RunState.Rebuild(outcome.RunId, harness.Journal.Events);

        rebuilt.ReplanCount.ShouldBe(outcome.State.ReplanCount);

        foreach ((NodeId id, NodeExecutionState node) in outcome.State.Nodes)
        {
            rebuilt.StateOf(id).ShouldBe(node.State, $"node '{id}' differs after rebuild.");
        }
    }
}
