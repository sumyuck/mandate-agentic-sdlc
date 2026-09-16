using System.Collections.Immutable;
using Mandate.Agents.Scripted;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Tests.Support;

namespace Mandate.Orchestrator.Tests.Execution;

/// <summary>
/// The scheduler's contract: how it walks the graph, honours join policy and conditional
/// paths, and applies gates.
/// </summary>
public sealed class SchedulerTests
{
    private static NodeId Id(string value) => NodeId.Parse(value);

    // a -> b, with no gates that need evidence beyond what the stages produce.
    private static WorkflowDefinition Chain() => WorkflowFixtures.Definition(
        [WorkflowFixtures.Node("a"), WorkflowFixtures.Node("b")],
        [WorkflowEdge.Forward(Id("a"), Id("b"))]);

    [Fact]
    public async Task A_simple_chain_runs_to_completion()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);
        EngineHarness.StateOf(outcome, "a").ShouldBe(NodeState.Succeeded);
        EngineHarness.StateOf(outcome, "b").ShouldBe(NodeState.Succeeded);
    }

    [Fact]
    public async Task Dependencies_run_before_their_dependents()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        await harness.RunAsync();

        ImmutableArray<RunEvent> attempts = harness.EventsOfKind(RunEventKind.NodeAttemptStarted);

        attempts.Select(@event => @event.NodeId!.Value.Value).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Every_run_records_an_intact_audit_chain()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        RunOutcome outcome = await harness.RunAsync();

        AuditChain.Verify(outcome.RunId, harness.Journal.Events).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task The_run_can_be_rebuilt_from_its_log_alone()
    {
        // The property the whole design rests on: live execution and replay go through the
        // same reducer, so a rebuilt run cannot disagree with the one that was executed.
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.FanOutFanIn());

        RunOutcome outcome = await harness.RunAsync();
        RunState rebuilt = RunState.Rebuild(outcome.RunId, harness.Journal.Events);

        rebuilt.Status.ShouldBe(outcome.State.Status);
        rebuilt.LastSequence.ShouldBe(outcome.State.LastSequence);
        rebuilt.Artifacts.Length.ShouldBe(outcome.State.Artifacts.Length);
        rebuilt.Context.Count.ShouldBe(outcome.State.Context.Count);

        foreach ((NodeId id, NodeExecutionState node) in outcome.State.Nodes)
        {
            rebuilt.StateOf(id).ShouldBe(node.State, $"node '{id}' differs after rebuild.");
        }
    }

    // ---- join policy ----

    [Fact]
    public async Task A_join_on_all_waits_for_every_inbound_path()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.FanOutFanIn());

        await harness.RunAsync();

        ImmutableArray<string> order =
        [
            .. harness.EventsOfKind(RunEventKind.NodeAttemptStarted)
                .Select(@event => @event.NodeId!.Value.Value),
        ];

        int join = order.IndexOf("join");

        foreach (string branch in new[] { "left", "right" })
        {
            order.IndexOf(branch).ShouldBeLessThan(join, $"'{branch}' must precede the join.");
        }
    }

    [Fact]
    public async Task A_join_on_any_proceeds_on_the_first_satisfied_path()
    {
        // The design stage's situation: two mutually exclusive inbound paths, exactly one
        // taken. An 'all' join would wait forever for the path the guard excluded.
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.ExclusiveBranches());

        RunOutcome outcome = await harness.RunAsync(hasExistingCode: true);

        outcome.Status.ShouldBe(RunStatus.Succeeded);
        EngineHarness.StateOf(outcome, "via-analysis").ShouldBe(NodeState.Succeeded);
        EngineHarness.StateOf(outcome, "direct").ShouldBe(NodeState.Skipped);
        EngineHarness.StateOf(outcome, "design").ShouldBe(NodeState.Succeeded);
    }

    [Fact]
    public async Task A_join_on_all_is_skipped_when_an_inbound_path_is_excluded()
    {
        // Rather than waiting forever on a path that will never carry control.
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.GuardedChain());

        RunOutcome outcome = await harness.RunAsync(hasExistingCode: false);

        EngineHarness.StateOf(outcome, "conditional").ShouldBe(NodeState.Skipped);
        EngineHarness.StateOf(outcome, "after").ShouldBe(NodeState.Skipped);
        outcome.Status.ShouldBe(RunStatus.Succeeded);
    }

    [Fact]
    public async Task Exclusion_propagates_so_one_skipped_branch_cannot_stall_the_graph()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.GuardedChain());

        RunOutcome outcome = await harness.RunAsync(hasExistingCode: false);

        outcome.State.Nodes.Values.ShouldNotContain(node => node.State == NodeState.Pending);
    }

    // ---- guards ----

    [Fact]
    public async Task A_guard_is_evaluated_once_and_its_reasoning_recorded()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.GuardedChain());

        await harness.RunAsync(hasExistingCode: true);

        ImmutableArray<RunEvent> guards = harness.EventsOfKind(RunEventKind.EdgeGuardEvaluated);

        guards.Length.ShouldBe(1);

        EdgeGuardEvaluatedPayload payload = guards[0].Payload<EdgeGuardEvaluatedPayload>();
        payload.Taken.ShouldBeTrue();
        payload.Guard.ShouldBe("run.has-existing-code == true");
        payload.Explanation.ShouldContain("run.has-existing-code");
    }

    [Fact]
    public async Task A_path_not_taken_is_explained_in_the_log_rather_than_simply_absent()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.GuardedChain());

        await harness.RunAsync(hasExistingCode: false);

        EdgeGuardEvaluatedPayload payload = harness
            .EventsOfKind(RunEventKind.EdgeGuardEvaluated)
            .Single()
            .Payload<EdgeGuardEvaluatedPayload>();

        payload.Taken.ShouldBeFalse();
        payload.Explanation.ShouldContain("false");
    }

    // ---- gates ----

    [Fact]
    public async Task An_unmet_entry_gate_leaves_the_node_pending_then_blocks_it_at_quiescence()
    {
        // An entry gate is a precondition, so failing it means "not yet". Only once the run can
        // make no further progress does it become a block needing a human.
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.ImpossibleEntryGate());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Blocked);
        EngineHarness.StateOf(outcome, "gated").ShouldBe(NodeState.Blocked);
        outcome.Reason.ShouldContain("No evidence");
    }

    [Fact]
    public async Task An_unmet_entry_gate_is_recorded_once_not_on_every_scheduling_pass()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.ImpossibleEntryGate());

        await harness.RunAsync();

        harness.EventsOfKind(RunEventKind.EntryGateEvaluated)
            .Count(@event => @event.NodeId == Id("gated"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_exit_gate_fails_the_node_and_the_run()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.CoverageGated(),
            behaviours: new Dictionary<string, ScriptedBehaviour>(StringComparer.Ordinal)
            {
                ["tester-agent"] = ScriptedBehaviour.Reporting(("test.coverage", "0.20")),
            });

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Failed);
        EngineHarness.StateOf(outcome, "tester").ShouldBe(NodeState.Failed);
        outcome.Reason.ShouldContain("threshold");
    }

    [Fact]
    public async Task Gate_verdicts_are_recorded_with_the_evidence_behind_them()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.CoverageGated());

        await harness.RunAsync();

        GateEvaluatedPayload payload = harness
            .EventsOfKind(RunEventKind.ExitGateEvaluated)
            .Select(@event => @event.Payload<GateEvaluatedPayload>())
            .First(gate => gate.Conditions.Any(condition => condition.Kind == "coverage-at-least"));

        payload.Passed.ShouldBeTrue();
        payload.Conditions
            .First(condition => condition.Kind == "coverage-at-least")
            .Explanation.ShouldContain("test.coverage");
    }

    // ---- approvals ----

    [Fact]
    public async Task A_gate_failing_only_on_approval_parks_the_node_rather_than_failing_it()
    {
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.RequiresApproval());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.AwaitingApproval);
        EngineHarness.StateOf(outcome, "signed-off").ShouldBe(NodeState.AwaitingApproval);
        outcome.IsWaitingOnHuman.ShouldBeTrue();
        outcome.Reason.ShouldContain("complete and preserved");
    }

    [Fact]
    public async Task An_approval_request_records_who_produced_the_work()
    {
        // So the segregation-of-duties check does not depend on anyone's memory.
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.RequiresApproval());

        await harness.RunAsync();

        ApprovalRequestedPayload payload = harness
            .EventsOfKind(RunEventKind.ApprovalRequested)
            .Single()
            .Payload<ApprovalRequestedPayload>();

        payload.Role.ShouldBe("tech-lead");
        payload.SegregationOfDuties.ShouldBeTrue();
        payload.ProducedBy.ShouldBe("agent:signed-off-agent");
    }

    // ---- failures ----

    [Fact]
    public async Task A_failing_stage_fails_the_node_with_its_reason()
    {
        EngineHarness harness = EngineHarness.For(
            Chain(),
            behaviours: new Dictionary<string, ScriptedBehaviour>(StringComparer.Ordinal)
            {
                ["a-agent"] = ScriptedBehaviour.AlwaysFails("the model refused"),
            });

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Failed);
        EngineHarness.StateOf(outcome, "a").ShouldBe(NodeState.Failed);
        outcome.Reason.ShouldContain("the model refused");
    }

    [Fact]
    public async Task A_stage_that_throws_is_a_failed_stage_not_a_failed_engine()
    {
        // Keeping an exception inside the lifecycle's own recovery machinery is what lets
        // retries, fallbacks and compensation apply to it.
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.ThrowingStage(),
            extraAgents: [new ThrowingStageAgent()]);

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Reason.ShouldContain("InvalidOperationException");
    }

    [Fact]
    public async Task Downstream_work_does_not_start_after_an_upstream_failure()
    {
        EngineHarness harness = EngineHarness.For(
            Chain(),
            behaviours: new Dictionary<string, ScriptedBehaviour>(StringComparer.Ordinal)
            {
                ["a-agent"] = ScriptedBehaviour.AlwaysFails("nope"),
            });

        RunOutcome outcome = await harness.RunAsync();

        EngineHarness.StateOf(outcome, "b").ShouldBe(NodeState.Pending);
        harness.EventsOfKind(RunEventKind.NodeAttemptStarted)
            .ShouldNotContain(@event => @event.NodeId == Id("b"));
    }

    // ---- provenance and least privilege ----

    [Fact]
    public async Task Downstream_artifacts_are_derived_from_upstream_ones()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        RunOutcome outcome = await harness.RunAsync();

        Core.Artifacts.Artifact downstream = outcome.State.Artifacts
            .Single(artifact => artifact.ProducedByNode == Id("b"));

        Core.Artifacts.Artifact upstream = outcome.State.Artifacts
            .Single(artifact => artifact.ProducedByNode == Id("a"));

        downstream.DerivedFrom.ShouldContain(upstream.Hash);
    }

    [Fact]
    public async Task A_stage_sees_only_the_context_the_work_it_depends_on_produced()
    {
        // Least privilege derived from the declared graph: 'right' must not be able to read
        // what 'left' contributed, because it does not depend on it.
        EngineHarness harness = EngineHarness.For(WorkflowFixtures.FanOutFanIn());

        await harness.RunAsync();

        harness.Journal.Events
            .Where(@event => @event.Kind == RunEventKind.ContextFactAdded)
            .Select(@event => @event.Payload<ContextFactAddedPayload>().Key)
            .ShouldContain("left.done");
    }

    // ---- concurrency ----

    [Fact]
    public async Task Independent_stages_execute_concurrently()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.FanOutFanIn(),
            behaviours: new Dictionary<string, ScriptedBehaviour>(StringComparer.Ordinal)
            {
                ["left-agent"] = new ScriptedBehaviour(Delay: TimeSpan.FromMilliseconds(150)),
                ["right-agent"] = new ScriptedBehaviour(Delay: TimeSpan.FromMilliseconds(150)),
            },
            options: new EngineOptions(MaxConcurrency: 2));

        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        RunOutcome outcome = await harness.RunAsync();
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks);

        outcome.Status.ShouldBe(RunStatus.Succeeded);

        // Sequentially this would take at least 300ms; concurrently it should take far less.
        elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(280));
    }

    [Fact]
    public async Task Concurrency_is_bounded_by_the_configured_limit()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.FanOutFanIn(),
            behaviours: new Dictionary<string, ScriptedBehaviour>(StringComparer.Ordinal)
            {
                ["left-agent"] = new ScriptedBehaviour(Delay: TimeSpan.FromMilliseconds(120)),
                ["right-agent"] = new ScriptedBehaviour(Delay: TimeSpan.FromMilliseconds(120)),
            },
            options: EngineOptions.Sequential);

        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        await harness.RunAsync();
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks);

        // With a limit of one, the two delayed stages cannot overlap.
        elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(220));
    }

    [Fact]
    public async Task Concurrent_appends_keep_the_hash_chain_intact()
    {
        // The chain requires each event to commit to its immediate predecessor, so parallel
        // stages appending at once is exactly where it would break if appends interleaved.
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.WideFanOut(),
            options: new EngineOptions(MaxConcurrency: 8));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);
        AuditChain.Verify(outcome.RunId, harness.Journal.Events).IsIntact.ShouldBeTrue();

        harness.Journal.Events
            .Select(@event => @event.Sequence)
            .ShouldBe(Enumerable.Range(1, harness.Journal.Events.Length).Select(value => (long)value));
    }

    // ---- recording ----

    [Fact]
    public async Task The_run_record_stamps_the_engine_build_that_executed_it()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        await harness.RunAsync();

        RunPlannedPayload payload = harness
            .EventsOfKind(RunEventKind.RunPlanned)
            .Single()
            .Payload<RunPlannedPayload>();

        payload.EngineFingerprint.ShouldStartWith("mandate/");
        payload.PlannedNodes.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Run_level_facts_are_seeded_and_attributed_before_any_stage_runs()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        RunOutcome outcome = await harness.RunAsync(ScenarioKind.Brownfield, hasExistingCode: true);

        outcome.State.Context.Latest(WorkflowContextKeys.Scenario)!.Value.ShouldBe("brownfield");
        outcome.State.Context.Latest(WorkflowContextKeys.HasExistingCode)!.Value.ShouldBe("true");
        outcome.State.Context.Latest(WorkflowContextKeys.InitiatedBy)!.Value.ShouldBe("human:tester");
        outcome.State.Context.Latest(WorkflowContextKeys.Scenario)!.ProducedBy
            .ShouldBe(Actor.Engine);
    }

    [Fact]
    public async Task Every_event_names_an_actor()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        await harness.RunAsync();

        harness.Journal.Events.ShouldAllBe(@event => !@event.Actor.IsEmpty);
    }

    [Fact]
    public async Task Stage_output_is_attributed_to_the_agent_and_scheduling_to_the_engine()
    {
        EngineHarness harness = EngineHarness.For(Chain());

        await harness.RunAsync();

        harness.EventsOfKind(RunEventKind.ArtifactProduced)
            .ShouldAllBe(@event => @event.Actor.Kind == ActorKind.Agent);

        harness.EventsOfKind(RunEventKind.NodeStateChanged)
            .Where(@event => @event.Payload<NodeStateChangedPayload>().To == "Succeeded")
            .ShouldAllBe(@event => @event.Actor.Kind == ActorKind.Agent);

        harness.EventsOfKind(RunEventKind.EntryGateEvaluated)
            .ShouldAllBe(@event => @event.Actor == Actor.Engine);
    }
}
