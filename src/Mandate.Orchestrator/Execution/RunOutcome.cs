using System.Collections.Immutable;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;

namespace Mandate.Orchestrator.Execution;

/// <summary>The result of executing a run.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Status">Where it ended up.</param>
/// <param name="Reason">Why, in reviewer-facing terms.</param>
/// <param name="State">The final projected state, including every node's outcome.</param>
/// <param name="EventCount">How many audit events the run produced.</param>
public sealed record RunOutcome(
    RunId RunId,
    RunStatus Status,
    string Reason,
    RunState State,
    long EventCount)
{
    /// <summary>Nodes grouped by the state they ended in.</summary>
    public ImmutableDictionary<NodeState, ImmutableArray<NodeId>> ByState =>
        State.Nodes.Values
            .GroupBy(node => node.State)
            .ToImmutableDictionary(
                group => group.Key,
                group => group
                    .Select(node => node.Id)
                    .OrderBy(id => id.Value, StringComparer.Ordinal)
                    .ToImmutableArray());

    /// <summary>True when the run finished its work.</summary>
    public bool Succeeded => Status == RunStatus.Succeeded;

    /// <summary>True when the run is waiting on a human and can be resumed.</summary>
    public bool IsWaitingOnHuman =>
        Status is RunStatus.AwaitingApproval or RunStatus.Blocked;
}
