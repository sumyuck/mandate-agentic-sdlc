using System.Collections.Immutable;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;

namespace Mandate.Orchestrator.Execution;

/// <summary>Whether an inbound path has resolved, and how.</summary>
internal enum PathState
{
    /// <summary>The source has not settled yet, or its guard cannot be judged.</summary>
    Waiting,

    /// <summary>The source succeeded and, if guarded, the guard held.</summary>
    Satisfied,

    /// <summary>This path will never carry control: the guard was false, or the source was skipped.</summary>
    Excluded,
}

/// <summary>What the scheduler should do with a node.</summary>
internal enum Eligibility
{
    /// <summary>Not yet: some inbound path is unresolved.</summary>
    Waiting,

    /// <summary>The join condition is met; the node may be gated and run.</summary>
    Eligible,

    /// <summary>The join condition can never be met; the node will not run.</summary>
    Skipped,
}

/// <summary>
/// Decides whether a node's join condition is met, can never be met, or is still open.
/// </summary>
/// <remarks>
/// <para>
/// This is where join policy and conditional paths meet, and it is the one piece of the
/// scheduler that is easy to get quietly wrong. Two failure modes matter.
/// </para>
/// <para>
/// A node joining on <c>all</c> whose inbound paths include an excluded one can never be
/// satisfied — so it is <see cref="Eligibility.Skipped"/>, not left waiting forever. A node
/// joining on <c>any</c> becomes eligible on the first satisfied path, which is what lets the
/// design stage accept either the greenfield or the brownfield route without waiting for the
/// one the guard excluded.
/// </para>
/// <para>
/// Exclusion propagates: a path out of a skipped node is itself excluded. Without that, one
/// skipped branch would strand everything downstream of it in <c>Waiting</c> and the run would
/// stall with no explanation.
/// </para>
/// </remarks>
internal static class NodeEligibility
{
    public static Eligibility Assess(
        WorkflowNode node,
        ImmutableArray<(WorkflowEdge Edge, PathState State)> inbound)
    {
        if (inbound.IsEmpty)
        {
            // An entry node has nothing to wait for.
            return Eligibility.Eligible;
        }

        int satisfied = inbound.Count(path => path.State == PathState.Satisfied);
        int excluded = inbound.Count(path => path.State == PathState.Excluded);
        int waiting = inbound.Length - satisfied - excluded;

        return node.Join switch
        {
            JoinPolicy.All => AssessAll(satisfied, excluded, waiting, inbound.Length),
            JoinPolicy.Any => AssessQuorum(satisfied, waiting, required: 1),
            JoinPolicy.Quorum => AssessQuorum(satisfied, waiting, Math.Max(1, node.QuorumSize)),
            _ => throw new InvalidOperationException(
                $"Node '{node.Id}' has no join policy; validation should have rejected it."),
        };
    }

    private static Eligibility AssessAll(int satisfied, int excluded, int waiting, int total)
    {
        if (satisfied == total)
        {
            return Eligibility.Eligible;
        }

        // One excluded path makes 'all' unsatisfiable, so the node is skipped rather than
        // waiting on a path that will never arrive.
        return excluded > 0 && waiting == 0 ? Eligibility.Skipped : Eligibility.Waiting;
    }

    private static Eligibility AssessQuorum(int satisfied, int waiting, int required)
    {
        if (satisfied >= required)
        {
            return Eligibility.Eligible;
        }

        return satisfied + waiting < required ? Eligibility.Skipped : Eligibility.Waiting;
    }

    /// <summary>Classifies an inbound path given the state of its source and its guard verdict.</summary>
    public static PathState Classify(NodeState sourceState, bool? guardTaken) => sourceState switch
    {
        // Exclusion propagates so that one skipped branch cannot strand the graph.
        NodeState.Skipped or NodeState.Cancelled => PathState.Excluded,

        NodeState.Succeeded => guardTaken switch
        {
            false => PathState.Excluded,
            true or null => PathState.Satisfied,
        },

        _ => PathState.Waiting,
    };
}
