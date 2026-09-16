using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Helmsman.Core.Runs;

/// <summary>
/// The single authority on how a node may move between states.
/// </summary>
/// <remarks>
/// <para>
/// Every state change in the engine goes through <see cref="Transition"/>. Nothing assigns a
/// node state directly. That makes the governance model auditable in one place: the table
/// below is the complete, reviewable statement of what the orchestrator is allowed to do to
/// a node, and an attempt to do anything else throws rather than being silently recorded.
/// </para>
/// <para>
/// The non-obvious edges are the ones the brief's control requirements demand:
/// <list type="bullet">
/// <item><description><c>Failed → Ready</c> is a bounded retry, not an unbounded loop; the budget lives in the node's retry policy.</description></item>
/// <item><description><c>Succeeded → Invalidated</c> is dynamic re-planning: an accepted result stops being valid when an input changes.</description></item>
/// <item><description><c>AwaitingApproval → Invalidated</c> covers an upstream change landing while a human is still deliberating. Approving stale work must be impossible.</description></item>
/// <item><description><c>Blocked → Ready</c> requires a human act (a waiver or a fix); the engine can never clear its own policy block.</description></item>
/// </list>
/// </para>
/// <para>
/// Cancellation follows one rule: a safe stop or an operator cancellation may only cancel a
/// node with outstanding work. A node that already reached a settled outcome —
/// <see cref="NodeState.Succeeded"/> or <see cref="NodeState.Skipped"/> — keeps that outcome,
/// because it is a true statement about what happened. The fact that the run was stopped is
/// recorded on the run, as <see cref="RunStatus.SafeStopped"/> or
/// <see cref="RunStatus.Cancelled"/>, not by rewriting the history of work that completed.
/// </para>
/// </remarks>
public static class NodeStateMachine
{
    private static readonly FrozenDictionary<NodeState, ImmutableHashSet<NodeState>> Allowed =
        new Dictionary<NodeState, ImmutableHashSet<NodeState>>
        {
            [NodeState.Pending] =
            [
                NodeState.Ready, NodeState.Blocked, NodeState.Skipped,
                NodeState.Cancelled, NodeState.Invalidated,
            ],
            [NodeState.Ready] =
            [
                NodeState.Running, NodeState.AwaitingApproval, NodeState.Blocked,
                NodeState.Skipped, NodeState.Cancelled, NodeState.Invalidated,
            ],
            [NodeState.Running] =
            [
                NodeState.Succeeded, NodeState.Failed, NodeState.AwaitingApproval,
                NodeState.Blocked, NodeState.Cancelled,
            ],
            [NodeState.AwaitingApproval] =
            [
                NodeState.Running, NodeState.Blocked, NodeState.Cancelled, NodeState.Invalidated,
            ],
            [NodeState.Blocked] =
            [
                NodeState.Ready, NodeState.Compensating, NodeState.Cancelled, NodeState.Invalidated,
            ],
            [NodeState.Failed] =
            [
                NodeState.Ready, NodeState.Compensating, NodeState.Blocked, NodeState.Cancelled,
            ],
            [NodeState.Succeeded] =
            [
                NodeState.Invalidated, NodeState.Compensating,
            ],
            [NodeState.Compensating] =
            [
                NodeState.RolledBack, NodeState.Failed,
            ],
            [NodeState.RolledBack] =
            [
                NodeState.Pending, NodeState.Cancelled,
            ],
            [NodeState.Skipped] =
            [
                NodeState.Invalidated,
            ],
            [NodeState.Invalidated] =
            [
                NodeState.Pending, NodeState.Cancelled,
            ],
            [NodeState.Cancelled] = [],
        }.ToFrozenDictionary();

    /// <summary>States from which a run can make no further progress on this node.</summary>
    public static ImmutableHashSet<NodeState> Terminal { get; } = [NodeState.Cancelled];

    /// <summary>States in which a node is waiting on a human rather than on the engine.</summary>
    public static ImmutableHashSet<NodeState> AwaitingHuman { get; } =
        [NodeState.AwaitingApproval, NodeState.Blocked];

    /// <summary>States in which a node has produced accepted output.</summary>
    public static ImmutableHashSet<NodeState> Accepted { get; } = [NodeState.Succeeded];

    /// <summary>
    /// States representing a settled outcome, which a stop or cancellation must not relabel.
    /// </summary>
    public static ImmutableHashSet<NodeState> Settled { get; } =
        [NodeState.Succeeded, NodeState.Skipped];

    /// <summary>
    /// States with outstanding work, which a safe stop or cancellation may cancel.
    /// </summary>
    public static ImmutableHashSet<NodeState> Cancellable { get; } =
    [
        NodeState.Pending, NodeState.Ready, NodeState.Running, NodeState.AwaitingApproval,
        NodeState.Blocked, NodeState.Failed, NodeState.RolledBack, NodeState.Invalidated,
    ];

    /// <summary>True when <paramref name="to"/> is a permitted successor of <paramref name="from"/>.</summary>
    public static bool CanTransition(NodeState from, NodeState to) =>
        Allowed.TryGetValue(from, out ImmutableHashSet<NodeState>? successors) && successors.Contains(to);

    /// <summary>All permitted successors of <paramref name="from"/>.</summary>
    public static ImmutableHashSet<NodeState> SuccessorsOf(NodeState from) =>
        Allowed.TryGetValue(from, out ImmutableHashSet<NodeState>? successors) ? successors : [];

    /// <summary>
    /// Applies a transition, or throws if the transition is not permitted.
    /// </summary>
    /// <exception cref="InvalidNodeTransitionException">The transition is not in the table.</exception>
    public static NodeState Transition(NodeState from, NodeState to) =>
        CanTransition(from, to) ? to : throw new InvalidNodeTransitionException(from, to);
}

/// <summary>Raised when the engine attempts a state change the governance model forbids.</summary>
public sealed class InvalidNodeTransitionException : InvalidOperationException
{
    /// <summary>Creates the exception for a rejected transition.</summary>
    public InvalidNodeTransitionException(NodeState from, NodeState to)
        : base($"A node cannot move from {from} to {to}. Permitted from {from}: "
               + $"{FormatSuccessors(from)}.")
    {
        From = from;
        To = to;
    }

    /// <summary>The state the node was in.</summary>
    public NodeState From { get; }

    /// <summary>The state that was rejected.</summary>
    public NodeState To { get; }

    private static string FormatSuccessors(NodeState from)
    {
        ImmutableHashSet<NodeState> successors = NodeStateMachine.SuccessorsOf(from);

        return successors.IsEmpty
            ? "none (terminal)"
            : string.Join(", ", successors.Select(state => state.ToString()).Order(StringComparer.Ordinal));
    }
}
