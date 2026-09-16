using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Tests.Support;

namespace Mandate.Orchestrator.Tests.Execution;

/// <summary>
/// Closing the human loop: a parked run continues from its own log once a decision arrives.
/// </summary>
public sealed class ResumeTests
{
    private static NodeId Id(string value) => NodeId.Parse(value);

    private static EngineHarness Parked() =>
        EngineHarness.For(WorkflowFixtures.RequiresApproval());

    [Fact]
    public async Task An_approved_stage_completes_without_being_run_again()
    {
        // Re-executing on the strength of a signature would mean the human approved something
        // other than what ships.
        EngineHarness harness = Parked();

        RunOutcome parked = await harness.RunAsync();
        parked.Status.ShouldBe(RunStatus.AwaitingApproval);

        int attemptsBefore = parked.State.Nodes[Id("signed-off")].AttemptsMade;

        await harness.DecideAsync("tech-lead", "alex", granted: true, "Looks right.");
        RunOutcome resumed = await harness.ResumeAsync();

        resumed.Status.ShouldBe(RunStatus.Succeeded);
        EngineHarness.StateOf(resumed, "signed-off").ShouldBe(NodeState.Succeeded);
        resumed.State.Nodes[Id("signed-off")].AttemptsMade.ShouldBe(attemptsBefore);
        resumed.State.Nodes[Id("signed-off")].Detail!.ShouldContain("without re-running");
    }

    [Fact]
    public async Task A_refused_stage_is_blocked_rather_than_left_waiting()
    {
        // "Nobody has looked at this" and "somebody looked and said no" need different
        // handling; collapsing them would leave the run waiting on a decision already made.
        EngineHarness harness = Parked();

        await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: false, "Not this design.");

        RunOutcome resumed = await harness.ResumeAsync();

        resumed.Status.ShouldBe(RunStatus.Blocked);
        EngineHarness.StateOf(resumed, "signed-off").ShouldBe(NodeState.Blocked);
    }

    [Fact]
    public async Task Resuming_without_a_decision_leaves_the_run_parked()
    {
        // Resuming is not the same as approving.
        EngineHarness harness = Parked();

        await harness.RunAsync();
        RunOutcome resumed = await harness.ResumeAsync();

        resumed.Status.ShouldBe(RunStatus.AwaitingApproval);
        EngineHarness.StateOf(resumed, "signed-off").ShouldBe(NodeState.AwaitingApproval);
    }

    [Fact]
    public async Task A_granted_approval_supersedes_an_earlier_refusal()
    {
        // A human who changes their mind should not have to contend with their own previous
        // answer.
        EngineHarness harness = Parked();

        await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: false, "Not yet.");
        await harness.DecideAsync("tech-lead", "alex", granted: true, "Revised and fine.");

        RunOutcome resumed = await harness.ResumeAsync();

        resumed.Status.ShouldBe(RunStatus.Succeeded);
    }

    [Fact]
    public async Task A_resumed_run_continues_into_the_work_the_approval_unblocked()
    {
        WorkflowDefinition gated = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node(
                    "design",
                    exitGate:
                    [
                        new GateCondition("artifact-exists", "source-patch", "Produced."),
                        new GateCondition("approval-held", "tech-lead", "Signed off."),
                    ],
                    approvals:
                    [
                        new ApprovalRequirement("tech-lead", "High impact.", true),
                    ]),
                WorkflowFixtures.Node("build"),
            ],
            [WorkflowEdge.Forward(Id("design"), Id("build"))]);

        EngineHarness harness = EngineHarness.For(gated);

        RunOutcome parked = await harness.RunAsync();
        EngineHarness.StateOf(parked, "build").ShouldBe(NodeState.Pending);

        await harness.DecideAsync("tech-lead", "alex", granted: true);
        RunOutcome resumed = await harness.ResumeAsync();

        resumed.Status.ShouldBe(RunStatus.Succeeded);
        EngineHarness.StateOf(resumed, "build").ShouldBe(NodeState.Succeeded);
    }

    [Fact]
    public async Task A_resume_is_recorded_as_a_resume()
    {
        EngineHarness harness = Parked();

        await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: true);
        await harness.ResumeAsync();

        ImmutableArray<RunStartedPayload> starts =
        [
            .. harness.EventsOfKind(RunEventKind.RunStarted)
                .Select(@event => @event.Payload<RunStartedPayload>()),
        ];

        starts.Length.ShouldBe(2);
        starts[0].Resumed.ShouldBeFalse();
        starts[1].Resumed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_resumed_run_does_not_re_plan_or_re_seed_itself()
    {
        // The plan and the run-level facts are part of the original record; writing them
        // again would let a resume quietly restate what the run was asked to do.
        EngineHarness harness = Parked();

        await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: true);
        await harness.ResumeAsync();

        harness.EventsOfKind(RunEventKind.RunPlanned).ShouldHaveSingleItem();

        harness.EventsOfKind(RunEventKind.ContextFactAdded)
            .Count(@event =>
                @event.Payload<ContextFactAddedPayload>().Key == WorkflowContextKeys.Scenario)
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_chain_stays_intact_across_the_stop_and_the_resume()
    {
        EngineHarness harness = Parked();

        RunOutcome parked = await harness.RunAsync();
        await harness.DecideAsync("tech-lead", "alex", granted: true);
        await harness.ResumeAsync();

        AuditChain.Verify(parked.RunId, harness.Journal.Events).IsIntact.ShouldBeTrue();
    }

    // ---- reconstructing the run from its log ----

    [Fact]
    public async Task The_original_request_is_read_back_from_the_log()
    {
        EngineHarness harness = Parked();

        RunOutcome parked = await harness.RunAsync(
            ScenarioKind.Brownfield, hasExistingCode: true, request: "Add click analytics.");

        ResumePoint resume = ResumePoint.FromEvents(parked.RunId, harness.Journal.Events);

        resume.Request.Request.ShouldBe("Add click analytics.");
        resume.Request.Scenario.ShouldBe(ScenarioKind.Brownfield);
        resume.Request.HasExistingCode.ShouldBeTrue();
        resume.Request.InitiatedBy.ShouldBe(Actor.Human("tester"));
    }

    [Fact]
    public void A_run_with_no_events_cannot_be_resumed()
    {
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResumePoint.FromEvents(
                RunId.New(DateTimeOffset.UnixEpoch, "none01"), []));

        error.Message.ShouldContain("nothing to resume");
    }

    [Fact]
    public async Task A_log_with_no_plan_event_is_refused_rather_than_guessed_at()
    {
        EngineHarness harness = Parked();

        RunOutcome parked = await harness.RunAsync();

        ImmutableArray<RunEvent> withoutPlan =
            [.. harness.Journal.Events.Where(@event => @event.Kind != RunEventKind.RunPlanned)];

        Should.Throw<InvalidOperationException>(
                () => ResumePoint.FromEvents(parked.RunId, withoutPlan))
            .Message.ShouldContain("Refusing to guess");
    }

    [Fact]
    public async Task A_finished_run_reports_no_outstanding_work()
    {
        EngineHarness harness = EngineHarness.For(
            WorkflowFixtures.Definition([WorkflowFixtures.Node("only")], []));

        RunOutcome finished = await harness.RunAsync();
        finished.Status.ShouldBe(RunStatus.Succeeded);

        ResumePoint.FromEvents(finished.RunId, harness.Journal.Events)
            .HasOutstandingWork.ShouldBeFalse();
    }

    [Fact]
    public async Task A_parked_run_reports_outstanding_work()
    {
        EngineHarness harness = Parked();

        RunOutcome parked = await harness.RunAsync();

        ResumePoint.FromEvents(parked.RunId, harness.Journal.Events)
            .HasOutstandingWork.ShouldBeTrue();
    }
}
