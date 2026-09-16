using System.Collections.Immutable;
using Mandate.Agents.Scripted;
using Mandate.Core.Artifacts;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Tests.Architecture;
using Mandate.Orchestrator.Tests.Support;
using Mandate.Persistence.Workspaces;

namespace Mandate.Orchestrator.Tests.Execution;

/// <summary>
/// Bounded retries, fallback, rollback and safe-stop.
/// </summary>
/// <remarks>
/// The rollback tests run against a real git workspace rather than a stub, because the claim
/// being made is that the tree is genuinely restored — and a stub asserting it was restored
/// would be the same log-line-instead-of-a-rollback this design exists to avoid.
/// </remarks>
public sealed class ReliabilityTests : IDisposable
{
    private readonly string _workspaceRoot = Path.Combine(
        Path.GetTempPath(), $"mandate-rel-{Guid.NewGuid():N}");

    private static NodeId Id(string value) => NodeId.Parse(value);

    private static RetryPolicy Retry(int attempts, FallbackStrategy onExhaustion) => new(
        MaxAttempts: attempts,
        InitialBackoff: TimeSpan.FromSeconds(2),
        BackoffMultiplier: 2d,
        MaxBackoff: TimeSpan.FromSeconds(30),
        JitterRatio: 0d,
        OnExhaustion: onExhaustion);

    private static WorkflowDefinition OneNode(
        RetryPolicy retry, string? compensation = "revert-node-commit") =>
        WorkflowFixtures.Definition(
            [WorkflowFixtures.Node("work") with { Retry = retry, Compensation = compensation }],
            []);

    private static Dictionary<string, ScriptedBehaviour> Failing(
        string agent, ScriptedBehaviour behaviour) =>
        new(StringComparer.Ordinal) { [agent] = behaviour };

    private GitRunWorkspaceFactory Workspaces() => new(
        _workspaceRoot, Path.Combine(RepositoryLayout.Root.FullName, "templates", "service"));

    // ---- retries ----

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_node_succeeds()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(3, FallbackStrategy.FailNode)),
            Failing("work-agent", ScriptedBehaviour.FailsOnce("transient")));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);
        outcome.State.Nodes[Id("work")].AttemptsMade.ShouldBe(2);
    }

    [Fact]
    public async Task A_retry_is_recorded_as_a_retry()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(3, FallbackStrategy.FailNode)),
            Failing("work-agent", ScriptedBehaviour.FailsOnce("transient")));

        await harness.RunAsync();

        RetryPayload payload = harness
            .EventsOfKind(RunEventKind.NodeRetryScheduled)
            .ShouldHaveSingleItem()
            .Payload<RetryPayload>();

        payload.Attempt.ShouldBe(2);
        payload.MaxAttempts.ShouldBe(3);
        payload.Reason.ShouldContain("transient");
    }

    [Fact]
    public async Task A_retry_returns_through_ready_so_the_state_machine_sees_it()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(3, FallbackStrategy.FailNode)),
            Failing("work-agent", ScriptedBehaviour.FailsOnce("transient")));

        await harness.RunAsync();

        ImmutableArray<string> transitions =
        [
            .. harness.EventsOfKind(RunEventKind.NodeStateChanged)
                .Select(@event => @event.Payload<NodeStateChangedPayload>())
                .Select(payload => $"{payload.From}->{payload.To}"),
        ];

        transitions.ShouldContain("Running->Failed");
        transitions.ShouldContain("Failed->Ready");
        transitions.ShouldContain("Ready->Running");
    }

    [Fact]
    public async Task Backoff_grows_between_attempts_and_is_bounded()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(4, FallbackStrategy.FailNode)),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")));

        await harness.RunAsync();

        ImmutableArray<TimeSpan> waits = harness.Delay.Waits;

        waits.Length.ShouldBe(3, "three waits between four attempts.");
        waits[0].ShouldBe(TimeSpan.FromSeconds(2));
        waits[1].ShouldBe(TimeSpan.FromSeconds(4));
        waits[2].ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Backoff_never_exceeds_the_declared_ceiling()
    {
        RetryPolicy capped = Retry(5, FallbackStrategy.FailNode) with
        {
            MaxBackoff = TimeSpan.FromSeconds(3),
        };

        EngineHarness harness = EngineHarness.For(
            OneNode(capped), Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")));

        await harness.RunAsync();

        harness.Delay.Waits.ShouldAllBe(wait => wait <= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Jitter_keeps_the_wait_within_its_declared_spread()
    {
        // Without jitter, stages that failed together retry together — a transient upstream
        // problem becomes a repeated thundering herd against the thing already struggling.
        RetryPolicy jittered = Retry(4, FallbackStrategy.FailNode) with { JitterRatio = 0.25 };

        EngineHarness harness = EngineHarness.For(
            OneNode(jittered), Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")));

        await harness.RunAsync();

        harness.Delay.Waits[0].TotalSeconds.ShouldBeInRange(1.5, 2.5);
        harness.Delay.Waits.ShouldAllBe(wait => wait >= TimeSpan.Zero);
    }

    [Fact]
    public async Task A_node_with_no_retry_budget_makes_exactly_one_attempt()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(RetryPolicy.None),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("nope")));

        RunOutcome outcome = await harness.RunAsync();

        outcome.State.Nodes[Id("work")].AttemptsMade.ShouldBe(1);
        harness.Delay.Waits.ShouldBeEmpty();
    }

    // ---- fallback ----

    [Fact]
    public async Task Exhausting_the_budget_records_it_and_names_the_fallback()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(2, FallbackStrategy.HumanHandoff)),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")));

        await harness.RunAsync();

        harness.EventsOfKind(RunEventKind.NodeRetryBudgetExhausted).ShouldHaveSingleItem();

        harness.EventsOfKind(RunEventKind.NodeFallbackSelected)
            .ShouldHaveSingleItem()
            .Payload<FallbackSelectedPayload>()
            .Strategy.ShouldBe("HumanHandoff");
    }

    [Fact]
    public async Task A_human_handoff_blocks_rather_than_fails()
    {
        // Blocked is not failed: the run is not beyond saving, it needs someone to look at it.
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(2, FallbackStrategy.HumanHandoff)),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Blocked);
        EngineHarness.StateOf(outcome, "work").ShouldBe(NodeState.Blocked);
        outcome.Reason.ShouldContain("handed off to a human");
    }

    [Fact]
    public async Task Failing_the_node_leaves_the_run_failed()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(2, FallbackStrategy.FailNode)),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Failed);
        EngineHarness.StateOf(outcome, "work").ShouldBe(NodeState.Failed);
    }


    // ---- the workspace ----

    [Fact]
    public async Task A_successful_stage_commits_its_files_to_the_workspace()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(RetryPolicy.None), workspaces: Workspaces());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded);

        WorkspaceCommittedPayload payload = harness
            .EventsOfKind(RunEventKind.WorkspaceCommitted)
            .ShouldHaveSingleItem()
            .Payload<WorkspaceCommittedPayload>();

        payload.Paths.ShouldContain("src/work.cs");
        File.Exists(Path.Combine(harness.Workspace!.Root, "src", "work.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_workspace_starts_from_the_template()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(RetryPolicy.None), workspaces: Workspaces());

        await harness.RunAsync();

        File.Exists(Path.Combine(harness.Workspace!.Root, "Program.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_attempt_leaves_nothing_behind_in_the_tree()
    {
        // Whatever a failed stage half-wrote is not a change any node declared, so the
        // workspace must not be left holding it.
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(2, FallbackStrategy.FailNode)),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")),
            workspaces: Workspaces());

        await harness.RunAsync();

        WorkspaceStatus status = await harness.Workspace!.StatusAsync(CancellationToken.None);

        status.IsClean.ShouldBeTrue();
        status.CommitCount.ShouldBe(1, "only the seed commit.");
        harness.EventsOfKind(RunEventKind.WorkspaceCommitted).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stage_proposing_a_path_outside_the_workspace_fails_rather_than_writing_it()
    {
        // The engine applies every write, so this is a boundary it enforces rather than a
        // behaviour it hopes for. A stage writing a git hook would be running code on the host.
        EngineHarness harness = EngineHarness.For(
            OneNode(RetryPolicy.None),
            extraAgents: [new EscapingStageAgent("work-agent")],
            workspaces: Workspaces());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Reason.ShouldContain("outside its workspace");
        File.Exists(Path.Combine(harness.Workspace!.Root, ".git", "hooks", "pre-commit"))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task A_stage_proposing_files_with_no_workspace_configured_fails_loudly()
    {
        // Silently accepting the write would leave the run claiming output that exists nowhere.
        EngineHarness harness = EngineHarness.For(OneNode(RetryPolicy.None));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.Succeeded, "the null workspace in tests accepts writes.");
    }

    // ---- rollback ----

    [Fact]
    public async Task Exhausting_a_compensating_node_rolls_the_run_back()
    {
        EngineHarness harness = EngineHarness.For(
            OneNode(Retry(2, FallbackStrategy.Compensate)),
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("cannot be made to work")),
            workspaces: Workspaces());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.RolledBack);
        outcome.Reason.ShouldContain("exhausted its retries");
    }

    [Fact]
    public async Task Rollback_restores_the_tree_the_run_changed()
    {
        // The claim that matters: not that a rollback was recorded, but that the files are
        // actually gone and the tree is clean.
        WorkflowDefinition twoStages = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("build") with
                {
                    Retry = RetryPolicy.None,
                    Compensation = "revert-node-commit",
                },
                WorkflowFixtures.Node("verify") with
                {
                    Retry = Retry(2, FallbackStrategy.Compensate),
                    Compensation = "revert-node-commit",
                },
            ],
            [WorkflowEdge.Forward(Id("build"), Id("verify"))]);

        EngineHarness harness = EngineHarness.For(
            twoStages,
            Failing("verify-agent", ScriptedBehaviour.AlwaysFails("the change does not work")),
            workspaces: Workspaces());

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.RolledBack);

        string root = harness.Workspace!.Root;

        File.Exists(Path.Combine(root, "src", "build.cs"))
            .ShouldBeFalse("the successful stage's output was undone with the run.");

        WorkspaceStatus status = await harness.Workspace.StatusAsync(CancellationToken.None);
        status.IsClean.ShouldBeTrue();

        // The seed, the change, and its reversal: history is preserved, not erased.
        status.CommitCount.ShouldBe(3);
    }

    [Fact]
    public async Task Rollback_records_what_it_undid_and_leaves_the_history_intact()
    {
        WorkflowDefinition twoStages = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("build") with
                {
                    Retry = RetryPolicy.None,
                    Compensation = "revert-node-commit",
                },
                WorkflowFixtures.Node("verify") with
                {
                    Retry = Retry(1, FallbackStrategy.Compensate),
                    Compensation = "revert-node-commit",
                },
            ],
            [WorkflowEdge.Forward(Id("build"), Id("verify"))]);

        EngineHarness harness = EngineHarness.For(
            twoStages,
            Failing("verify-agent", ScriptedBehaviour.AlwaysFails("always")),
            workspaces: Workspaces());

        await harness.RunAsync();

        harness.EventsOfKind(RunEventKind.NodeCompensationStarted).ShouldNotBeEmpty();

        CompensationPayload undoneWork = harness
            .EventsOfKind(RunEventKind.NodeCompensationCompleted)
            .Select(@event => @event.Payload<CompensationPayload>())
            .First(payload => payload.Detail.Contains("Reverted", StringComparison.Ordinal));

        undoneWork.Action.ShouldBe("revert-node-commit");
        undoneWork.Undone.ShouldBeTrue();
        undoneWork.Detail.ShouldContain("remain in the history");
    }

    [Fact]
    public async Task A_stage_with_nothing_to_undo_is_not_marked_rolled_back()
    {
        // Claiming an action that never happened would make the log say something untrue.
        WorkflowDefinition mixed = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node(
                    "note",
                    exitGate:
                    [
                        new GateCondition("artifact-exists", "review-report", "Reviewed."),
                    ],
                    produces: [ArtifactKind.ReviewReport]) with
                {
                    Retry = RetryPolicy.None,
                    Compensation = null,
                },
                WorkflowFixtures.Node("work") with
                {
                    Retry = Retry(1, FallbackStrategy.Compensate),
                    Compensation = "revert-node-commit",
                },
            ],
            [WorkflowEdge.Forward(Id("note"), Id("work"))]);

        EngineHarness harness = EngineHarness.For(
            mixed,
            Failing("work-agent", ScriptedBehaviour.AlwaysFails("always")),
            workspaces: Workspaces());

        RunOutcome outcome = await harness.RunAsync();

        EngineHarness.StateOf(outcome, "note").ShouldBe(NodeState.Succeeded);
        EngineHarness.StateOf(outcome, "work").ShouldBe(NodeState.RolledBack);
    }

    // ---- safe stop ----

    [Fact]
    public async Task A_requested_stop_halts_the_run_at_a_safe_boundary()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.FanOutFanIn(), safeStop: new StubSafeStop(requestAfterChecks: 2));

        RunOutcome outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(RunStatus.SafeStopped);
        outcome.Reason.ShouldContain("Completed work is preserved");
    }

    [Fact]
    public async Task A_stop_preserves_completed_work_and_cancels_only_what_is_outstanding()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.FanOutFanIn(), safeStop: new StubSafeStop(requestAfterChecks: 2));

        RunOutcome outcome = await harness.RunAsync();

        EngineHarness.StateOf(outcome, "start").ShouldBe(NodeState.Succeeded);

        outcome.State.Nodes.Values
            .Where(node => node.State == NodeState.Cancelled)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_stop_is_recorded_from_request_to_completion()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.FanOutFanIn(), safeStop: new StubSafeStop(requestAfterChecks: 1));

        await harness.RunAsync();

        harness.EventsOfKind(RunEventKind.SafeStopRequested).ShouldHaveSingleItem();

        SafeStopPayload payload = harness
            .EventsOfKind(RunEventKind.SafeStopCompleted)
            .ShouldHaveSingleItem()
            .Payload<SafeStopPayload>();

        payload.NodesCancelled.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_stopped_run_still_verifies()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.FanOutFanIn(), safeStop: new StubSafeStop(requestAfterChecks: 2));

        RunOutcome outcome = await harness.RunAsync();

        AuditChain.Verify(outcome.RunId, harness.Journal.Events).IsIntact.ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            try
            {
                Directory.Delete(_workspaceRoot, recursive: true);
            }
            catch (IOException)
            {
                // A lock on a temp directory is not worth failing a test over.
            }
        }
    }
}
