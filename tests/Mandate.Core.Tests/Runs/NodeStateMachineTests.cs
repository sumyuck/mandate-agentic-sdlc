using System.Collections.Immutable;
using Mandate.Core.Runs;

namespace Mandate.Core.Tests.Runs;

/// <summary>
/// The transition table is the engine's governance contract. These tests assert the
/// control properties the brief requires, not merely that the table is self-consistent.
/// </summary>
public sealed class NodeStateMachineTests
{
    [Theory]
    [InlineData(NodeState.Pending, NodeState.Ready)]
    [InlineData(NodeState.Ready, NodeState.Running)]
    [InlineData(NodeState.Running, NodeState.Succeeded)]
    [InlineData(NodeState.Running, NodeState.Failed)]
    [InlineData(NodeState.Failed, NodeState.Ready)]
    [InlineData(NodeState.Failed, NodeState.Compensating)]
    [InlineData(NodeState.Compensating, NodeState.RolledBack)]
    [InlineData(NodeState.RolledBack, NodeState.Pending)]
    [InlineData(NodeState.Succeeded, NodeState.Invalidated)]
    [InlineData(NodeState.Invalidated, NodeState.Pending)]
    [InlineData(NodeState.AwaitingApproval, NodeState.Running)]
    [InlineData(NodeState.AwaitingApproval, NodeState.Blocked)]
    [InlineData(NodeState.Blocked, NodeState.Ready)]
    public void Permitted_transitions_are_applied(NodeState from, NodeState to) =>
        NodeStateMachine.Transition(from, to).ShouldBe(to);

    [Theory]
    [InlineData(NodeState.Pending, NodeState.Running)]
    [InlineData(NodeState.Pending, NodeState.Succeeded)]
    [InlineData(NodeState.Ready, NodeState.Succeeded)]
    [InlineData(NodeState.Succeeded, NodeState.Running)]
    [InlineData(NodeState.Succeeded, NodeState.Failed)]
    [InlineData(NodeState.RolledBack, NodeState.Succeeded)]
    [InlineData(NodeState.Compensating, NodeState.Succeeded)]
    public void Forbidden_transitions_throw(NodeState from, NodeState to) =>
        Should.Throw<InvalidNodeTransitionException>(() => NodeStateMachine.Transition(from, to));

    [Fact]
    public void A_node_cannot_skip_execution_and_report_success()
    {
        // The central integrity property, stated transitively because an approved node
        // completes from AwaitingApproval rather than by running a second time. Every path
        // into Succeeded must still pass through Running.
        ImmutableHashSet<NodeState> intoSucceeded =
        [
            .. Enum.GetValues<NodeState>()
                .Where(state => NodeStateMachine.CanTransition(state, NodeState.Succeeded)),
        ];

        intoSucceeded.ShouldBe([NodeState.Running, NodeState.AwaitingApproval], ignoreOrder: true);

        // ...and the only way into AwaitingApproval is from Running, so there is no route to
        // success that avoids executing the stage.
        Enum.GetValues<NodeState>()
            .Where(state => NodeStateMachine.CanTransition(state, NodeState.AwaitingApproval))
            .ShouldBe([NodeState.Running]);
    }

    [Fact]
    public void A_stage_cannot_be_parked_for_approval_before_it_has_run()
    {
        // A stage that must not start until someone says so expresses that as an entry gate,
        // which holds it Pending. Parking a Ready node would open a path to success that
        // never executed anything.
        NodeStateMachine.CanTransition(NodeState.Ready, NodeState.AwaitingApproval).ShouldBeFalse();
        NodeStateMachine.CanTransition(NodeState.Pending, NodeState.AwaitingApproval).ShouldBeFalse();
    }

    [Fact]
    public void A_policy_block_cannot_be_cleared_into_execution_directly()
    {
        // Clearing a block requires a human act that returns the node to Ready, where the
        // entry gate and policy are evaluated again. Blocked -> Running would let the engine
        // walk past its own guardrail.
        NodeStateMachine.CanTransition(NodeState.Blocked, NodeState.Running).ShouldBeFalse();
        NodeStateMachine.CanTransition(NodeState.Blocked, NodeState.Ready).ShouldBeTrue();
    }

    [Fact]
    public void Work_awaiting_approval_can_be_invalidated_by_an_upstream_change()
    {
        // Approving stale work must be impossible: if an input changes while a human is
        // deliberating, the parked node is invalidated rather than approved.
        NodeStateMachine.CanTransition(NodeState.AwaitingApproval, NodeState.Invalidated).ShouldBeTrue();
    }

    [Fact]
    public void Retry_returns_through_ready_so_gates_are_re_evaluated()
    {
        NodeStateMachine.CanTransition(NodeState.Failed, NodeState.Running).ShouldBeFalse();
        NodeStateMachine.CanTransition(NodeState.Failed, NodeState.Ready).ShouldBeTrue();
    }

    [Fact]
    public void Cancelled_is_the_only_terminal_state()
    {
        NodeStateMachine.Terminal.ShouldBe([NodeState.Cancelled]);
        NodeStateMachine.SuccessorsOf(NodeState.Cancelled).ShouldBeEmpty();
    }

    [Fact]
    public void Nodes_with_outstanding_work_can_be_cancelled_by_a_safe_stop()
    {
        foreach (NodeState state in NodeStateMachine.Cancellable)
        {
            NodeStateMachine.CanTransition(state, NodeState.Cancelled).ShouldBeTrue(
                $"safe-stop must be able to cancel a node in {state}.");
        }
    }

    [Fact]
    public void A_settled_outcome_is_never_relabelled_by_a_stop()
    {
        // A node that succeeded did succeed, and a node that was skipped was skipped. Marking
        // either Cancelled because the run stopped later would make the audit log state
        // something untrue. The stop is recorded on the run, not on completed work.
        foreach (NodeState settled in NodeStateMachine.Settled)
        {
            NodeStateMachine.CanTransition(settled, NodeState.Cancelled).ShouldBeFalse(
                $"{settled} is a settled outcome and must not become Cancelled.");
        }
    }

    [Fact]
    public void Every_state_is_classified_as_settled_cancellable_terminal_or_in_compensation()
    {
        // Ensures a newly added state cannot quietly escape the cancellation rules.
        IEnumerable<NodeState> classified = NodeStateMachine.Settled
            .Union(NodeStateMachine.Cancellable)
            .Union(NodeStateMachine.Terminal)
            .Add(NodeState.Compensating)
            .Add(NodeState.Unknown);

        classified.ShouldBe(Enum.GetValues<NodeState>(), ignoreOrder: true);
    }

    [Fact]
    public void Compensation_runs_to_completion_rather_than_being_cancelled()
    {
        // Abandoning a half-applied rollback would leave the workspace in an unknown state,
        // which is worse than either outcome. Compensation may fail, but it may not be cut short.
        NodeStateMachine.CanTransition(NodeState.Compensating, NodeState.Cancelled).ShouldBeFalse();
        NodeStateMachine.SuccessorsOf(NodeState.Compensating)
            .ShouldBe([NodeState.RolledBack, NodeState.Failed], ignoreOrder: true);
    }

    [Fact]
    public void The_unknown_state_has_no_transitions_in_either_direction()
    {
        NodeStateMachine.SuccessorsOf(NodeState.Unknown).ShouldBeEmpty();

        foreach (NodeState state in Enum.GetValues<NodeState>())
        {
            NodeStateMachine.CanTransition(state, NodeState.Unknown).ShouldBeFalse();
        }
    }

    [Fact]
    public void Rejection_names_the_permitted_successors_so_the_error_is_actionable()
    {
        InvalidNodeTransitionException error = Should.Throw<InvalidNodeTransitionException>(
            () => NodeStateMachine.Transition(NodeState.Pending, NodeState.Succeeded));

        error.From.ShouldBe(NodeState.Pending);
        error.To.ShouldBe(NodeState.Succeeded);
        error.Message.ShouldContain("Ready");
    }

    [Theory]
    [InlineData(NodeState.Succeeded)]
    [InlineData(NodeState.Skipped)]
    [InlineData(NodeState.AwaitingApproval)]
    [InlineData(NodeState.Blocked)]
    [InlineData(NodeState.Failed)]
    [InlineData(NodeState.RolledBack)]
    public void Any_settled_verdict_can_be_invalidated_when_its_inputs_change(NodeState from)
    {
        // One rule, uniformly. A failure is a verdict about particular inputs just as a
        // success is; when the inputs change, both are equally stale. The gap here used to
        // be Failed and RolledBack, and it crashed the engine the first time a loop-back
        // re-ran an upstream stage and cascaded back onto the stage that had failed.
        NodeStateMachine.CanTransition(from, NodeState.Invalidated).ShouldBeTrue();
    }

    [Fact]
    public void A_cancelled_node_stays_cancelled()
    {
        NodeStateMachine.CanTransition(NodeState.Cancelled, NodeState.Invalidated).ShouldBeFalse();
        NodeStateMachine.SuccessorsOf(NodeState.Cancelled).ShouldBeEmpty();
    }

    [Fact]
    public void Invalidation_always_leads_back_into_the_plan()
    {
        NodeStateMachine.CanTransition(NodeState.Invalidated, NodeState.Pending).ShouldBeTrue();
    }
}
